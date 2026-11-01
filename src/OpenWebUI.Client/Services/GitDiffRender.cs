namespace OpenWebUI.Client.Services;

/// <summary>
/// Renderização de unified diff compartilhada (git bar do chat e aba
/// Changes do /ide — SPEC-20261009-web-ide-surface RF-003).
/// </summary>
public static class GitDiffRender
{
    /// <summary>Quebra o unified diff em seções por arquivo (chave = path do lado b).</summary>
    public static Dictionary<string, string> SplitDiff(string diff)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var sb = new System.Text.StringBuilder();
        void Flush()
        {
            if (current is not null)
            {
                map[current] = sb.ToString();
            }
            sb.Clear();
        }

        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                var bIdx = line.LastIndexOf(" b/", StringComparison.Ordinal);
                current = bIdx >= 0 ? line[(bIdx + 3)..].Trim() : null;
            }
            if (current is not null)
            {
                sb.AppendLine(line);
            }
        }
        Flush();
        return map;
    }

    /// <summary>Linhas do diff classificadas (+/−/@@/contexto) com a classe CSS.</summary>
    public static IEnumerable<(string Text, string Css)> Lines(string diff)
    {
        foreach (var line in diff.Split('\n'))
        {
            var css = line switch
            {
                { } l2 when l2.StartsWith('+') => "text-green-600 dark:text-green-400",
                { } l2 when l2.StartsWith('-') => "text-red-500 dark:text-red-400",
                { } l2 when l2.StartsWith("@@") => "text-blue-600 dark:text-blue-400",
                _ => "text-gray-500 dark:text-gray-400",
            };
            yield return (line, css);
        }
    }
}
