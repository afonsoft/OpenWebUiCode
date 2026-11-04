using System.Text;
using System.Text.Json;
using OpenWebUI.Infrastructure.Services;
using Chunk = OpenWebUI.Infrastructure.Services.WorkspaceIndexService.Chunk;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:code_search</c> — busca híbrida semântica+lexical sobre o
/// índice do workspace (SPEC-20261103-semantic-code-search): fusão RRF
/// quando há provider de embeddings, degrade honesto para lexical puro
/// quando não há. Resultado navegável (path + linhas + excerpt) para
/// <c>file_read</c> subsequente.
/// </summary>
public sealed class CodeSearchBuiltinTool(
    WorkspaceIndexService? index = null,
    EmbeddingService? embeddings = null) : IBuiltinChatTool
{
    private const int MaxExcerptLines = 8;
    private const int LexicalCandidates = 100;

    /// <inheritdoc />
    public string Name => "code_search";

    /// <inheritdoc />
    public string Description =>
        "Semantic + lexical code search over the workspace — finds code "
        + "by meaning even without exact identifiers; returns "
        + "path/line-range/excerpt ready for file_read.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Natural-language or code query." },
            "top_k": { "type": "integer", "description": "Max results (default 10, max 50).", "default": 10 },
            "path_filter": { "type": "string", "description": "Optional glob (e.g. 'src/**/*.cs') or substring filter on relative paths." }
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
        var query = args.TryGetProperty("query", out var q)
            ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
        {
            return new BuiltinToolResult("Parâmetro 'query' é obrigatório.");
        }
        var topK = args.TryGetProperty("top_k", out var k)
            && k.TryGetInt32(out var kv) ? Math.Clamp(kv, 1, 50) : 10;
        var pathFilter = args.TryGetProperty("path_filter", out var pf)
            ? pf.GetString() : null;

        var idx = index ?? new WorkspaceIndexService();
        idx.EnsureIndexed(context.WorkspacePath);
        var chunks = idx.AllChunks(context.WorkspacePath);
        chunks = ApplyPathFilter(chunks, pathFilter);
        if (chunks.Count == 0)
        {
            return new BuiltinToolResult(
                "Nenhum arquivo indexado no escopo.",
                new { results = Array.Empty<object>(), mode = "lexical" });
        }

        var terms = WorkspaceIndexService.QueryTerms(query);
        var lexRank = chunks
            .Select(c => (C: c, S: WorkspaceIndexService.LexicalScore(c, terms)))
            .Where(x => x.S > 0)
            .OrderByDescending(x => x.S)
            .Take(LexicalCandidates)
            .Select(x => x.C)
            .ToList();

        // Semântico: embed query + chunks ainda sem vetor (budget/call);
        // erro de embeddings → lexical puro no mesmo call (RF-004).
        List<Chunk>? semRank = null;
        var degraded = embeddings is null;
        var degradeReason = embeddings is null
            ? "sem provider de embeddings configurado" : (string?)null;
        if (embeddings is not null)
        {
            try
            {
                var qv = await embeddings.EmbedAsync(query, ct);
                if (qv is not null)
                {
                    var missing = chunks.Where(c => c.Vector is null)
                        .Take(WorkspaceIndexService.MaxEmbedPerCall).ToList();
                    if (missing.Count > 0)
                    {
                        var vecs = await embeddings.EmbedBatchAsync(
                            missing.Select(c => c.Text).ToArray(), ct);
                        if (vecs is not null)
                        {
                            for (var i = 0; i < missing.Count
                                && i < vecs.Length; i++)
                            {
                                missing[i].Vector = vecs[i];
                            }
                        }
                    }
                    semRank = chunks.Where(c => c.Vector is not null)
                        .Select(c => (C: c,
                            S: WorkspaceIndexService.Cosine(qv, c.Vector!)))
                        .Where(x => x.S > 0)
                        .OrderByDescending(x => x.S)
                        .Select(x => x.C)
                        .ToList();
                }
                else
                {
                    degraded = true;
                    degradeReason = "provider de embeddings indisponível";
                }
            }
            catch (Exception ex)
            {
                degraded = true;
                degradeReason = $"embeddings falharam: {ex.Message}";
            }
        }

        var mode = semRank is { Count: > 0 } ? "hybrid" : "lexical";
        var merged = semRank is { Count: > 0 }
            ? WorkspaceIndexService.Rrf(lexRank, semRank)
            : lexRank.ToDictionary(c => c, _ => 0.0);
        if (merged.Count == 0)
        {
            merged = lexRank.ToDictionary(c => c, _ => 0.0);
        }

        var hits = merged
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key.Rel)
            .Take(topK)
            .ToList();
        var sb = new StringBuilder(
            $"{hits.Count} resultado(s) [{mode}"
            + $"{(degraded ? ", degraded: " + degradeReason : "")}]\n");
        var results = hits.Select(h => new
        {
            path = h.Key.Rel,
            start_line = h.Key.StartLine,
            end_line = h.Key.EndLine,
            score = Math.Round(h.Value, 4),
            excerpt = Excerpt(h.Key.Text),
        }).ToList();
        foreach (var h in hits)
        {
            sb.AppendLine(
                $"--- {h.Key.Rel}:{h.Key.StartLine}-{h.Key.EndLine} " +
                $"({h.Value:F4})\n{Excerpt(h.Key.Text)}");
        }

        return new BuiltinToolResult(sb.ToString().TrimEnd(), new
        {
            results,
            degraded = degraded ? true : (bool?)null,
            degrade_reason = degradeReason,
            mode,
        });
    }

    /// <summary>path_filter: glob quando contém coringa, substring senão.</summary>
    private static List<Chunk> ApplyPathFilter(
        List<Chunk> chunks, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return chunks;
        }
        if (filter.Contains('*') || filter.Contains('?'))
        {
            var re = WorkspaceFiles.GlobToRegex(filter);
            return chunks.Where(c => re.IsMatch(c.Rel)).ToList();
        }
        return chunks.Where(c => c.Rel.Contains(
            filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Excerpt ≤ <see cref="MaxExcerptLines"/> linhas do chunk.</summary>
    private static string Excerpt(string text) =>
        string.Join('\n',
            text.Split('\n').Take(MaxExcerptLines)).TrimEnd();
}
