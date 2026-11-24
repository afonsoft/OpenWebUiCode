namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Guard de branch protegida (SPEC-20261124-autonomous-delivery RF-001):
/// operações git que mutam refs são recusadas quando miram uma branch da
/// lista protegida (default <c>main,master</c> — kv
/// <c>git.protectedBranches</c>). O agente é orientado a criar uma
/// branch de trabalho — o bloqueio é a correção, não o beco sem saída.
/// </summary>
public static class ProtectedBranchGuard
{
    /// <summary>Conjunto default quando o kv não está configurado.</summary>
    public const string DefaultCsv = "main,master";

    // Subcomandos que criam/mudam commits na branch corrente — bloqueados
    // quando a branch corrente é protegida.
    private static readonly HashSet<string> MutatingCurrent = new(StringComparer.Ordinal)
    {
        "commit", "merge", "rebase", "cherry-pick", "am"
    };

    /// <summary>CSV → conjunto case-insensitive; vazio cai no default.</summary>
    public static HashSet<string> ParseSet(string? csv)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in (csv ?? string.Empty).Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(name);
        }
        if (set.Count == 0)
        {
            foreach (var name in DefaultCsv.Split(','))
            {
                set.Add(name);
            }
        }
        return set;
    }

    /// <summary>
    /// Analisa o comando por segmentos e devolve a violação encontrada
    /// (mensagem orientando a criar branch) ou <c>null</c> quando liberado.
    /// <paramref name="currentBranch"/>=null indica fora de repo — git
    /// falha sozinho e o guard não precisa opinar.
    /// </summary>
    public static string? Check(
        string command, string? currentBranch, ISet<string> protectedSet)
    {
        foreach (var segment in CommandRiskClassifier.SplitSegments(command))
        {
            var tokens = SkipEnvAssignments(segment);
            if (tokens.Count < 2
                || !Path.GetFileName(tokens[0]).Equals("git", StringComparison.Ordinal))
            {
                continue;
            }

            // Flags globais do git (-C dir, -c k=v) precedem o subcomando.
            var i = 1;
            while (i < tokens.Count && tokens[i].StartsWith('-'))
            {
                // -C e -c consomem o próximo token como valor.
                i += tokens[i] is "-C" or "-c" ? 2 : 1;
            }
            if (i >= tokens.Count)
            {
                continue;
            }

            var sub = tokens[i];
            var args = tokens.Skip(i + 1).ToList();
            if (MutatingCurrent.Contains(sub) && IsProtected(currentBranch, protectedSet))
            {
                return Refusal(currentBranch!);
            }
            if (sub == "push" && PushHitsProtected(args, currentBranch, protectedSet))
            {
                return Refusal(currentBranch ?? string.Join(',', protectedSet));
            }
        }
        return null;
    }

    /// <summary>
    /// Push é bloqueado quando (a) a branch corrente é protegida — push de
    /// HEAD/sem refspec empurra ela — ou (b) um refspec posicional tem
    /// destino protegido (<c>main</c>, <c>HEAD:main</c>, <c>+x:main</c>,
    /// <c>:main</c>).
    /// </summary>
    private static bool PushHitsProtected(
        IReadOnlyList<string> args, string? currentBranch, ISet<string> protectedSet)
    {
        if (IsProtected(currentBranch, protectedSet))
        {
            return true;
        }

        // Posicionais após flags: o 1º é o remote, os demais são refspecs.
        var positionals = args.Where(a => !a.StartsWith('-')).ToList();
        foreach (var refspec in positionals.Skip(1))
        {
            var spec = refspec.TrimStart('+');
            var dst = spec.Split(':', 2) is { } parts && parts.Length == 2
                ? parts[1]
                : spec;
            if (IsProtected(dst, protectedSet))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsProtected(string? branch, ISet<string> protectedSet) =>
        branch is not null && protectedSet.Contains(branch);

    private static string Refusal(string branch) =>
        $"git recusado: '{branch}' é branch protegida — commits e pushes "
        + "diretos nela contornam o fluxo de PR. Crie uma branch de "
        + "trabalho primeiro: `git checkout -b feature/{slug}`.";

    private static IReadOnlyList<string> SkipEnvAssignments(IReadOnlyList<string> tokens)
    {
        var i = 0;
        while (i < tokens.Count && IsEnvAssignment(tokens[i]))
        {
            i++;
        }
        return i == 0 ? tokens : tokens.Skip(i).ToList();
    }

    private static bool IsEnvAssignment(string token)
    {
        var eq = token.IndexOf('=');
        return eq > 0
            && (char.IsLetter(token[0]) || token[0] == '_')
            && token[..eq].All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}
