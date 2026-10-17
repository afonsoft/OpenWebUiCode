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
    public bool SafeInParallel => true;


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

        var outcome = await search.SearchDetailedAsync(query, count, ct);
        if (outcome is null)
        {
            return new BuiltinToolResult(
                "Busca web não configurada — ative um engine em Admin → Configurações → Busca web.");
        }

        // Todos os providers em cooldown/falha no modo auto (RF-004).
        if (outcome.Error is { } error)
        {
            return new BuiltinToolResult(
                $"Busca web indisponível ({error}) — tente em "
                    + $"{outcome.RetryAfterSeconds}s.",
                new
                {
                    error,
                    retryAfterSeconds = outcome.RetryAfterSeconds,
                    provider = outcome.Provider,
                });
        }

        var results = outcome.Results;
        if (results.Count == 0)
        {
            return new BuiltinToolResult($"Nenhum resultado para '{query}'.", new { results = Array.Empty<object>() });
        }

        // RF-003: provider efetivo (e cadeia de failover) visível no output.
        var via = outcome.FailoverChain ?? outcome.Provider;
        var items = results.Select(r => new { r.Title, r.Url, r.Snippet }).ToList();
        var text = $"[via {via}]\n" + string.Join("\n", results.Select((r, i) =>
            $"{i + 1}. {r.Title}\n   {r.Url}\n   {r.Snippet}"));
        return new BuiltinToolResult(text, new
        {
            results = items,
            provider = outcome.Provider,
            failover = outcome.FailoverChain,
        });
    }
}
