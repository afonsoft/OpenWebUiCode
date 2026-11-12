using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Índice em memória por workspace para o <c>builtin:code_search</c>
/// (SPEC-20261103-semantic-code-search): chunks por arquivo com mtime+size
/// (reindex incremental), chunker por janela de linhas com overlap,
/// scoring lexical e vetores cacheados por chunk (reuso por chamada).
/// Singleton — o índice sobrevive entre calls do mesmo processo.
/// </summary>
public sealed class WorkspaceIndexService
{
    /// <summary>Arquivos acima do cap não entram no índice.</summary>
    public const long MaxIndexFileBytes = 512 * 1024;
    /// <summary>Linhas por chunk.</summary>
    public const int ChunkLines = 40;
    /// <summary>Overlap entre chunks consecutivos.</summary>
    public const int ChunkOverlap = 10;
    /// <summary>Budget de embeddings por chamada — chunks além do cap
    /// ficam só no lado lexical até a próxima chamada.</summary>
    public const int MaxEmbedPerCall = 512;

    /// <summary>Chunk de código indexado.</summary>
    public sealed record Chunk(
        string Rel, int StartLine, int EndLine, string Text)
    {
        /// <summary>Vetor cacheado (null até a 1ª busca semântica).</summary>
        public float[]? Vector { get; set; }
    }

    private sealed record FileEntry(long MtimeTicks, long Size,
        List<Chunk> Chunks);

    private sealed class Index
    {
        public readonly ConcurrentDictionary<string, FileEntry> Files = new(
            StringComparer.OrdinalIgnoreCase);
        public readonly object Gate = new();
    }

    private readonly ConcurrentDictionary<string, Index> _indexes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Varre o workspace e reindexa só o que mudou (mtime+size).
    /// Retorna quantos arquivos foram (re)indexados nesta passada.</summary>
    public int EnsureIndexed(string workspace)
    {
        var index = _indexes.GetOrAdd(workspace, _ => new Index());
        lock (index.Gate)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reindexed = 0;
            foreach (var file in WorkspaceFiles.EnumerateFiles(workspace))
            {
                var rel = WorkspaceFiles.RelativeOf(workspace, file);
                seen.Add(rel);
                if (WorkspaceFiles.LooksBinary(file))
                {
                    index.Files.TryRemove(rel, out _);
                    continue;
                }
                FileInfo info;
                try { info = new FileInfo(file); }
                catch (Exception) { continue; }
                if (!info.Exists || info.Length is 0 or > MaxIndexFileBytes)
                {
                    index.Files.TryRemove(rel, out _);
                    continue;
                }
                if (index.Files.TryGetValue(rel, out var e)
                    && e.MtimeTicks == info.LastWriteTimeUtc.Ticks
                    && e.Size == info.Length)
                {
                    continue;
                }
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch (Exception) { continue; }
                index.Files[rel] = new FileEntry(
                    info.LastWriteTimeUtc.Ticks, info.Length,
                    ChunkFile(rel, lines));
                reindexed++;
            }
            foreach (var gone in index.Files.Keys
                .Where(k => !seen.Contains(k)).ToList())
            {
                index.Files.TryRemove(gone, out _);
            }
            return reindexed;
        }
    }

    /// <summary>Todos os chunks do índice (após EnsureIndexed).</summary>
    public List<Chunk> AllChunks(string workspace) =>
        _indexes.TryGetValue(workspace, out var index)
            ? index.Files.Values.SelectMany(f => f.Chunks).ToList()
            : [];

    /// <summary>Score lexical: termos da query com frequência saturada por
    /// chunk + bônus de match no caminho. Tokens ≥2 chars, lowercase.</summary>
    public static double LexicalScore(Chunk chunk, string[] terms)
    {
        if (terms.Length == 0)
        {
            return 0;
        }
        var text = chunk.Text.ToLowerInvariant();
        var rel = chunk.Rel.ToLowerInvariant();
        var score = 0.0;
        foreach (var term in terms)
        {
            var hits = CountOccurrences(text, term);
            if (hits > 0)
            {
                score += hits / (1.0 + hits);
            }
            if (rel.Contains(term, StringComparison.Ordinal))
            {
                score += 0.5;
            }
        }
        return score;
    }

    /// <summary>Cosseno entre dois vetores (0 quando incomparáveis).</summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }
        var n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na > 0 && nb > 0 ? dot / (Math.Sqrt(na) * Math.Sqrt(nb)) : 0;
    }

    /// <summary>Reciprocal Rank Fusion entre rankings lex/semântico
    /// (k=60, convenção clássica).</summary>
    public static Dictionary<Chunk, double> Rrf(
        List<Chunk> lexRank, List<Chunk> semRank)
    {
        var fused = new Dictionary<Chunk, double>();
        for (var i = 0; i < lexRank.Count; i++)
        {
            fused[lexRank[i]] = fused.GetValueOrDefault(lexRank[i])
                + 1.0 / (60 + i + 1);
        }
        for (var i = 0; i < semRank.Count; i++)
        {
            fused[semRank[i]] = fused.GetValueOrDefault(semRank[i])
                + 1.0 / (60 + i + 1);
        }
        return fused;
    }

    /// <summary>Tokeniza a query em termos únicos ≥2 chars.</summary>
    public static string[] QueryTerms(string query) =>
        Regex.Split(query.ToLowerInvariant(), @"[^a-z0-9_]+",
                RegexOptions.None, TimeSpan.FromSeconds(1))
            .Where(t => t.Length >= 2)
            .Distinct()
            .ToArray();

    /// <summary>Conta ocorrências não-sobrepostas de um termo.</summary>
    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var i = 0;
        while ((i = text.IndexOf(term, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += term.Length;
        }
        return count;
    }

    /// <summary>Chunker por janela fixa com overlap.</summary>
    private static List<Chunk> ChunkFile(string rel, string[] lines)
    {
        var chunks = new List<Chunk>();
        var step = Math.Max(1, ChunkLines - ChunkOverlap);
        for (var start = 0; start < lines.Length; start += step)
        {
            var end = Math.Min(start + ChunkLines, lines.Length);
            chunks.Add(new Chunk(rel, start + 1, end,
                string.Join('\n', lines[start..end])));
            if (end >= lines.Length)
            {
                break;
            }
        }
        return chunks;
    }
}
