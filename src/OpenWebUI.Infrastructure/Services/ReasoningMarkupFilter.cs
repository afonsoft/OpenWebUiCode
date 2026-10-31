using System.Text;
using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Filtro streaming-safe de tags de raciocínio inline e special tokens
/// <c>&lt;|…|&gt;</c> que providers emitem dentro de <c>delta.content</c>.
/// </summary>
/// <remarks>
/// Paridade com o upstream <c>open-webui/open-webui</c>
/// (<c>DEFAULT_REASONING_TAGS</c> + <c>tag_output_handler</c> em
/// <c>backend/open_webui/utils/middleware.py</c>): pares como
/// <c>&lt;think&gt;…&lt;/think&gt;</c> e
/// <c>&lt;|begin_of_thought|&gt;…&lt;|end_of_thought|&gt;</c> têm o texto
/// interno roteado ao canal de reasoning em vez do content; tokens de
/// controle órfãos (harmony/ChatML — <c>&lt;|im_start|&gt;</c>,
/// <c>&lt;|end|&gt;</c>, <c>&lt;|channel|&gt;</c>, <c>&lt;channel|&gt;</c>
/// etc.) são removidos onde aparecem.
/// <para>
/// A variante observada em produção (OmniRoute → Gemini, chat
/// <c>30b15271</c>) é o header de canal harmony malformado
/// <c>&lt;|channel&gt;thought\n&lt;channel|&gt;</c>: tratada como bloco
/// cujo conteúdo (incluindo o nome do canal) vai ao reasoning. No formato
/// harmony completo o header fecha em <c>&lt;|message|&gt;</c> — também
/// aceito como fechamento, preservando a resposta que vem depois.
/// </para>
/// <para>
/// Robustez a chunk boundary: sufixos que podem ser o começo de um
/// marcador ou token são segurados até o próximo <see cref="Feed"/> /
/// <see cref="Flush"/> — o markup nunca vaza metade-resolvido.
/// </para>
/// </remarks>
public sealed class ReasoningMarkupFilter
{
    /// <summary>Bloco de tag aberto além desse tamanho é desistido
    /// (modelo quebrado) — mesmo teto do filtro DSML.</summary>
    private const int MaxBlockLength = 256 * 1024;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Pares de tags de raciocínio (abertura → fechamentos aceitos) —
    /// paridade com <c>DEFAULT_REASONING_TAGS</c> do upstream mais a
    /// variante <c>&lt;|channel&gt;</c> harmony observada em produção.
    /// </summary>
    private static readonly (string Open, string[] Closes)[] TagPairs =
    [
        ("<think>", ["</think>"]),
        ("<thinking>", ["</thinking>"]),
        ("<reason>", ["</reason>"]),
        ("<reasoning>", ["</reasoning>"]),
        ("<thought>", ["</thought>"]),
        ("<Thought>", ["</Thought>"]),
        ("<|begin_of_thought|>", ["<|end_of_thought|>"]),
        ("◁think▷", ["◁/think▷"]),
        // Header de canal harmony: fecha em <channel|> (variante OmniRoute)
        // ou <|message|> (formato padrão — conteúdo após ele é resposta).
        ("<|channel>", ["<channel|>", "<|message|>"]),
    ];

    /// <summary>
    /// Special tokens de controle (harmony, ChatML, llama, Gemini):
    /// <c>&lt;|word|&gt;</c>, <c>&lt;|word&gt;</c> e <c>&lt;word|&gt;</c>.
    /// Marcadores legítimos sem pipe (<c>&lt;div&gt;</c>, <c>&lt;code&gt;</c>)
    /// não casam — nada de over-strip em markup/HTML do usuário.
    /// </summary>
    private static readonly Regex SpecialToken = new(
        "<\\|[A-Za-z_]+\\|?>|<[A-Za-z_]+\\|>",
        RegexOptions.Compiled, RegexTimeout);

    /// <summary>Cauda que é um token incompleto (<c>&lt;|chan</c>,
    /// <c>&lt;channel</c>, <c>&lt;</c> sozinho): segurada até completar.</summary>
    private static readonly Regex IncompleteToken = new(
        "<\\|?[A-Za-z_]*\\|?$", RegexOptions.Compiled, RegexTimeout);

    private readonly bool _keepInnerText;
    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _reasoning = new();

    /// <summary>
    /// Cria o filtro. <paramref name="keepInnerText"/> = <c>true</c> para
    /// canais que já são de raciocínio (<c>reasoning_content</c>,
    /// <c>message.thinking</c>): o texto interno dos blocos permanece na
    /// saída (já é thinking) e só os marcadores/tokens saem.
    /// </summary>
    public ReasoningMarkupFilter(bool keepInnerText = false) =>
        _keepInnerText = keepInnerText;

    /// <summary>
    /// Alimenta um pedaço do stream; devolve só o texto visível seguro.
    /// </summary>
    public string Feed(string piece)
    {
        _pending.Append(piece);
        return Drain(flush: false);
    }

    /// <summary>Fim do stream: solta o resto seguro; bloco não fechado é
    /// descartado como markup (o conteúdo interno já foi ao reasoning).</summary>
    public string Flush() => Drain(flush: true);

    /// <summary>
    /// Drena o raciocínio extraído do content desde a última chamada.
    /// Em modo <c>keepInnerText</c> devolve sempre vazio — o inner text
    /// já sai pelo próprio <see cref="Feed"/>.
    /// </summary>
    public string TakeReasoning()
    {
        var r = _reasoning.ToString();
        _reasoning.Clear();
        return r;
    }

