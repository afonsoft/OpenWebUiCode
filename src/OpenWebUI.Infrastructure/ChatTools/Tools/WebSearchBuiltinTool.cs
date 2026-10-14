using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:web_search</c> — busca na web pelo engine configurado em
/// Admin → Busca web (SPEC-20261007-chat-agent-tools RF-005). Reutiliza o
/// <see cref="WebSearchService"/> — se não houver engine configurado,
/// devolve erro claro em vez de inventar resultado.
/// </summary>
public sealed class WebSearchBuiltinTool(WebSearchService search) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "web_search";

    /// <inheritdoc />
    public string Description =>
        "Search the web — ALWAYS call for current info/news/facts.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Search query." },
            "count": { "type": "integer", "description": "Number of results (max 10).", "default": 5 }
          },
          "required": ["query"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
        {
            return new BuiltinToolResult("Parâmetro 'query' é obrigatório.");
        }

        var count = args.TryGetProperty("count", out var c) && c.TryGetInt32(out var n)
            ? Math.Clamp(n, 1, 10)
            : 5;

        var results = await search.SearchAsync(query, count, ct);
        if (results is null)
        {
            return new BuiltinToolResult(
                "Busca web não configurada — ative um engine em Admin → Configurações → Busca web.");
        }

        if (results.Count == 0)
        {
            return new BuiltinToolResult($"Nenhum resultado para '{query}'.", new { results = Array.Empty<object>() });
        }

        var items = results.Select(r => new { r.Title, r.Url, r.Snippet }).ToList();
        var text = string.Join("\n", results.Select((r, i) =>
            $"{i + 1}. {r.Title}\n   {r.Url}\n   {r.Snippet}"));
        return new BuiltinToolResult(text, new { results = items });
    }
}
