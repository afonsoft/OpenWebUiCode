using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using OpenWebUI.Domain;
using McpTool = ModelContextProtocol.Client.McpClientTool;
using TextContentBlock = ModelContextProtocol.Protocol.TextContentBlock;
using CallToolResult = ModelContextProtocol.Protocol.CallToolResult;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Cliente MCP: conecta a servers registrados (stdio via subprocess ou
/// streamable HTTP), descobre tools via <c>tools/list</c> e executa
/// <c>tools/call</c>. Tools são materializadas como <see cref="Tool"/>
/// virtuais (<c>UserId = "mcp"</c>, <c>Url = "mcp://{serverId}/{tool}"</c>)
/// para fluir no pipeline de function calling existente.
/// Timeout de 30s por operação; falhas viram <see cref="McpServer.LastError"/>,
/// nunca exceção para o chamador de chat.
/// </summary>
public partial class McpClientService(
    AppDbContext db,
    HybridCache cache,
    PolicyService? policies = null,
    SkillDiscoveryService? skills = null,
    ILogger<McpClientService>? logger = null,
    McpOAuthService? oauth = null)
{

    /// <summary>Prefixo interno de URL das tools virtuais MCP.</summary>
    public const string VirtualUrlPrefix = "mcp://";

    /// <summary>Prefixo de id de servidor declarado em <c>.agents/mcp.json</c> (não cadastrado via UI).</summary>
    public const string EnvServerIdPrefix = "env:";

    private const string FunctionType = "function";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ToolsCacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EnvSyncTtl = TimeSpan.FromSeconds(120);

    // Valores literais de env dos servers env:* (resolvidos na sync; o DB
    // persiste só os NOMES em EnvJson — valores ficam em memória e são
    // repovoados a cada boot/sync, RF-006 global-agents-scope).
    private static readonly ConcurrentDictionary<string, Dictionary<string, string?>> EnvLiterals = new();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> EnvSyncAt = new();

    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = ToolsCacheTtl,
        LocalCacheExpiration = ToolsCacheTtl,
    };
    private const int MaxOutputChars = 4000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Re-descobre as tools do server (<c>tools/list</c>) e materializa como
    /// Tools virtuais. Atualiza <see cref="McpServer.LastError"/>.
    /// </summary>
    /// <param name="server">Server registrado.</param>
    /// <param name="ct">Cancelamento.</param>
    /// <returns>Número de tools descobertas.</returns>
    /// <exception cref="InvalidOperationException">Server desabilitado ou inacessível.</exception>
    public async Task<int> RefreshToolsAsync(McpServer server, CancellationToken ct = default)
    {
        if (!server.Enabled)
        {
            throw new InvalidOperationException("Servidor MCP desabilitado.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            await using var client = await ConnectAsync(server, timeout.Token);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);

            // RF-003 (SPEC-20261018-agent-polish): servers com resources
            // ganham a tool virtual read-only `read_resource`
            // (mcp_{server}_read_resource) — o modelo lê qualquer URI do
            // resources/list sem tool específica por recurso.
            List<McpResourceDescriptor>? resources = null;
            try
            {
                var listed = await client.ListResourcesAsync(
                    cancellationToken: timeout.Token);
                if (listed.Count > 0)
                {
                    resources = listed
                        .Select(r => new McpResourceDescriptor(
                            r.Name, r.Uri, r.MimeType, r.Description))
                        .ToList();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogDebug(ex,
                    "Server {Server}: resources/list falhou — sem read_resource.", server.Name);
            }

            // Prompts expostos via slash `{server}:{prompt}` (RF-003) —
            // cache paralelo ao de tools.
            try
            {
                var prompts = await client.ListPromptsAsync(
                    cancellationToken: timeout.Token);
                await cache.SetAsync(PromptsCacheKey(server.Id),
                    prompts.Select(p => new McpPromptDescriptor(
                        p.Name, p.Description,
                        p.ProtocolPrompt.Arguments?
                            .Select(a => a.Name ?? "").ToList() ?? []))
                        .ToList(),
                    CacheOptions, tags: ["mcp"], cancellationToken: timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogDebug(ex,
                    "Server {Server}: prompts/list falhou — sem slash prompts.", server.Name);
            }

            var stale = await db.Tools
                .Where(t => t.Url.StartsWith($"{VirtualUrlPrefix}{server.Id}/"))
                .ToListAsync(timeout.Token);
            db.Tools.RemoveRange(stale);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Tools virtuais herdam o dono do server (SPEC-20261014-config-
            // layers RF-002): globais ficam null (visíveis a todos); de
            // usuário carregam o userId e só entram no catálogo dele.
            var virtualTools = tools.Select(t => new Tool
            {
                UserId = server.UserId,
                Name = t.Name,
                Description = t.Description,
                SpecJson = BuildSpecJson(server, t),
                Url = $"{VirtualUrlPrefix}{server.Id}/{t.Name}",
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            }).ToList();
            if (resources is not null)
            {
                virtualTools.Add(BuildReadResourceTool(server, now));
            }
            db.Tools.AddRange(virtualTools);

            server.LastError = null;
            server.UpdatedAt = now;
            await db.SaveChangesAsync(timeout.Token);

            await cache.SetAsync(ToolsCacheKey(server.Id),
                virtualTools.Select(t => new McpToolDescriptor(t.Name, t.Description ?? "")).ToList(), CacheOptions,
                tags: ["mcp"], cancellationToken: timeout.Token);
            return virtualTools.Count;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            server.LastError = $"Timeout de {Timeout.TotalSeconds}s ao contactar o servidor MCP.";
            await db.SaveChangesAsync(CancellationToken.None);
            throw new InvalidOperationException(server.LastError);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            server.LastError = ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw new InvalidOperationException($"Falha ao contactar o servidor MCP: {ex.Message}");
        }
    }

    /// <summary>
    /// Executa <c>tools/call</c> no server e serializa o resultado
    /// (texto ou JSON dos content blocks) para o modelo.
    /// </summary>
    /// <param name="server">Server dono da tool.</param>
    /// <param name="toolName">Nome da tool no server.</param>
    /// <param name="argumentsJson">Argumentos JSON do function call.</param>
    /// <param name="ct">Cancelamento.</param>
    /// <returns>Texto do resultado ou mensagem de erro para o modelo.</returns>
    public async Task<string> CallToolAsync(
        McpServer server, string toolName, string argumentsJson, CancellationToken ct = default)
    {
        if (!server.Enabled)
        {
            return $"Erro: servidor MCP '{server.Name}' está desabilitado.";
        }

        // Policies admin (SPEC-20261018-policies-admin RF-004): hard-deny
        // de integração antes de conectar.
        if (policies is not null
            && await policies.DenyAsync(PolicyService.ActionIntegrationUse,
                new[] { $"mcp:{server.Id}", $"mcp:{server.Name}" }, server.UserId, ct) is { } denied)
        {
            return "Erro: " + PolicyService.DeniedMessage(denied, $"mcp:{server.Name}");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            await using var client = await ConnectAsync(server, timeout.Token);
            var args = ParseArguments(argumentsJson);

            // Tool virtual read-only (RF-003): lê um resource do server.
            if (toolName.Equals(ReadResourceToolName, StringComparison.OrdinalIgnoreCase))
            {
                var uri = args.TryGetValue("uri", out var u) ? u?.ToString() : null;
                return string.IsNullOrWhiteSpace(uri)
                    ? "Erro: argumento 'uri' é obrigatório."
                    : await ReadResourceCoreAsync(client, uri, timeout.Token);
            }

            var result = await client.CallToolAsync(
                toolName, args, progress: null, cancellationToken: timeout.Token);

            var text = SerializeResult(result);
            var truncated = text.Length > MaxOutputChars ? text[..MaxOutputChars] : text;
            return result.IsError is true ? $"Erro: {truncated}" : truncated;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Erro: timeout de {Timeout.TotalSeconds}s ao executar a tool no servidor MCP.";
        }
        catch (HttpRequestException ex) { return $"Erro ao executar a tool MCP '{toolName}': {ex.Message}"; }
        catch (JsonException ex) { return $"Erro ao executar a tool MCP '{toolName}': {ex.Message}"; }
        catch (InvalidOperationException ex) { return $"Erro ao executar a tool MCP '{toolName}': {ex.Message}"; }
        catch (ModelContextProtocol.McpException ex) { return $"Erro ao executar a tool MCP '{toolName}': {ex.Message}"; }
    }

    /// <summary>Nome da tool virtual de leitura de resources (RF-003).</summary>
    public const string ReadResourceToolName = "read_resource";

    /// <summary>
    /// Lista os resources do server (<c>resources/list</c>) — RF-003.
    /// Falha → lista vazia (resources não derrubam a run).
    /// </summary>
    public async Task<List<McpResourceDescriptor>> ListResourcesAsync(
        McpServer server, CancellationToken ct = default)
    {
        if (!server.Enabled)
        {
            return [];
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await using var client = await ConnectAsync(server, timeout.Token);
            var resources = await client.ListResourcesAsync(
                cancellationToken: timeout.Token);
            return resources
                .Select(r => new McpResourceDescriptor(
                    r.Name, r.Uri, r.MimeType, r.Description))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogDebug(ex, "Server {Server}: resources/list falhou.", server.Name);
            return [];
        }
    }

    /// <summary>
    /// Lê um resource do server (<c>resources/read</c>) — RF-003. Texto
    /// retorna direto; blob vira nota de tamanho+mime. Erros viram
    /// mensagem para o modelo, nunca exceção.
    /// </summary>
    public async Task<string> ReadResourceAsync(
        McpServer server, string uri, CancellationToken ct = default)
    {
        if (!server.Enabled)
        {
            return $"Erro: servidor MCP '{server.Name}' está desabilitado.";
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await using var client = await ConnectAsync(server, timeout.Token);
            return await ReadResourceCoreAsync(client, uri, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"Erro: timeout de {Timeout.TotalSeconds}s ao ler o resource no servidor MCP.";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            or InvalidOperationException or ModelContextProtocol.McpException)
        {
            return $"Erro ao ler o resource '{uri}' no servidor MCP: {ex.Message}";
        }
    }

    private static async Task<string> ReadResourceCoreAsync(
        McpClient client, string uri, CancellationToken ct)
    {
        var result = await client.ReadResourceAsync(uri, cancellationToken: ct);
        var parts = new List<string>();
        foreach (var content in result.Contents)
        {
            switch (content)
            {
                case ModelContextProtocol.Protocol.TextResourceContents textRes:
                    parts.Add(textRes.Text);
                    break;
                case ModelContextProtocol.Protocol.BlobResourceContents blob:
                    parts.Add($"[blob {blob.MimeType ?? "application/octet-stream"} — {blob.Blob.Length} bytes]");
                    break;
            }
        }
        var text = string.Join("\n", parts);
        var truncated = text.Length > MaxOutputChars ? text[..MaxOutputChars] : text;
        return string.IsNullOrWhiteSpace(truncated) ? "(resource vazio)" : truncated;
    }

    /// <summary>
    /// Lista os prompts do server (<c>prompts/list</c>) — RF-003. Cache
    /// de 60s junto às tools; falha → cache vazio (prompts são opcionais).
    /// </summary>
    public async Task<List<McpPromptDescriptor>> ListPromptsAsync(
        McpServer server, CancellationToken ct = default)
    {
        if (!server.Enabled)
        {
            return [];
        }
        var cached = await cache.GetOrCreateAsync(
            PromptsCacheKey(server.Id),
            async cancel =>
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    timeout.CancelAfter(Timeout);
                    await using var client = await ConnectAsync(server, timeout.Token);
                    var prompts = await client.ListPromptsAsync(
                        cancellationToken: timeout.Token);
                    return prompts.Select(p => new McpPromptDescriptor(
                        p.Name, p.Description,
                        p.ProtocolPrompt.Arguments?
                            .Select(a => a.Name ?? "").ToList() ?? []))
                        .ToList();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogDebug(ex, "Server {Server}: prompts/list falhou.", server.Name);
                    return new List<McpPromptDescriptor>();
                }
            },
            CacheOptions, tags: ["mcp"], cancellationToken: ct);
        return [.. cached];
    }

    /// <summary>
    /// Resolve um prompt do server (<c>prompts/get</c>) — RF-003:
    /// devolve o texto dos blocos unidos; erro vira null para o chamador
    /// mapear 404/400.
    /// </summary>
    public async Task<string?> GetPromptAsync(
        McpServer server, string promptName,
        IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        if (!server.Enabled)
        {
            return null;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await using var client = await ConnectAsync(server, timeout.Token);
            var result = await client.GetPromptAsync(
                promptName, args, cancellationToken: timeout.Token);
            var parts = result.Messages.Select(message =>
                message.Content is TextContentBlock text
                    ? text.Text
                    : JsonSerializer.Serialize(message.Content, message.Content.GetType(), JsonOptions));
            return string.Join("\n\n", parts);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            or InvalidOperationException or ModelContextProtocol.McpException)
        {
            logger?.LogDebug(ex, "Server {Server}: prompts/get falhou.", server.Name);
            return null;
        }
    }

    /// <summary>Spec JSON da tool virtual read_resource (RF-003).</summary>
    private static Tool BuildReadResourceTool(McpServer server, long now)
    {
        var spec = new Dictionary<string, object?>
        {
            ["type"] = FunctionType,
            [FunctionType] = new Dictionary<string, object?>
            {
                ["name"] = VirtualFunctionName(server, ReadResourceToolName),
                ["description"] = $"Lê um resource exposto pelo servidor MCP {server.Name} por URI.",
                ["parameters"] = JsonDocument.Parse("""
                    {"type":"object","properties":{"uri":{"type":"string","description":"URI do resource (veja resources/list)"}},"required":["uri"]}
                    """).RootElement,
            },
        };
        return new Tool
        {
            UserId = server.UserId,
            Name = ReadResourceToolName,
            Description = $"Lê resources do servidor MCP {server.Name}.",
            SpecJson = JsonSerializer.Serialize(spec, JsonOptions),
            Url = $"{VirtualUrlPrefix}{server.Id}/{ReadResourceToolName}",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Nome da função virtual de uma tool MCP (OpenAI-safe: [a-zA-Z0-9_-], máx 64).</summary>
    public static string VirtualFunctionName(McpServer server, string toolName)
    {
        var raw = $"mcp_{Slug(server.Name)}_{Slug(toolName)}";
        return raw.Length > 64 ? raw[..64] : raw;
    }

    /// <summary>Extrai serverId e toolName de uma URL virtual <c>mcp://{serverId}/{tool}</c>.</summary>
    internal static (string ServerId, string ToolName)? ParseVirtualUrl(string url)
    {
        if (!url.StartsWith(VirtualUrlPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var rest = url[VirtualUrlPrefix.Length..];
        var slash = rest.IndexOf('/');
        return slash <= 0 ? null : (rest[..slash], rest[(slash + 1)..]);
    }

    /// <summary>
    /// Abre o transporte (stdio ou streamable HTTP). Servers HTTP do
    /// usuário ganham <c>Authorization: Bearer</c> via
    /// <see cref="McpOAuthService"/> quando há token (SPEC-20261018-
    /// mcp-oauth RF-004); falha 401 marca o server como
    /// <c>authState=required</c> e repropaga o erro original.
    /// </summary>
    private async Task<McpClient> ConnectAsync(McpServer server, CancellationToken ct)
    {
        IClientTransport transport = server.Transport == "stdio"
            ? new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command!,
                Arguments = ParseStringList(server.ArgsJson),
                EnvironmentVariables = LoadEnv(server),
                InheritEnvironmentVariables = false,
            })
            : new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url!, UriKind.Absolute),
                TransportMode = HttpTransportMode.AutoDetect,
                AdditionalHeaders = await ResolveHeadersAsync(server, ct),
            });

        try
        {
            return await McpClient.CreateAsync(transport, null, null, ct);
        }
        catch (Exception ex) when (oauth is not null
            && server.UserId is not null
            && McpOAuthService.IsAuthError(ex))
        {
            await oauth.MarkRequiredAsync(server.UserId, server.Id, ct);
            throw;
        }
    }

    /// <summary>Headers do server + Bearer OAuth quando há token válido.</summary>
    private async Task<Dictionary<string, string>> ResolveHeadersAsync(
        McpServer server, CancellationToken ct)
    {
        var headers = ParseHeaders(server.HeadersJson);
        if (oauth is null || server.UserId is not { Length: > 0 } userId)
        {
            return headers;
        }
        var token = await oauth.GetAccessTokenAsync(userId, server.Id, ct);
        if (token is { Length: > 0 })
        {
            headers["Authorization"] = $"Bearer {token}";
        }
        return headers;
    }

    /// <summary>
    /// Só os nomes listados em EnvJson são lidos do ambiente e passados ao
    /// processo — valores nunca persistidos (permit-list, RF-004). Servers
    /// <c>env:*</c> mesclam os literais declarados no <c>mcp.json</c>
    /// (resolvidos na sync, mantidos em memória apenas).
    /// </summary>
    private static Dictionary<string, string?> LoadEnv(McpServer server)
    {
        var env = new Dictionary<string, string?>();
        foreach (var name in ParseStringList(server.EnvJson).Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            env[name] = Environment.GetEnvironmentVariable(name);
        }

        if (server.Id.StartsWith(EnvServerIdPrefix, StringComparison.Ordinal)
            && EnvLiterals.TryGetValue(server.Id, out var literals))
        {
            foreach (var (k, v) in literals)
            {
                env[k] = v;
            }
        }

        return env;
    }

    private static string BuildSpecJson(McpServer server, McpClientTool tool)
    {
        var spec = new Dictionary<string, object?>
        {
            ["type"] = FunctionType,
            [FunctionType] = new Dictionary<string, object?>
            {
                ["name"] = VirtualFunctionName(server, tool.Name),
                ["description"] = tool.Description ?? $"Tool {tool.Name} do servidor MCP {server.Name}",
                ["parameters"] = tool.JsonSchema.ValueKind == JsonValueKind.Object
                    ? tool.JsonSchema
                    : JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement,
            },
        };
        return JsonSerializer.Serialize(spec, JsonOptions);
    }

    private static IReadOnlyDictionary<string, object?> ParseArguments(string argumentsJson)
    {
        var args = new Dictionary<string, object?>();
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    args[prop.Name] = prop.Value.Clone();
                }
            }
        }
        catch (JsonException)
        {
            // argumentos inválidos → chamada vazia; o server responde com erro de schema
        }
        return args;
    }

    private static string SerializeResult(CallToolResult result)
    {
        var parts = new List<string>();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text)
            {
                parts.Add(text.Text);
            }
            else
            {
                parts.Add(JsonSerializer.Serialize(block, block.GetType(), JsonOptions));
            }
        }
        if (parts.Count == 0 && result.StructuredContent is { } structured)
        {
            parts.Add(structured.GetRawText());
        }
        return string.Join("\n", parts);
    }

    /// <summary>Deserializa uma lista de strings em JSON (tolerante a malformado).</summary>
    public static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Deserializa um objeto de headers em JSON (tolerante a malformado).</summary>
    public static Dictionary<string, string> ParseHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Materializa os servidores declarados em <c>~/.agents/mcp.json</c> +
    /// <c>{workdir}/.agents/mcp.json</c> (SPEC-20261016-global-agents-scope
    /// RF-006): upsert de linhas <c>env:{id}</c> (globais, <c>UserId</c>
    /// null), desativa as não mais declaradas e re-descobre suas tools como
    /// <c>mcp://</c> virtuais. Throttle de 120s por workdir — o refresh
    /// conecta de fato a cada server. Falhas de um server viram
    /// <see cref="McpServer.LastError"/>, nunca quebram a run.
    /// </summary>
    /// <param name="workdir">Workdir do repo vinculado (null = só o global).</param>
    /// <param name="ct">Cancelamento.</param>
    /// <returns>Quantidade de tools materializadas.</returns>
    public async Task<int> SyncEnvServersAsync(string? workdir, CancellationToken ct = default)
    {
        if (skills is null)
        {
            return 0;
        }

        var throttleKey = workdir ?? "";
        if (EnvSyncAt.TryGetValue(throttleKey, out var last)
            && DateTimeOffset.UtcNow - last < EnvSyncTtl)
        {
            return 0;
        }
        EnvSyncAt[throttleKey] = DateTimeOffset.UtcNow;

        var config = SkillDiscoveryService.LoadMcpServers(workdir);
        foreach (var w in config.Warnings)
        {
            logger?.LogWarning("mcp.json: {Warning}", w);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = new List<McpServer>();
        foreach (var def in config.Servers)
        {
            var id = EnvServerIdPrefix + def.Id;
            var envNames = new List<string>();
            var envValues = new Dictionary<string, string?>();
            foreach (var (k, v) in def.Env)
            {
                envNames.Add(k);
                envValues[k] = ResolveEnvRef(v);
            }
            EnvLiterals[id] = envValues;

            var row = await db.McpServers.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (row is null)
            {
                row = new McpServer { Id = id, CreatedAt = now };
                db.McpServers.Add(row);
            }

            row.Name = def.Id;
            row.Transport = def.Transport;
            row.Command = def.Command;
            row.ArgsJson = JsonSerializer.Serialize(def.Args, JsonOptions);
            row.EnvJson = JsonSerializer.Serialize(envNames, JsonOptions);
            row.Url = def.Url;
            row.HeadersJson = JsonSerializer.Serialize(def.Headers, JsonOptions);
            row.UserId = null;
            row.Enabled = true;
            row.UpdatedAt = now;
            rows.Add(row);
        }

        var declared = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var stale = await db.McpServers
            .Where(s => s.Id.StartsWith(EnvServerIdPrefix) && !declared.Contains(s.Id))
            .ToListAsync(ct);
        foreach (var s in stale)
        {
            s.Enabled = false;
            s.UpdatedAt = now;
            EnvLiterals.TryRemove(s.Id, out _);
        }

        await db.SaveChangesAsync(ct);
        await cache.RemoveByTagAsync("mcp", ct);

        var materialized = 0;
        foreach (var row in rows)
        {
            try
            {
                materialized += await RefreshToolsAsync(row, ct);
            }
            catch (InvalidOperationException ex)
            {
                logger?.LogWarning(
                    ex, "Servidor MCP env:{Id} falhou no refresh", row.Name);
            }
        }

        return materialized;
    }

    /// <summary>
    /// Resolve referências <c>${VAR}</c>/<c>{env:VAR}</c>/<c>$VAR</c> de
    /// <c>mcp.json</c> lendo o ambiente do processo; demais valores passam
    /// como literais (mantidos só em memória).
    /// </summary>
    internal static string? ResolveEnvRef(string value)
    {
        var m = EnvRefRegex().Match(value);
        return m.Success
            ? Environment.GetEnvironmentVariable(m.Groups["v"].Value)
            : value;
    }

    [GeneratedRegex(@"^\$\{(?<v>[A-Za-z_][A-Za-z0-9_]*)\}$|^\{env:(?<v>[A-Za-z_][A-Za-z0-9_]*)\}$|^\$(?<v>[A-Za-z_][A-Za-z0-9_]*)$")]
    private static partial Regex EnvRefRegex();

    /// <summary>
    /// Obtém um servidor MCP por id com cache de 30s (hot path do
    /// <c>tools/call</c> — evita query ao SQLite por execução de tool).
    /// Invalidado pela tag "mcp" em qualquer mutação de servidor.
    /// </summary>
    /// <param name="serverId">Id do servidor MCP.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public Task<McpServer?> GetServerAsync(string serverId, CancellationToken ct = default)
        => cache.GetOrCreateAsync(
            $"mcp-server:{serverId}",
            async cancel => await db.McpServers.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == serverId, cancel),
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromSeconds(30),
                LocalCacheExpiration = TimeSpan.FromSeconds(30),
            },
            tags: ["mcp"],
            cancellationToken: ct).AsTask();

    private static string ToolsCacheKey(string serverId) => $"mcp-tools:{serverId}";

    private static string PromptsCacheKey(string serverId) => $"mcp-prompts:{serverId}";

    private static string Slug(string value)
    {
        var slug = SlugRegex().Replace(value, "_");
        return string.IsNullOrEmpty(slug) ? "x" : slug;
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]+")]
    private static partial Regex SlugRegex();
}

/// <summary>Descrição serializável de uma tool MCP para o HybridCache (tuplas não serializam).</summary>
public sealed record McpToolDescriptor(string Name, string Description);

/// <summary>Descrição serializável de um prompt MCP (RF-003) — slash `{server}:{prompt}`.</summary>
public sealed record McpPromptDescriptor(
    string Name, string? Description, IReadOnlyList<string> Arguments);

/// <summary>Descrição serializável de um resource MCP (RF-003).</summary>
public sealed record McpResourceDescriptor(
    string Name, string Uri, string? MimeType, string? Description);