    /// <summary>
    /// Uso one-shot (resposta buffered): devolve o texto limpo e o
    /// raciocínio extraído das tags.
    /// </summary>
    public static (string Text, string Reasoning) Extract(
        string text, bool keepInnerText = false)
    {
        var filter = new ReasoningMarkupFilter(keepInnerText);
        var visible = filter.Feed(text) + filter.Flush();
        return (visible, filter.TakeReasoning());
    }

    /// <summary>
    /// Sanitização one-shot para persistência (defesa em profundidade):
    /// remove marcadores e tokens; o raciocínio extraído é descartado —
    /// nesse estágio não há mais canal para ele.
    /// </summary>
    public static string Strip(string text) => Extract(text).Text;

    private string Drain(bool flush)
    {
        var text = _pending.ToString();
        _pending.Clear();
        var output = new StringBuilder();
        var pos = 0;
        while (pos < text.Length)
        {
            var (openIdx, pairIndex) = EarliestOpen(text, pos);
            if (openIdx < 0)
            {
                break;
            }
            EmitVisible(output, text, pos, openIdx);
            var pair = TagPairs[pairIndex];
            var innerStart = openIdx + pair.Open.Length;
            var (closeIdx, closeLen) = EarliestClose(text, innerStart, pair.Closes);
            if (closeIdx >= 0)
            {
                EmitReasoning(output, text[innerStart..closeIdx]);
                pos = closeIdx + closeLen;
                continue;
            }
            var tail = text.Length - openIdx;
            if (!flush && tail <= MaxBlockLength)
            {
                _pending.Append(text, openIdx, tail);
                return output.ToString();
            }
            // Bloco não fechado no fim do stream (ou gigante demais):
            // o interno acumulado vai ao reasoning; o markup nunca vaza.
            EmitReasoning(output, text[innerStart..]);
            pos = text.Length;
        }
        // Sem bloco aberto restante: emite tudo menos o sufixo que pode
        // ser o começo de um marcador/token.
        var hold = flush ? 0 : HeldSuffixLength(text, pos);
        EmitVisible(output, text, pos, text.Length - hold);
        if (hold > 0)
        {
            _pending.Append(text, text.Length - hold, hold);
        }
        return output.ToString();
    }

    /// <summary>Texto visível: sem special tokens órfãos.</summary>
    private void EmitVisible(StringBuilder output, string text, int from, int to)
    {
        if (to <= from)
        {
            return;
        }
        output.Append(SpecialToken.Replace(text[from..to], string.Empty));
    }

    /// <summary>Texto interno de bloco de raciocínio: sem tokens órfãos;
    /// vai ao bucket de reasoning (modo content) ou à própria saída
    /// (modo keepInner — o canal já é o reasoning).</summary>
    private void EmitReasoning(StringBuilder output, string inner)
    {
        var clean = SpecialToken.Replace(inner, string.Empty);
        if (clean.Length == 0)
        {
            return;
        }
        if (_keepInnerText)
        {
            output.Append(clean);
        }
        else
        {
            _reasoning.Append(clean);
        }
    }

    /// <summary>Índice da abertura de tag mais cedo no texto a partir de
    /// <paramref name="pos"/>; -1 quando não há nenhuma.</summary>
    private static (int Index, int PairIndex) EarliestOpen(string text, int pos)
    {
        var best = -1;
        var bestPair = -1;
        for (var i = 0; i < TagPairs.Length; i++)
        {
            var idx = text.IndexOf(TagPairs[i].Open, pos, StringComparison.Ordinal);
            if (idx >= 0 && (best < 0 || idx < best))
            {
                best = idx;
                bestPair = i;
            }
        }
        return (best, bestPair);
    }

    /// <summary>Fechamento mais cedo entre os aceitos do par aberto.</summary>
    private static (int Index, int Length) EarliestClose(
        string text, int pos, string[] closes)
    {
        var best = -1;
        var bestLen = 0;
        foreach (var close in closes)
        {
            var idx = text.IndexOf(close, pos, StringComparison.Ordinal);
            if (idx >= 0 && (best < 0 || idx < best))
            {
                best = idx;
                bestLen = close.Length;
            }
        }
        return (best, bestLen);
    }

    /// <summary>Tamanho do sufixo de <paramref name="text"/> (depois de
    /// <paramref name="pos"/>) que pode ser o começo de uma abertura de
    /// tag, de um fechamento órfão ou de um special token — não pode ser
    /// emitido ainda porque pode completar marcador no próximo chunk.</summary>
    private static int HeldSuffixLength(string text, int pos)
    {
        var tail = text.AsSpan(pos);
        var best = 0;
        // Prefixo de qualquer marcador conhecido (aberturas + fechamentos).
        foreach (var pair in TagPairs)
        {
            best = Math.Max(best, MarkerPrefixLength(tail, pair.Open));
            foreach (var close in pair.Closes)
            {
                best = Math.Max(best, MarkerPrefixLength(tail, close));
            }
        }
        // Token especial incompleto na cauda ("<|chan", "<channel", "<").
        var m = IncompleteToken.Match(text, pos);
        if (m.Success && m.Index + m.Length == text.Length)
        {
            best = Math.Max(best, m.Length);
        }
        return best;
    }

    /// <summary>Maior sufixo de <paramref name="tail"/> que é prefixo de
    /// <paramref name="marker"/> (sem contar o marcador inteiro — esse já
    /// teria sido pego pelo scan).</summary>
    private static int MarkerPrefixLength(ReadOnlySpan<char> tail, string marker)
    {
        var max = Math.Min(marker.Length - 1, tail.Length);
        for (var len = max; len > 0; len--)
        {
            if (tail[^len..].SequenceEqual(marker.AsSpan(0, len)))
            {
                return len;
            }
        }
        return 0;
    }
}
