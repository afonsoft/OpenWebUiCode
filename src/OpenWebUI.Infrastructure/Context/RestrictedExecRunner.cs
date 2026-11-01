using System.Text;
using System.Text.RegularExpressions;

using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Infrastructure.Context;

/// <summary>
/// Executor restrito da tool <c>instant_context</c> (SPEC-20261031-agent-
/// prompt-pack RF-003): allowlist estrita de comandos de coleta —
/// <c>rg &lt;pattern&gt;</c>, <c>readfile &lt;path&gt; &lt;a&gt; &lt;b&gt;</c> e
/// <c>tree &lt;path&gt; [depth]</c> — tudo confinado ao workspace da run e
/// com limites de comandos/turno e de saída. Nenhum shell é invocado.
/// </summary>
public sealed class RestrictedExecRunner
{
    /// <summary>Máximo de comandos por execução (turno).</summary>
    public const int MaxCommands = 8;

    /// <summary>Máximo de linhas devolvidas por comando.</summary>
    public const int MaxOutputLines = 80;

    /// <summary>Profundidade máxima de <c>tree</c>.</summary>
    public const int MaxTreeDepth = 4;

    /// <summary>Máximo de matches de um <c>rg</c>.</summary>
    private const int MaxMatches = 60;

    /// <summary>Comando fora da allowlist / mal formado.</summary>
    public sealed record CommandError(string Command, string Error);

    /// <summary>Trecho coletado: arquivo + range de linhas + conteúdo.</summary>
    public sealed record Snippet(string Path, int Start, int End, string Text);

    /// <summary>Resultado de um lote: snippets válidos + erros por comando.</summary>
    public sealed record BatchResult(List<Snippet> Snippets, List<CommandError> Errors);

    /// <summary>
    /// Executa o lote de comandos (no máx. <see cref="MaxCommands"/>) contra
    /// o workspace. Cada comando é uma linha; erros não abortam o lote.
    /// </summary>
    public BatchResult Run(string workspace, IReadOnlyList<string> commands)
    {
        var snippets = new List<Snippet>();
        var errors = new List<CommandError>();
        foreach (var raw in commands.Take(MaxCommands))
        {
            var cmd = raw.Trim();
            try
            {
                snippets.AddRange(RunOne(workspace, cmd));
            }
            catch (RestrictedExecException ex)
            {
                errors.Add(new CommandError(cmd, ex.Message));
            }
        }
        return new BatchResult(snippets, errors);
    }

    private IEnumerable<Snippet> RunOne(string workspace, string cmd)
    {
        var space = cmd.IndexOf(' ');
        var verb = space < 0 ? cmd : cmd[..space];
        var rest = space < 0 ? string.Empty : cmd[(space + 1)..].Trim();
        return verb switch
        {
            "rg" => RunGrep(workspace, rest),
            "readfile" => RunReadFile(workspace, rest),
            "tree" => RunTree(workspace, rest),
            _ => throw new RestrictedExecException(
                $"comando não permitido: '{verb}' (use rg, readfile ou tree)"),
        };
    }

    /// <summary><c>rg &lt;pattern&gt;</c> — busca regex linha a linha.</summary>
    private static IEnumerable<Snippet> RunGrep(string workspace, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new RestrictedExecException("rg exige um padrão");
        }
        Regex re;
        try
        {
            re = new Regex(pattern, RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            throw new RestrictedExecException($"regex inválida: {ex.Message}");
        }
        var matches = 0;
        foreach (var file in EnumerateFiles(workspace))
        {
            foreach (var snippet in GrepFile(workspace, file, re))
            {
                yield return snippet;
                if (++matches >= MaxMatches)
                {
                    yield break;
                }
            }
            if (matches >= MaxMatches)
            {
                yield break;
            }
        }
    }

