using System.Text;
using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Resolução de caminhos confinados ao workspace do usuário
/// (SPEC-20261007-chat-agent-parity RF-011): todo caminho pedido pelo
/// modelo é normalizado contra <c>data/workspaces/{userId}</c> — fuga via
/// <c>..</c>, absolutos ou <c>~</c>/<c>$VAR</c> é negada fail-closed.
/// Reusa <see cref="CommandRiskClassifier.PathInside"/> para o jail.
/// </summary>
public static class WorkspaceFiles
{
    /// <summary>Cap de leitura por arquivo (512KB) — protege o contexto do modelo.</summary>
    public const int MaxFileBytes = 512 * 1024;

    /// <summary>Cap de entradas em listagens/globs.</summary>
    public const int MaxEntries = 500;

    /// <summary>
    /// Resolve <paramref name="relative"/> dentro do workspace; devolve o
    /// caminho absoluto ou null + motivo quando escapa do jail.
    /// </summary>
    public static string? ResolveInside(string workspace, string? relative, out string error)
    {
        var rel = string.IsNullOrWhiteSpace(relative) ? "." : relative.Trim();
        if (rel.StartsWith('~') || rel.StartsWith('$'))
        {
            error = $"Caminho '{rel}' não resolve dentro do workspace.";
            return null;
        }

        if (Path.IsPathRooted(rel))
        {
            error = $"Caminho absoluto '{rel}' negado — use caminho relativo ao workspace.";
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Join(workspace, rel));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            error = $"Caminho '{rel}' inválido.";
            return null;
        }

        if (!CommandRiskClassifier.PathInside(workspace, full))
        {
            error = $"Caminho '{rel}' escapa do workspace — negado.";
            return null;
        }

        error = string.Empty;
        return full;
    }

    /// <summary>
    /// <see cref="ResolveInside"/> + resolução de symlinks (SPEC-20261009-workspace-file-api):
    /// se o nó final ou qualquer ancestral dentro do workspace for um link, o
    /// alvo real precisa permanecer dentro do jail — senão um link criado por
    /// <c>shell_exec</c> viraria um canal de fuga da API de arquivos.
    /// </summary>
    public static string? ResolveInsideFinal(string workspace, string? relative, out string error)
    {
        var full = ResolveInside(workspace, relative, out error);
        if (full is null)
        {
            return null;
        }

        FileSystemInfo? node = null;
        if (Directory.Exists(full))
        {
            node = new DirectoryInfo(full);
        }
        else if (File.Exists(full))
        {
            node = new FileInfo(full);
        }
        if (node?.LinkTarget is not null)
        {
            var target = node.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !CommandRiskClassifier.PathInside(workspace, target.FullName))
            {
                error = $"Caminho '{relative}' aponta para fora do workspace — negado.";
                return null;
            }
            full = target.FullName;
        }

        var root = Path.GetFullPath(workspace)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var ancestor = Directory.Exists(full)
            ? new DirectoryInfo(full)
            : new DirectoryInfo(Path.GetDirectoryName(full)!);
        while (ancestor is not null
               && ancestor.FullName.Length > root.Length
               && ancestor.FullName.StartsWith(root, cmp))
        {
            if (ancestor.LinkTarget is not null)
            {
                var target = ancestor.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !CommandRiskClassifier.PathInside(workspace, target.FullName))
                {
                    error = $"Caminho '{relative}' aponta para fora do workspace — negado.";
                    return null;
                }
            }
            ancestor = ancestor.Parent;
        }

        error = string.Empty;
        return full;
    }

    /// <summary>True quando o conteúdo parece binário (NUL nos primeiros 8KB).</summary>
    public static bool LooksBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[8192];
            var read = fs.Read(buf, 0, buf.Length);
            return Array.IndexOf(buf, (byte)0, 0, read) >= 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Converte um glob (<c>*</c>, <c>**</c>, <c>?</c>, <c>{a,b}</c>,
    /// <c>[abc]</c>) em Regex casado contra o caminho relativo com '/'.
    /// </summary>
    public static Regex GlobToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        // '**' atravessa diretórios; '**/' também casa raiz.
                        sb.Append(".*");
                        i += 2;
                        if (i < pattern.Length && pattern[i] == '/')
                        {
                            sb.Append('?');
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                        i++;
                    }

                    break;
                case '?':
                    sb.Append("[^/]");
                    i++;
                    break;
                case '[':
                    var end = pattern.IndexOf(']', i + 1);
                    if (end < 0)
                    {
                        sb.Append("\\[");
                        i++;
                    }
                    else
                    {
                        var cls = pattern[(i + 1)..end];
                        sb.Append('[');
                        if (cls.StartsWith('!'))
                        {
                            sb.Append('^').Append(cls[1..]);
                        }
                        else
                        {
                            sb.Append(cls);
                        }

                        sb.Append(']');
                        i = end + 1;
                    }

                    break;
                case '{':
                    var close = pattern.IndexOf('}', i + 1);
                    if (close < 0)
                    {
                        sb.Append("\\{");
                        i++;
                    }
                    else
                    {
                        var alts = pattern[(i + 1)..close]
                            .Split(',')
                            .Select(Regex.Escape);
                        sb.Append('(').Append(string.Join('|', alts)).Append(')');
                        i = close + 1;
                    }

                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexDefaults.MatchTimeout);
    }

    /// <summary>Caminho relativo (separador '/') de <paramref name="full"/> ao workspace.</summary>
    public static string RelativeOf(string workspace, string full)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(workspace), full);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Enumera arquivos do workspace com caps de profundidade/contagem.</summary>
    public static IEnumerable<string> EnumerateFiles(string root, int maxDepth = 8)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        var count = 0;
        while (stack.Count > 0 && count < MaxEntries)
        {
            var (dir, depth) = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (Exception) when (depth >= 0)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (count >= MaxEntries)
                {
                    yield break;
                }

                if (Directory.Exists(entry))
                {
                    if (depth < maxDepth && !entry.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        stack.Push((entry, depth + 1));
                    }

                    continue;
                }

                count++;
                yield return entry;
            }
        }
    }
}
