namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>Um hunk de update: bloco contíguo esperado → substituto.</summary>
/// <param name="Expected">Linhas contexto/`-` que devem aparecer contíguas no arquivo.</param>
/// <param name="Replacement">Linhas contexto/`+` que substituem o bloco.</param>
public sealed record PatchHunk(IReadOnlyList<string> Expected, IReadOnlyList<string> Replacement);

/// <summary>Operação de um arquivo dentro do patch.</summary>
/// <param name="Kind">add | update | delete.</param>
/// <param name="Path">Path relativo alvo.</param>
/// <param name="MoveTo">Destino do rename (só update, <c>*** Move to:</c>).</param>
/// <param name="AddLines">Conteúdo novo (só add).</param>
/// <param name="Hunks">Blocos de mudança (só update).</param>
public sealed record PatchOp(
    string Kind, string Path, string? MoveTo,
    IReadOnlyList<string>? AddLines, IReadOnlyList<PatchHunk>? Hunks);

/// <summary>
/// Parser + aplicador do formato OpenAI apply_patch
/// (SPEC-20261009-worktree-format-hooks, RF-003):
/// <code>*** Begin Patch
/// *** Add File: path          (+linhas de conteúdo)
/// *** Update File: path       (*** Move to: novo-path opcional; @@ separa hunks;
///                             ' ' contexto, '-' removido, '+' adicionado)
/// *** Delete File: path
/// *** End Patch</code>
/// Hunks casam por igualdade exata de linha, com fallbacks
/// trim-end/trim-total (como o apply_patch do Codex); um hunk precisa
/// casar exatamente uma vez — ambíguo ou ausente é erro.
/// </summary>
public static class ApplyPatch
{
    /// <summary>Parse do patch. Devolve as ops ou null com <paramref name="error"/>.</summary>
    public static List<PatchOp>? Parse(string patch, out string error)
    {
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i]))
        {
            i++;
        }
        if (i >= lines.Length || lines[i].Trim() != "*** Begin Patch")
        {
            error = "patch inválido — deve começar com '*** Begin Patch'.";
            return null;
        }
        i++;

        var ops = new List<PatchOp>();
        var ended = false;
        while (i < lines.Length && !ended)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed == "*** End of File")
            {
                // "End of File" é terminador opcional de seção (compat
                // com o formato codex).
                i++;
                continue;
            }

            if (trimmed == "*** End Patch")
            {
                ended = true;
                i++;
                continue;
            }

            var op = ParseOp(lines, ref i, out error);
            if (op is null)
            {
                return null;
            }
            ops.Add(op);
        }

        if (!ended)
        {
            error = "patch inválido — falta '*** End Patch'.";
            return null;
        }
        if (ops.Count == 0)
        {
            error = "patch vazio — nenhuma operação de arquivo.";
            return null;
        }

        error = "";
        return ops;
    }

    /// <summary>Parse da operação na linha atual (<c>*** Add|Update|Delete File:</c>).</summary>
    private static PatchOp? ParseOp(string[] lines, ref int i, out string error)
    {
        var line = lines[i];
        if (line.StartsWith("*** Add File: ", StringComparison.Ordinal))
        {
            return ParseAddFile(lines, ref i, line["*** Add File: ".Length..].Trim(), out error);
        }

        if (line.StartsWith("*** Update File: ", StringComparison.Ordinal))
        {
            return ParseUpdateFile(lines, ref i, line["*** Update File: ".Length..].Trim(), out error);
        }

        if (line.StartsWith("*** Delete File: ", StringComparison.Ordinal))
        {
            i++;
            error = "";
            return new PatchOp("delete", line["*** Delete File: ".Length..].Trim(), null, null, null);
        }

        error = $"patch inválido — linha inesperada '{line}'.";
        return null;
    }

    /// <summary><c>*** Add File:</c> — consome as linhas <c>+</c> até o próximo <c>*** </c>.</summary>
    private static PatchOp? ParseAddFile(string[] lines, ref int i, string path, out string error)
    {
        var content = new List<string>();
        i++;
        while (i < lines.Length
            && !lines[i].StartsWith("*** ", StringComparison.Ordinal))
        {
            var l = lines[i];
            if (!l.StartsWith('+'))
            {
                error = $"Add File '{path}': linha deve começar com '+' — '{l}'.";
                return null;
            }
            content.Add(l[1..]);
            i++;
        }
        error = "";
        return new PatchOp("add", path, null, content, null);
    }

    /// <summary><c>*** Update File:</c> — <c>*** Move to:</c> opcional + hunks separados por <c>@@</c>.</summary>
    private static PatchOp? ParseUpdateFile(string[] lines, ref int i, string path, out string error)
    {
        i++;
        string? moveTo = null;
        if (i < lines.Length && lines[i].StartsWith("*** Move to: ", StringComparison.Ordinal))
        {
            moveTo = lines[i]["*** Move to: ".Length..].Trim();
            i++;
        }

        var hunks = ParseHunks(lines, ref i, path, out error);
        if (hunks is null)
        {
            return null;
        }
        if (hunks.Count == 0 && moveTo is null)
        {
            error = $"Update File '{path}': nenhum hunk nem '*** Move to:'.";
            return null;
        }
        error = "";
        return new PatchOp("update", path, moveTo, null, hunks);
    }

    /// <summary>
    /// Hunks de um update até o próximo <c>*** </c>: <c>@@</c> fecha o bloco,
    /// <c>' '</c> contexto, <c>-</c> removido, <c>+</c> adicionado.
    /// </summary>
    private static List<PatchHunk>? ParseHunks(string[] lines, ref int i, string path, out string error)
    {
        var hunks = new List<PatchHunk>();
        var expected = new List<string>();
        var replacement = new List<string>();
        while (i < lines.Length
            && !lines[i].StartsWith("*** ", StringComparison.Ordinal))
        {
            var l = lines[i];
            if (l.StartsWith("@@", StringComparison.Ordinal))
            {
                // Separador de hunk — fecha o bloco atual.
                FlushHunk(hunks, ref expected, ref replacement);
                i++;
                continue;
            }
            if (!AppendHunkLine(l, expected, replacement))
            {
                error = $"Update File '{path}': linha inválida no hunk — '{l}' (use ' ', '-' ou '+').";
                return null;
            }
            i++;
        }
        FlushHunk(hunks, ref expected, ref replacement);
        error = "";
        return hunks;
    }

    /// <summary>Fecha o hunk em acumulação (quando há linhas) e reinicia os buffers.</summary>
    private static void FlushHunk(
        List<PatchHunk> hunks, ref List<string> expected, ref List<string> replacement)
    {
        if (expected.Count > 0 || replacement.Count > 0)
        {
            hunks.Add(new PatchHunk(expected, replacement));
            expected = new List<string>();
            replacement = new List<string>();
        }
    }

    /// <summary>Classifica a linha do hunk em expected/replacement; false quando inválida.</summary>
    private static bool AppendHunkLine(string l, List<string> expected, List<string> replacement)
    {
        if (l.StartsWith(' '))
        {
            expected.Add(l[1..]);
            replacement.Add(l[1..]);
        }
        else if (l.StartsWith('-'))
        {
            expected.Add(l[1..]);
        }
        else if (l.StartsWith('+'))
        {
            replacement.Add(l[1..]);
        }
        else
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Aplica os hunks de um update ao texto. Devolve o novo texto ou
    /// null com <paramref name="error"/> (hunk não encontrado ou ambíguo).
    /// </summary>
    public static string? ApplyHunks(string oldText, IReadOnlyList<PatchHunk> hunks, out string error)
    {
        var fileLines = oldText.Replace("\r\n", "\n").Split('\n').ToList();
        foreach (var hunk in hunks)
        {
            if (hunk.Expected.Count == 0)
            {
                // Hunk puro de adição: anexa no fim do arquivo (antes do
                // '\n' final implícito quando o texto termina em newline).
                var insertAt = fileLines.Count > 0 && fileLines[^1] == ""
                    ? fileLines.Count - 1
                    : fileLines.Count;
                fileLines.InsertRange(insertAt, hunk.Replacement);
                continue;
            }

            var idx = FindUnique(fileLines, hunk.Expected, out var errorDetail);
            if (idx < 0)
            {
                error = $"hunk não encontrado ou ambíguo: '{hunk.Expected[0]}' ({errorDetail})";
                return null;
            }

            fileLines.RemoveRange(idx, hunk.Expected.Count);
            fileLines.InsertRange(idx, hunk.Replacement);
        }

        error = "";
        return string.Join('\n', fileLines);
    }

    /// <summary>Índice único onde <paramref name="expected"/> casa; -1 com detalhe.</summary>
    private static int FindUnique(List<string> fileLines, IReadOnlyList<string> expected, out string detail)
    {
        foreach (var (mode, match) in new (string, Func<string, string, bool>)[]
        {
            ("exato", (a, b) => a == b),
            ("trim-end", (a, b) => a.TrimEnd() == b.TrimEnd()),
            ("trim", (a, b) => a.Trim() == b.Trim()),
        })
        {
            var found = -1;
            var count = 0;
            for (var i = 0; i + expected.Count <= fileLines.Count; i++)
            {
                var ok = true;
                for (var j = 0; j < expected.Count; j++)
                {
                    if (!match(fileLines[i + j], expected[j]))
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                {
                    count++;
                    if (found < 0)
                    {
                        found = i;
                    }
                }
            }

            if (count == 1)
            {
                detail = mode;
                return found;
            }
            if (count > 1)
            {
                detail = $"{count} ocorrências ({mode})";
                return -1;
            }
        }

        detail = "0 ocorrências";
        return -1;
    }
}
