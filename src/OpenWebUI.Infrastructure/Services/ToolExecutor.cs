using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Resultado detalhado de uma execução de tool.</summary>
/// <param name="Text">Texto retornado ao modelo (já truncado/higienizado).</param>
/// <param name="Result">Payload estruturado opcional para renderização no cliente.</param>
public sealed record ToolExecutionOutcome(string Text, JsonElement? Result = null);

/// <summary>
/// Executa tools registradas via HTTP POST server-side (URL nunca exposta
/// ao cliente), em subprocess Python quando a tool tem
/// <see cref="Tool.Code"/> (convenção class Tools do upstream), em MCP
/// (URL virtual <c>mcp://</c>) ou in-process quando a URL é
/// <c>builtin://</c> (<see cref="BuiltinToolRegistry"/>,
/// SPEC-20261007-chat-agent-tools). Timeout de 30s nas HTTP; erro vira
/// resultado de erro para o modelo.
/// </summary>
public class ToolExecutor(
    AppDbContext db,
    IHttpClientFactory httpClientFactory,
    PythonToolExecutor pythonExecutor,
    McpClientService mcp,
    BuiltinToolRegistry builtins,
    ConfigService config,
    ToolOutputService? toolOutputs = null)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const int MaxOutputChars = 4000;

    /// <summary>
    /// Carrega tools habilitadas do usuário pelos ids selecionados — ids
    /// <c>builtin:*</c> resolvem no <see cref="BuiltinToolRegistry"/> sem
    /// tocar o banco.
    /// </summary>
    /// <param name="userId">Dono das tools.</param>
    /// <param name="toolIds">Ids selecionados no chat.</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<List<Tool>> LoadEnabledAsync(
        string userId, IReadOnlyList<string> toolIds, CancellationToken ct = default)
    {
        if (toolIds.Count == 0)
        {
            return [];
        }

        // Gate admin das tools de contexto (SPEC-20261013-agent-context-
        // management RF-005): com contexttools.enabled=false os ids nem
        // chegam ao anúncio — o modelo nunca vê checkpoint/rewind & cia.
        if (await config.GetAsync<bool?>("contexttools.enabled", null, ct) == false)
        {
            toolIds = toolIds
                .Where(id => !id.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)
                    || !ContextBoundaryService.IsContextTool(id["builtin:".Length..]))
                .ToList();
            if (toolIds.Count == 0)
            {
                return [];
            }
        }

        var dbIds = new List<string>();
        var tools = builtins.ResolveIds(toolIds, dbIds);
        if (dbIds.Count > 0)
        {
            // Visibilidade MCP (SPEC-20261014-config-layers RF-002): tools
            // virtuais globais têm UserId null; as de usuário só para o
            // dono. Além disso o usuário pode ter desligado um servidor
            // global — HiddenMcpServerIds cobre disable + sombra por nome.
            var hidden = await new UserConfigOverlayService(db, config)
                .HiddenMcpServerIdsAsync(userId, ct);
            tools.AddRange(await db.Tools.AsNoTracking()
                .Where(t => dbIds.Contains(t.Id) && t.Enabled
                    && (t.UserId == userId
                        || (t.UserId == null
                            && t.Url.StartsWith(McpClientService.VirtualUrlPrefix))))
                .ToListAsync(ct));
            if (hidden.Count > 0)
            {
                tools.RemoveAll(t =>
                    t.Url.StartsWith(McpClientService.VirtualUrlPrefix)
                    && hidden.Contains(McpServerId(t.Url)));
            }
        }

        return tools;
    }

    /// <summary>Extrai o id do servidor de <c>mcp://{serverId}/{tool}</c>.</summary>
    private static string McpServerId(string url) =>
        url[McpClientService.VirtualUrlPrefix.Length..]
            .Split('/', 2)[0];

    /// <summary>
    /// Localiza a tool pelo nome da função do spec. Além do match exato,
    /// built-ins (<c>builtin://{name}</c>) casam por qualquer forma do nome
    /// (<c>name</c>, <c>builtin:name</c>, <c>builtin_name</c>) — providers
    /// podem sanitizar/emitir o nome anunciado sem o prefixo.
    /// </summary>
    public static Tool? FindTool(IReadOnlyList<Tool> tools, string functionName)
    {
        var tool = tools.FirstOrDefault(t => FunctionName(t) == functionName);
        if (tool is not null)
        {
            return tool;
        }

        var bare = PermissionRuleset.BuiltinName(functionName);
        return tools.FirstOrDefault(t =>
            t.Url?.StartsWith(BuiltinToolRegistry.UrlPrefix, StringComparison.OrdinalIgnoreCase) == true
            && string.Equals(t.Url[BuiltinToolRegistry.UrlPrefix.Length..], bare,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Cap de concorrência de um batch de calls paralelizáveis
    /// (<c>BuiltinTools:MaxParallel</c>, default 4 — RF-006 do SPEC
    /// parallel-tool-calls).</summary>
    public int MaxParallel => builtins.MaxParallel;

    /// <summary>
    /// A call <paramref name="functionName"/> resolve para uma built-in
    /// marcada <see cref="IBuiltinChatTool.SafeInParallel"/> e habilitada no
    /// contexto? Tools de DB/HTTP/MCP/python e desconhecidas → false —
    /// sequenciais (classificação conservadora, RF-001/RF-003).
    /// </summary>
    public bool IsSafeInParallel(IReadOnlyList<Tool> tools, string functionName)
    {
        var tool = FindTool(tools, functionName);
        return tool?.Url?.StartsWith(
            BuiltinToolRegistry.UrlPrefix, StringComparison.OrdinalIgnoreCase) == true
            && builtins.IsSafeInParallel(
                tool.Url[BuiltinToolRegistry.UrlPrefix.Length..]);
    }

    /// <summary>
    /// Executa a tool pelo nome da função chamada e retorna o texto do
    /// resultado (truncado) ou mensagem de erro para o modelo, mais o
    /// payload estruturado quando a tool o produz (builtins).
    /// </summary>
    /// <param name="tools">Tools habilitadas no contexto.</param>
    /// <param name="functionName">Nome da função pedida.</param>
    /// <param name="argumentsJson">Argumentos em JSON.</param>
    /// <param name="builtinContext">Contexto das tools built-in (workspace/dono); null desabilita.</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<ToolExecutionOutcome> ExecuteAsync(
        IReadOnlyList<Tool> tools, string functionName, string argumentsJson,
        BuiltinToolContext? builtinContext = null,
        CancellationToken ct = default)
    {
        var tool = FindTool(tools, functionName);
        if (tool is null)
        {
            return new($"Erro: tool '{functionName}' não está habilitada neste chat.");
        }

        if (!string.IsNullOrWhiteSpace(tool.Code))
        {
            return new(await pythonExecutor.ExecuteAsync(tool, functionName, argumentsJson, ct));
        }

        if (McpClientService.ParseVirtualUrl(tool.Url) is { } mcpTarget)
        {
            var server = await mcp.GetServerAsync(mcpTarget.ServerId, ct);
            return new(server is null
                ? $"Erro: servidor MCP da tool '{functionName}' não existe mais."
                : await mcp.CallToolAsync(server, mcpTarget.ToolName, argumentsJson, ct));
        }

        if (tool.Url?.StartsWith(BuiltinToolRegistry.UrlPrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            return await ExecuteBuiltinAsync(
                tool, functionName, argumentsJson, builtinContext, ct);
        }

        return await PostHttpAsync(tool, functionName, argumentsJson, ct);
    }

    /// <summary>Caminho built-in: executa via
    /// <see cref="BuiltinToolRegistry"/>, aplica o bound genérico de
    /// output (SPEC-20261016-tool-output-bounding — tool que já devolveu
    /// <c>truncated:true</c> não é re-truncada) e serializa o Result.</summary>
    private async Task<ToolExecutionOutcome> ExecuteBuiltinAsync(
        Tool tool, string functionName, string argumentsJson,
        BuiltinToolContext? builtinContext, CancellationToken ct)
    {
        if (builtinContext is null)
        {
            return new($"Erro: tool '{functionName}' requer contexto de execução built-in.");
        }

        var outcome = await builtins.ExecuteAsync(tool, argumentsJson, builtinContext, ct);
        if (outcome is null)
        {
            return new($"Erro: tool '{functionName}' não é built-in registrada.");
        }

        var text = outcome.Refused
            ? $"Erro: {outcome.RefuseReason ?? outcome.Text}"
            : outcome.Text;
        JsonElement? result = outcome.Result is null
            ? null
            : JsonSerializer.SerializeToElement(outcome.Result);

        if (!outcome.Refused
            && toolOutputs is not null
            && !AlreadyTruncated(result))
        {
            var bound = await toolOutputs.BoundAsync(
                text, functionName, ct);
            if (bound.Truncated)
            {
                result = WithBoundMetadata(result, bound);
                text = bound.Text;
            }
        }

        return new(text, result);
    }

    /// <summary>Caminho HTTP: POST do argumentsJson na Url da tool com
    /// cap de saída — falhas de transporte viram texto de erro para o
    /// modelo, nunca exceção.</summary>
    private async Task<ToolExecutionOutcome> PostHttpAsync(
        Tool tool, string functionName, string argumentsJson,
        CancellationToken ct)
    {
        try
        {
            using var http = httpClientFactory.CreateClient();
            http.Timeout = Timeout;
            using var content = new StringContent(
                argumentsJson, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(tool.Url, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return new($"Erro: tool '{functionName}' respondeu {(int)response.StatusCode}.");
            }
            return new(body.Length > MaxOutputChars ? body[..MaxOutputChars] : body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new($"Erro ao executar tool '{functionName}': {ex.Message}");
        }
    }

    /// <summary>Atalho sem contexto built-in (tools do banco/HTTP/MCP apenas).</summary>
    public Task<ToolExecutionOutcome> ExecuteAsync(
        IReadOnlyList<Tool> tools, string functionName, string argumentsJson,
        CancellationToken ct) =>
        ExecuteAsync(tools, functionName, argumentsJson, null, ct);

    /// <summary>
    /// Se a tool é mutável para o gate de aprovação
    /// (SPEC-20261007-chat-tool-streaming RF-003): flag
    /// <see cref="Tool.RequiresApproval"/>, ou sempre — código Python e
    /// tools MCP são tratadas como mutáveis por poderem ter efeitos
    /// colaterais arbitrários. Tools HTTP POST seguem o flag (o dono
    /// decide se a URL escreve ou só lê).
    /// </summary>
    public static bool IsMutable(Tool tool) =>
        tool.RequiresApproval
        || !string.IsNullOrWhiteSpace(tool.Code)
        || McpClientService.ParseVirtualUrl(tool.Url) is not null;

    /// <summary>Result já marca <c>truncated:true</c> (a tool aplicou cap próprio).</summary>
    private static bool AlreadyTruncated(JsonElement? result)
    {
        if (result is not { } el || el.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return el.TryGetProperty("truncated", out var t)
            && t.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// Mescla os metadados do bound (<c>truncated</c>, <c>outputPath</c>,
    /// <c>totalLines</c>, <c>totalBytes</c>) no Result existente.
    /// </summary>
    private static JsonElement WithBoundMetadata(
        JsonElement? result, ToolOutputService.BoundResult bound)
    {
        var node = result is { } el && el.ValueKind == JsonValueKind.Object
            ? System.Text.Json.Nodes.JsonObject.Create(el)
            : new System.Text.Json.Nodes.JsonObject();
        node!["truncated"] = true;
        if (bound.OutputPath is { } p)
        {
            node["outputPath"] = p;
        }

        node["totalLines"] = bound.TotalLines;
        node["totalBytes"] = bound.TotalBytes;
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>Extrai o nome da função do spec da tool.</summary>
    public static string? FunctionName(Tool tool)
    {
        try
        {
            using var doc = JsonDocument.Parse(tool.SpecJson);
            return doc.RootElement.TryGetProperty("function", out var fn) &&
                   fn.TryGetProperty("name", out var name)
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
