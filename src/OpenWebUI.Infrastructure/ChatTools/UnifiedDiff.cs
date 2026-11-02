using System.Text;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Diff unificado por linhas (SPEC-20261007-chat-agent-parity RF-011):
/// LCS simples com hunks de 3 linhas de contexto, formato
/// <c>--- a/path +++ b/path @@ -s,c +s,c @@</c>. Teto de 4M de pares de
/// linhas na matriz — acima disso emite diff ingênuo (-/+ inteiro truncado).
/// </summary>
public static class UnifiedDiff
{
    /// <summary>Contexto por hunk.</summary>
    private const int Context = 3;

    /// <summary>Teto da matriz LCS (linhas old × linhas new).</summary>
    private const long MaxPairs = 4_000_000;

    /// <summary>Resultado de um diff: texto unificado + contagens.</summary>
    public sealed record Result(string Text, int Added, int Removed);

    /// <summary>
    /// Computa o diff unificado de <paramref name="oldText"/> →
    /// <paramref name="newText"/> rotulado com <paramref name="path"/>.
    /// </summary>
    public static Result Compute(string path, string? oldText, string newText)
    {
        var oldLines = Split(oldText ?? string.Empty);
        var newLines = Split(newText);

        if (oldLines.SequenceEqual(newLines))
        {
            return new Result(string.Empty, 0, 0);
        }

        if ((long)oldLines.Length * newLines.Length > MaxPairs)
        {
            return Naive(path, oldLines, newLines);
        }

        var ops = EditScript(oldLines, newLines);
        var text = Emit(path, ops, oldLines, newLines);
        return new Result(text, ops.Count(o => o == Op.Add), ops.Count(o => o == Op.Del));
    }

    private enum Op : byte { Keep, Del, Add }

    /// <summary>Tabela DP de LCS (dp[i,j] = maior sufixo comum a partir de i,j).</summary>
    private static int[,] ComputeLcsTable(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = a[i] == b[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }
        return dp;
    }