    /// <summary>Matches do pattern num arquivo → snippets agrupados.</summary>
    private static IEnumerable<Snippet> GrepFile(
        string workspace, string file, Regex re)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        var found = new List<(int Line, string Text)>();
        for (var i = 0; i < lines.Length && found.Count < 10; i++)
        {
            if (re.IsMatch(lines[i]))
            {
                found.Add((i + 1, lines[i]));
            }
        }
        var rel = Path.GetRelativePath(workspace, file);
        foreach (var g in found.GroupConsecutive())
        {
            var text = string.Join('\n',
                g.Select(m => $"{m.Line}: {m.Text}"));
            yield return new Snippet(rel, g[0].Line, g[^1].Line, text);
        }
    }

    /// <summary><c>readfile &lt;path&gt; &lt;a&gt; &lt;b&gt;</c> — linhas a–b.</summary>
    private static IEnumerable<Snippet> RunReadFile(string workspace, string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !int.TryParse(parts[1], out var a)
            || !int.TryParse(parts[2], out var b))
        {
            throw new RestrictedExecException(
                "uso: readfile <path> <linha-ini> <linha-fim>");
        }
        var full = WorkspaceFiles.ResolveInsideFinal(
            workspace, parts[0], out var error);
        if (full is null || !File.Exists(full))
        {
            throw new RestrictedExecException(error is { Length: > 0 }
                ? error
                : $"arquivo '{parts[0]}' não existe");
        }
        if (a < 1 || b < a)
        {
            throw new RestrictedExecException("range de linhas inválido");
        }
        var lines = File.ReadLines(full).Skip(a - 1)
            .Take(Math.Min(b - a + 1, MaxOutputLines)).ToList();
        var text = string.Join('\n',
            lines.Select((l, i) => $"{a + i}: {l}"));
        yield return new Snippet(parts[0], a, a + lines.Count - 1, text);
    }

    /// <summary><c>tree &lt;path&gt; [depth]</c> — árvore de diretório.</summary>
    private static IEnumerable<Snippet> RunTree(string workspace, string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rel = parts.Length > 0 ? parts[0] : ".";
        var depth = parts.Length > 1 && int.TryParse(parts[1], out var d)
            ? Math.Clamp(d, 1, MaxTreeDepth)
            : 2;
        var full = WorkspaceFiles.ResolveInsideFinal(
            workspace, rel, out var error);
        if (full is null || !Directory.Exists(full))
        {
            throw new RestrictedExecException(error is { Length: > 0 }
                ? error
                : $"diretório '{rel}' não existe");
        }
        var sb = new StringBuilder();
        var lines = 0;
        foreach (var entry in EnumerateTree(full, depth))
        {
            sb.AppendLine(Path.GetRelativePath(full, entry));
            if (++lines >= MaxOutputLines)
            {
                sb.AppendLine("…");
                break;
            }
        }
        yield return new Snippet(rel, 1, lines, sb.ToString().TrimEnd());
    }

    private static IEnumerable<string> EnumerateTree(string root, int depth)
    {
        if (depth < 1)
        {
            yield break;
        }
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(root).ToList();
        }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (var entry in entries.OrderBy(e => e))
        {
            yield return entry;
            if (Directory.Exists(entry))
            {
                foreach (var sub in EnumerateTree(entry, depth - 1))
                {
                    yield return sub;
                }
            }
        }
    }

    /// <summary>Arquivos de texto do workspace (ignora bin/dirs pesados).</summary>
    private static IEnumerable<string> EnumerateFiles(string workspace)
    {
        var skip = new[] { ".git", "bin", "obj", "node_modules", ".vs" };
        var stack = new Stack<string>();
        stack.Push(workspace);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (!skip.Contains(Path.GetFileName(entry)))
                    {
                        stack.Push(entry);
                    }
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>Erro de comando restrito (não aborta o lote).</summary>
    public sealed class RestrictedExecException(string message)
        : Exception(message);
}

/// <summary>Agrupa matches em blocos de linhas consecutivas.</summary>
internal static class ConsecutiveGrouper
{
    public static IEnumerable<List<(int Line, string Text)>> GroupConsecutive(
        this IEnumerable<(int Line, string Text)> matches)
    {
        List<(int, string)>? block = null;
        foreach (var m in matches)
        {
            if (block is not null && m.Line == block[^1].Item1 + 1)
            {
                block.Add(m);
            }
            else
            {
                if (block is not null)
                {
                    yield return block;
                }
                block = [m];
            }
        }
        if (block is not null)
        {
            yield return block;
        }
    }
}
