namespace OpenWebUI.Client.Utils;

/// <summary>Fuzzy match do Quick Open (SPEC-20261011-ide-editor-core, Fase B):
/// subsequência case-insensitive com score — menor é melhor; null = sem match.
/// Bônus para match no início do basename e após separadores.</summary>
public static class IdeQuickOpenFuzzy
{
    /// <summary>Score do match (null se query não é subsequência do candidato).</summary>
    public static int? Score(string query, string candidate)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }
        var q = query.AsSpan();
        var c = candidate.AsSpan();
        var score = 0;
        var qi = 0;
        var lastHit = -2;
        for (var ci = 0; ci < c.Length && qi < q.Length; ci++)
        {
            if (char.ToLowerInvariant(c[ci]) != char.ToLowerInvariant(q[qi]))
            {
                continue;
            }
            score += ci; // matches cedo valem mais
            if (ci == lastHit + 1)
            {
                score -= 3; // bônus: consecutivos
            }
            var baseStart = candidate.LastIndexOf('/') + 1;
            if (ci == baseStart)
            {
                score -= 25; // bônus forte: início do basename (prioridade do Quick Open)
            }
            else if (c[ci - 1] is '/' or '.' or '-' or '_')
            {
                score -= 8; // bônus: início de token
            }
            lastHit = ci;
            qi++;
        }
        return qi == q.Length ? score + candidate.Length : null;
    }

    /// <summary>Filtra + ordena candidatos pelo score; sem query devolve os primeiros max.</summary>
    public static List<string> Match(string query, IEnumerable<string> candidates, int max = 50)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return candidates.Take(max).ToList();
        }
        return candidates
            .Select(c => (c, s: Score(query, c)))
            .Where(x => x.s is not null)
            .OrderBy(x => x.s!.Value)
            .ThenBy(x => x.c.Length)
            .Take(max)
            .Select(x => x.c)
            .ToList();
    }
}
