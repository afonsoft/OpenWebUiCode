using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Índice leve + busca substring das tools deferidas
/// (SPEC-20261031-tool-search-deferred RF-002/RF-003): o system prompt
/// recebe só "name: one-line description [keywords]" (~15 tokens/tool) e
/// o schema completo entra quando o modelo carrega via
/// <c>builtin_tool_search</c>. MVP = substring match; sem embeddings.
/// </summary>
public sealed class ToolIndexService(IConfiguration cfg)
{
    /// <summary>Teto de linhas do bloco de índice no system prompt.</summary>
    public const int MaxIndexLines = 60;

    /// <summary>
    /// Tools builtin que nunca podem ser deferidas — o loader, o controle
    /// de modo e o resultado de run têm que estar sempre anunciados.
    /// </summary>
    private static readonly HashSet<string> NeverDefer =
        new(StringComparer.OrdinalIgnoreCase)
            { "tool_search", "plan_exit", "run_result", "approval" };

    /// <summary>
    /// Separa as tools deferidas da run (RF-001/RF-005): builtins listadas
    /// em <c>BuiltinTools:Deferred</c> (csv; aceita <c>name</c>,
    /// <c>builtin:name</c> ou <c>builtin_name</c>) + tools MCP quando o nº
    /// de servidores visíveis passa de <c>BuiltinTools:McpDeferThreshold</c>
    /// (default 3), exceto servidores no allowlist
    /// <c>BuiltinTools:McpCore</c>. Opt-in: csv vazio e ≤ threshold =
    /// comportamento atual.
    /// </summary>
    public IReadOnlyList<Tool> SplitDeferred(IReadOnlyList<Tool> tools)
    {
        var deferredNames = Csv(cfg["BuiltinTools:Deferred"]);
        var mcpCore = Csv(cfg["BuiltinTools:McpCore"]);
        var threshold = int.TryParse(cfg["BuiltinTools:McpDeferThreshold"],
            out var th) ? th : 3;
        var mcpServerCount = tools
            .Where(t => t.Url.StartsWith(McpClientService.VirtualUrlPrefix,
                StringComparison.OrdinalIgnoreCase))
            .Select(McpServerId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var deferMcp = mcpServerCount > threshold;
        if (deferredNames.Count == 0 && !deferMcp)
        {
            return [];
        }
        var deferred = new List<Tool>();
        foreach (var tool in tools)
        {
            if (tool.Url.StartsWith(BuiltinToolRegistry.UrlPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                var name = tool.Url[BuiltinToolRegistry.UrlPrefix.Length..];
                if (!NeverDefer.Contains(name)
                    && deferredNames.Contains(name))
                {
                    deferred.Add(tool);
                }
            }
            else if (deferMcp
                && tool.Url.StartsWith(McpClientService.VirtualUrlPrefix,
                    StringComparison.OrdinalIgnoreCase)
                && !mcpCore.Contains(McpServerId(tool)))
            {
                deferred.Add(tool);
            }
        }
        return deferred;
    }

    /// <summary>csv case-insensitive com trim (aceita prefixos builtin:/builtin_).</summary>
    private static HashSet<string> Csv(string? raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (raw ?? string.Empty).Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part;
            if (name.StartsWith(BuiltinToolRegistry.IdPrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                name = name[BuiltinToolRegistry.IdPrefix.Length..];
            }
            else if (name.StartsWith(BuiltinToolRegistry.SpecPrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                name = name[BuiltinToolRegistry.SpecPrefix.Length..];
            }
            set.Add(name);
        }
        return set;
    }

    /// <summary>Id do servidor de <c>mcp://{serverId}/{tool}</c>.</summary>
    private static string McpServerId(Tool tool) =>
        tool.Url[McpClientService.VirtualUrlPrefix.Length..]
            .Split('/', 2)[0];

    /// <summary>
    /// Bloco "## Available on demand" para o system prompt — null quando
    /// não há tools deferidas (feature inativa → prompt idêntico ao atual).
    /// </summary>
    public string? BuildIndex(IReadOnlyList<Tool> deferred)
    {
        if (deferred.Count == 0)
        {
            return null;
        }
        var sb = new StringBuilder();
        sb.AppendLine("## Available on demand");
        sb.AppendLine("More tools are deferred to save context — call "
            + "`builtin_tool_search` with a query to load a tool's schema "
            + "before using it.");
        var shown = deferred.Take(MaxIndexLines).ToList();
        foreach (var tool in shown)
        {
            var name = ToolExecutor.FunctionName(tool) ?? tool.Name;
            var desc = OneLine(DescriptionOf(tool));
            sb.Append("- ").Append(name);
            if (desc.Length > 0)
            {
                sb.Append(": ").Append(desc);
            }
            var keywords = KeywordsOf(tool);
            if (keywords.Length > 0)
            {
                sb.Append(" [").Append(keywords).Append(']');
            }
            sb.Append('\n');
        }
        if (deferred.Count > shown.Count)
        {
            sb.Append("…and ").Append(deferred.Count - shown.Count)
                .Append(" more\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Busca substring sobre nome + descrição + keywords; matches no nome
    /// rankeiam primeiro. <paramref name="limit"/> default 5.
    /// </summary>
    public IReadOnlyList<Tool> Search(
        IReadOnlyList<Tool> deferred, string query, int limit = 5)
    {
        var q = query.Trim();
        if (q.Length == 0 || deferred.Count == 0)
        {
            return [];
        }
        return deferred
            .Where(t => Haystack(t).Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t =>
                (ToolExecutor.FunctionName(t) ?? t.Name)
                    .Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(limit, 1, 20))
            .ToList();
    }

    private static string Haystack(Tool tool)
    {
        var name = ToolExecutor.FunctionName(tool) ?? tool.Name;
        var desc = DescriptionOf(tool);
        return $"{name}\n{desc}\n{KeywordsOf(tool)}";
    }

    /// <summary>Descrição: campo da entity, senão a do spec JSON.</summary>
    private static string DescriptionOf(Tool tool)
    {
        if (!string.IsNullOrWhiteSpace(tool.Description))
        {
            return tool.Description;
        }
        try
        {
            return JsonDocument.Parse(tool.SpecJson).RootElement
                .TryGetProperty("function", out var fn)
                && fn.TryGetProperty("description", out var d)
                    ? d.GetString() ?? string.Empty
                    : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Keywords derivadas dos segmentos do nome (builtin_file_read → file read).</summary>
    private static string KeywordsOf(Tool tool)
    {
        var name = ToolExecutor.FunctionName(tool) ?? tool.Name;
        var bare = name.StartsWith(BuiltinToolRegistry.SpecPrefix,
            StringComparison.OrdinalIgnoreCase)
                ? name[BuiltinToolRegistry.SpecPrefix.Length..]
                : name;
        return string.Join(' ', bare.Split(
            new[] { '_', '-', ' ' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 2));
    }

    private static string OneLine(string text) =>
        text.ReplaceLineEndings(" ").Trim();
}
