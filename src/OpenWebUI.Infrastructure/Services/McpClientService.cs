using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
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
    HybridCache cache)
{

    /// <summary>Prefixo interno de URL das tools virtuais MCP.</summary>
    public const string VirtualUrlPrefix = "mcp://";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ToolsCacheTtl = TimeSpan.FromSeconds(60);

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
            db.Tools.AddRange(virtualTools);

            server.LastError = null;
            server.UpdatedAt = now;
            await db.SaveChangesAsync(timeout.Token);

            await cache.SetAsync(ToolsCacheKey(server.Id),
                virtualTools.Select(t => new McpToolDescriptor(t.Name, t.Description)).ToList(), CacheOptions,
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

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            await using var client = await ConnectAsync(server, timeout.Token);
            var args = ParseArguments(argumentsJson);
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

    private static async Task<McpClient> ConnectAsync(McpServer server, CancellationToken ct)
    {
        IClientTransport transport = server.Transport == "stdio"
            ? new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command!,
                Arguments = ParseStringList(server.ArgsJson),
                EnvironmentVariables = LoadEnvAllowList(server.EnvJson),
                InheritEnvironmentVariables = false,
            })
            : new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url!, UriKind.Absolute),
                TransportMode = HttpTransportMode.AutoDetect,
                AdditionalHeaders = ParseHeaders(server.HeadersJson),
            });

        return await McpClient.CreateAsync(transport, null, null, ct);
    }

    /// <summary>
    /// Só os nomes listados em EnvJson são lidos do ambiente e passados ao
    /// processo — valores nunca persistidos (permit-list, RF-004).
    /// </summary>
    private static Dictionary<string, string?> LoadEnvAllowList(string? envJson)
    {
        var env = new Dictionary<string, string?>();
        foreach (var name in ParseStringList(envJson).Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            env[name] = Environment.GetEnvironmentVariable(name);
        }
        return env;
    }

    private static string BuildSpecJson(McpServer server, McpClientTool tool)
    {
        var spec = new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
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