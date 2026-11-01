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

        var resolved = ResolveFinalLink(full, workspace);
        if (resolved is null || !AncestorsInside(resolved, workspace))
        {
            error = $"Caminho '{relative}' aponta para fora do workspace — negado.";
            return null;
        }

        error = string.Empty;
        return resolved;
    }

    /// <summary>Resolve o link do nó final quando ele é symlink —
    /// <c>null</c> = o alvo real escapa do jail.</summary>
    private static string? ResolveFinalLink(string full, string workspace)
    {
        FileSystemInfo? node = null;
        if (Directory.Exists(full))
        {
            node = new DirectoryInfo(full);
        }
        else if (File.Exists(full))
        {
            node = new FileInfo(full);
        }
        if (node?.LinkTarget is null)
        {
            return full;
        }
        var target = node.ResolveLinkTarget(returnFinalTarget: true);
        return target is not null
            && CommandRiskClassifier.PathInside(workspace, target.FullName)
            ? target.FullName
            : null;
    }

    /// <summary>Cada ancestral do alvo, até a raiz do workspace, não pode
    /// ser um link que escape do jail.</summary>
    private static bool AncestorsInside(string full, string workspace)
    {
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
                    return false;
                }
            }
            ancestor = ancestor.Parent;
        }
        return true;
    }

    /// <summary>
    /// <see cref="ResolveInsideFinal"/> com escape autorizado
    /// (SPEC-20261018-permissions-v2 RF-003): quando o jail nega, tenta o
    /// <see cref="ExternalAccessRegistry"/> — o gate libera paths fora do
    /// workdir quando a regra <c>external_directory</c> do usuário é
    /// <c>allow</c> ou o dono aprovou o prompt.
    /// </summary>
    public static string? ResolveInsideOrExternal(
        string workspace, string? relative, ExternalAccessRegistry? external,
        string? runId, out string error)
    {
        var full = ResolveInsideFinal(workspace, relative, out error);
        if (full is not null)
        {
            return full;
        }
        var allowed = external?.ResolveAllowed(runId, workspace, relative);
        if (allowed is not null)
        {
            error = string.Empty;
            return allowed;
        }
        return null;
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
            i = AppendGlobToken(sb, pattern, i);
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.IgnoreCase, RegexDefaults.MatchTimeout);
    }

    /// <summary>Emite o regex do token em <paramref name="i"/> e devolve a
    /// próxima posição do padrão.</summary>
    private static int AppendGlobToken(StringBuilder sb, string pattern, int i)
    {
        return pattern[i] switch
        {
            '*' => AppendGlobStar(sb, pattern, i),
            '?' => AppendAndNext(sb, "[^/]", i + 1),
            '[' => AppendCharClass(sb, pattern, i),
            '{' => AppendAlternation(sb, pattern, i),
            var c => AppendAndNext(sb, Regex.Escape(c.ToString()), i + 1),
        };
    }

    private static int AppendAndNext(StringBuilder sb, string text, int next)
    {
        sb.Append(text);
        return next;
    }

    private static int AppendGlobStar(StringBuilder sb, string pattern, int i)
    {
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

            return i;
        }

        sb.Append("[^/]*");
        return i + 1;
    }

    private static int AppendCharClass(StringBuilder sb, string pattern, int i)
    {
        var end = pattern.IndexOf(']', i + 1);
        if (end < 0)
        {
            sb.Append("\\[");
            return i + 1;
        }

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
        return end + 1;
    }

    private static int AppendAlternation(StringBuilder sb, string pattern, int i)
    {
        var close = pattern.IndexOf('}', i + 1);
        if (close < 0)
        {
            sb.Append("\\{");
            return i + 1;
        }

        var alts = pattern[(i + 1)..close]
            .Split(',')
            .Select(Regex.Escape);
        sb.Append('(').Append(string.Join('|', alts)).Append(')');
        return close + 1;
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
            foreach (var entry in SafeList(dir))
            {
                if (count >= MaxEntries)
                {
                    yield break;
                }

                if (Directory.Exists(entry))
                {
                    PushSubDir(stack, entry, depth, maxDepth);
                    continue;
                }

                count++;
                yield return entry;
            }
        }
    }

    /// <summary>Lista um diretório; falha de IO rende enumeração vazia —
    /// a varredura segue com os demais ramos.</summary>
    private static IEnumerable<string> SafeList(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void PushSubDir(
        Stack<(string Dir, int Depth)> stack, string entry, int depth, int maxDepth)
    {
        if (depth < maxDepth && !entry.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            stack.Push((entry, depth + 1));
        }
    }
}
