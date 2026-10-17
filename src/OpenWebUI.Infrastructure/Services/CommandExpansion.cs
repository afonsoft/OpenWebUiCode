using System.Text;
using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Expansão server-side de commands markdown (SPEC-20261016-commands-v2,
/// RF-002/RF-006 — semântica de <c>command.ts</c> do opencode):
/// <list type="bullet">
/// <item><c>$ARGUMENTS</c> → linha inteira de args;</item>
/// <item><c>$1</c>..<c>$9</c> → token posicional (aspas simples/duplas
/// agrupam); o <b>último</b> placeholder numérico do template absorve o
/// resto dos tokens; <c>$0</c> e <c>$10+</c> são literais;</item>
/// <item>template sem placeholder → args anexados após linha em branco;</item>
/// <item>blocos <c>!`cmd`</c> → executados somente quando
/// <paramref name="allowShell"/> (default off →
/// <c>[shell block omitted: disabled]</c>).</item>
/// </list>
/// </summary>
public static class CommandExpansion
{
    /// <summary>Timeout de um bloco shell (RF-006).</summary>
    public static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Teto da saída capturada de um bloco shell (RF-006).</summary>
    public const int ShellOutputMaxBytes = 8 * 1024;

    /// <summary>Literal emitido quando shell expansion está desligado.</summary>
    public const string ShellDisabledLiteral = "[shell block omitted: disabled]";

    private static readonly Regex ShellBlock = new(
        @"!`([^`]+)`", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Expande <paramref name="template"/> com <paramref name="args"/> e,
    /// quando habilitado, avalia blocos <c>!`shell`</c> em
    /// <paramref name="workdir"/>.
    /// </summary>
    /// <param name="shellRunner">
    /// Executa o comando e devolve a saída (já truncada). Injetado pelo
    /// chamador — o helper não conhece process runner.
    /// </param>
    public static async Task<string> ExpandAsync(
        string template, string args, bool allowShell,
        string? workdir,
        Func<string, string?, CancellationToken, Task<string>>? shellRunner,
        CancellationToken ct = default)
    {
        var expanded = Interpolate(template, args);
        if (!ShellBlock.IsMatch(expanded))
        {
            return expanded;
        }

        var sb = new StringBuilder(expanded.Length + 256);
        var last = 0;
        foreach (Match m in ShellBlock.Matches(expanded))
        {
            sb.Append(expanded, last, m.Index - last);
            var cmd = m.Groups[1].Value.Trim();
            if (!allowShell || shellRunner is null)
            {
                sb.Append(ShellDisabledLiteral);
            }
            else
            {
                var output = await shellRunner(cmd, workdir, ct);
                sb.Append("<shell-output cmd=\"").Append(cmd).Append("\">")
                    .Append(output).Append("</shell-output>");
            }
            last = m.Index + m.Length;
        }
        sb.Append(expanded, last, expanded.Length - last);
        return sb.ToString();
    }

    /// <summary>Interpolação pura (sem shell) — mesma regra do opencode.</summary>
    public static string Interpolate(string template, string args)
    {
        var tokens = Tokenize(args);
        var numeric = HighestNumericPlaceholder(template);
        var sb = new StringBuilder(template.Length + args.Length);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '$')
            {
                if (template.AsSpan(i).StartsWith("$ARGUMENTS", StringComparison.Ordinal))
                {
                    sb.Append(args);
                    i += "$ARGUMENTS".Length - 1;
                    continue;
                }

                // $N com N de um dígito 1..9 NÃO seguido de outro dígito
                // ($10+ são literais); o último placeholder do template
                // absorve os tokens restantes ("rest capture").
                if (i + 1 < template.Length && template[i + 1] is >= '1' and <= '9'
                    && (i + 2 >= template.Length
                        || !char.IsDigit(template[i + 2])))
                {
                    var n = template[i + 1] - '0';
                    var token = n - 1 < tokens.Count ? tokens[n - 1] : string.Empty;
                    if (n == numeric && n - 1 < tokens.Count)
                    {
                        token = string.Join(' ', tokens.Skip(n - 1));
                    }
                    sb.Append(token);
                    i++;
                    continue;
                }
            }
            sb.Append(template[i]);
        }

        var result = sb.ToString();
        // Sem nenhum placeholder: args vão anexados após linha em branco
        // (fallback do opencode para "/cmd x" com template fixo).
        if (!string.IsNullOrWhiteSpace(args)
            && !template.Contains("$ARGUMENTS", StringComparison.Ordinal)
            && numeric == 0)
        {
            result = result.TrimEnd() + "\n\n" + args;
        }
        return result;
    }

    /// <summary>Maior <c>$N</c> (1..9) presente no template — 0 se nenhum.</summary>
    private static int HighestNumericPlaceholder(string template)
    {
        var max = 0;
        for (var i = 0; i + 1 < template.Length; i++)
        {
            if (template[i] == '$' && template[i + 1] is >= '1' and <= '9'
                && (i + 2 >= template.Length
                    || !char.IsDigit(template[i + 2])))
            {
                max = Math.Max(max, template[i + 1] - '0');
            }
        }
        return max;
    }

    /// <summary>Tokeniza args respeitando aspas duplas/simples.</summary>
    private static List<string> Tokenize(string args)
    {
        var tokens = new List<string>();
        var cur = new StringBuilder();
        var quote = '\0';
        foreach (var c in args)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    cur.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (cur.Length > 0)
                {
                    tokens.Add(cur.ToString());
                    cur.Clear();
                }
            }
            else
            {
                cur.Append(c);
            }
        }
        if (cur.Length > 0)
        {
            tokens.Add(cur.ToString());
        }
        return tokens;
    }
}