    /// <summary>Script de edição por LCS (DP) sobre as linhas.</summary>
    private static List<Op> EditScript(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var dp = ComputeLcsTable(a, b);

        var ops = new List<Op>(n + m);
        var x = 0;
        var y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                ops.Add(Op.Keep);
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                ops.Add(Op.Del);
                x++;
            }
            else
            {
                ops.Add(Op.Add);
                y++;
            }
        }

        while (x++ < n)
        {
            ops.Add(Op.Del);
        }

        while (y++ < m)
        {
            ops.Add(Op.Add);
        }

        return ops;
    }

    /// <summary>Emite hunks com Context linhas de margem.</summary>
    private static string Emit(string path, List<Op> ops, string[] a, string[] b)
    {
        // Índices (em ops) onde há mudança; agrupa em janelas com contexto.
        var changed = ops
            .Select((op, i) => (op, i))
            .Where(t => t.op != Op.Keep)
            .Select(t => t.i)
            .ToList();
        if (changed.Count == 0)
        {
            return string.Empty;
        }

        var hunks = GroupHunks(ops, changed);

        var sb = new StringBuilder();
        sb.Append("--- a/").Append(path).Append('\n')
            .Append("+++ b/").Append(path).Append('\n');
        var ai = 0;
        var bi = 0;
        var pos = 0;
        var body = new StringBuilder();
        foreach (var (hs, he) in hunks)
        {
            AdvanceTo(ops, hs, ref pos, ref ai, ref bi);
            EmitHunk(new HunkCtx
            {
                Sb = sb, Body = body, Ops = ops, A = a, B = b,
                Hs = hs, He = he, Pos = pos, Ai = ai, Bi = bi,
            }, c => { pos = c.Pos; ai = c.Ai; bi = c.Bi; });
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Agrupa os índices com mudança em janelas de
    /// <see cref="Context"/> linhas de margem.</summary>
    private static List<(int Start, int End)> GroupHunks(
        List<Op> ops, List<int> changed)
    {
        var hunks = new List<(int Start, int End)>();
        var start = Math.Max(0, changed[0] - Context);
        var end = Math.Min(ops.Count - 1, changed[0] + Context);
        foreach (var idx in changed.Skip(1))
        {
            if (idx - end <= Context + 1)
            {
                end = Math.Min(ops.Count - 1, idx + Context);
            }
            else
            {
                hunks.Add((start, end));
                start = Math.Max(0, idx - Context);
                end = Math.Min(ops.Count - 1, idx + Context);
            }
        }

        hunks.Add((start, end));
        return hunks;
    }

    /// <summary>Avança os cursores ai/bi pelas ops anteriores ao hunk.</summary>
    private static void AdvanceTo(
        List<Op> ops, int target, ref int pos, ref int ai, ref int bi)
    {
        while (pos < target)
        {
            if (ops[pos] == Op.Keep)
            {
                ai++;
                bi++;
            }
            else if (ops[pos] == Op.Del)
            {
                ai++;
            }
            else
            {
                bi++;
            }

            pos++;
        }
    }

    /// <summary>Emite um hunk <c>@@ -oldStart,oldCount +newStart,newCount @@</c>
    /// consumindo as ops da janela e avançando os cursores.</summary>
    /// <summary>Estado mutável do hunk em emissão (cursores pos/ai/bi
    /// viajam por referência dentro do objeto — S107).</summary>
    private sealed record HunkCtx
    {
        public required StringBuilder Sb { get; init; }
        public required StringBuilder Body { get; init; }
        public required List<Op> Ops { get; init; }
        public required string[] A { get; init; }
        public required string[] B { get; init; }
        public required int Hs { get; init; }
        public required int He { get; init; }
        public int Pos { get; set; }
        public int Ai { get; set; }
        public int Bi { get; set; }
    }

    private static void EmitHunk(HunkCtx c, Action<HunkCtx> flush)
    {
        var oldStart = c.Ai + 1;
        var newStart = c.Bi + 1;
        var oldCount = 0;
        var newCount = 0;
        c.Body.Clear();
        for (var i = c.Hs; i <= c.He && c.Pos <= c.He; i++, c.Pos++)
        {
            switch (c.Ops[i])
            {
                case Op.Keep:
                    c.Body.Append(' ').Append(c.A[c.Ai++]).Append('\n');
                    c.Bi++;
                    oldCount++;
                    newCount++;
                    break;
                case Op.Del:
                    c.Body.Append('-').Append(c.A[c.Ai++]).Append('\n');
                    oldCount++;
                    break;
                case Op.Add:
                    c.Body.Append('+').Append(c.B[c.Bi++]).Append('\n');
                    newCount++;
                    break;
            }
        }

        c.Sb.Append("@@ -").Append(oldCount == 0 ? oldStart - 1 : oldStart)
            .Append(',').Append(oldCount)
            .Append(" +").Append(newCount == 0 ? newStart - 1 : newStart)
            .Append(',').Append(newCount)
            .Append(" @@\n").Append(c.Body);
        flush(c);
    }

    /// <summary>Fallback para arquivos grandes: tudo removido/adicionado.</summary>
    private static Result Naive(string path, string[] a, string[] b)
    {
        var sb = new StringBuilder()
            .Append("--- a/").Append(path).Append('\n')
            .Append("+++ b/").Append(path).Append('\n')
            .Append("@@ arquivo reescrito por inteiro @@\n");
        foreach (var l in a.Take(400))
        {
            sb.Append('-').Append(l).Append('\n');
        }

        foreach (var l in b.Take(400))
        {
            sb.Append('+').Append(l).Append('\n');
        }

        sb.Append("[diff truncado]");
        return new Result(sb.ToString(), b.Length, a.Length);
    }

    private static string[] Split(string text) =>
        text.Replace("\r\n", "\n").Split('\n');
}
