using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Detector de "fake 200" — provider/gateway que devolve HTTP 200 com a
/// falha de billing/conta no envelope ou dentro do próprio content do
/// assistant (o aviso "The account behind this API key doesn't have
/// enough credits..." do Pollinations, por exemplo). Replica o
/// tratamento do OmniRoute (<c>validateQuality.ts</c> #6427/#19278 e o
/// peek streaming #20528): detectado vira
/// <see cref="ProviderHttpException"/> 402 → a retry policy classifica
/// como não-retentável e a cadeia adaptive/fusion avança para o
/// próximo modelo em vez de entregar o aviso como resposta.
/// </summary>
internal static class MaskedProviderError
{
    /// <summary>Teto do prefixo de texto do assistant inspecionado no
    /// stream — o aviso mascarado é curto (~230 chars); prosa real passa
    /// do limite e libera o buffer (OmniRoute <c>MASKED_STREAM_NOTICE_MAX</c>).</summary>
    public const int StreamNoticeMaxChars = 512;

    /// <summary>Teto da resposta INTEIRA para o marcador de billing
    /// vencer o usage reportado — gateways (Pollinations) emitem
    /// completion_tokens até no aviso de crédito, então o gate de
    /// usage sozinho deixava o fake-200 passar.</summary>
    public const int NoticeSizedMaxChars = StreamNoticeMaxChars + 128;

    /// <summary>Termo de crédito/conta esgotado — "enough credits",
    /// "insufficient credits", "credit balance".</summary>
    private static readonly Regex CreditTerm = new(
        "\\b(enough\\s+credits?|insufficient\\s+credits?|out\\s+of\\s+credits?"
        + "|credit\\s+balance|credits?\\s+exhausted)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>Ação de billing pedida — "top up", "recharge",
    /// "purchase credits", "upgrade", "quest" (Pollinations).</summary>
    private static readonly Regex ActionTerm = new(
        "\\b(top\\s+up|recharge|purchase\\s+(more\\s+)?credits?"
        + "|add\\s+credits?|complete\\s+a\\s+quest|upgrade\\s+(your|the|to))\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>Aviso de billing FORTE — termo de crédito esgotado E
    /// ação de billing juntos (o aviso real do Pollinations tem os dois:
    /// "doesn't have enough credits... top up / complete a quest").
    /// Um só marcador pode ser prosa legítima; os dois numa resposta
    /// curta só é o aviso — vence o usage reportado pelo gateway.</summary>
    public static bool MatchesStrongNotice(string content) =>
        CreditTerm.IsMatch(content) && ActionTerm.IsMatch(content);

    /// <summary>Camada-1 (envelope, #6427): termos amplos de esgotamento —
    /// só aplicados nos campos de erro (<c>error/message/detail</c>
    /// top-level), nunca na prosa do assistant.</summary>
    private static readonly Regex EnvelopePattern = new(
        "\\b(insufficient\\s+credit|insufficient\\s+balance|quota\\s+exceeded"
        + "|out\\s+of\\s+credits?|credit\\s+exhausted)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>Camada-2 (content, #19278): deliberadamente MAIS estreita —
    /// só frases de ação de billing ("enough credits", "top up",
    /// "purchase credits"), nunca termos genéricos que aparecem em prosa
    /// legítima. Gate adicional no caller: completion_tokens ausente/≤0 —
    /// resposta real sempre carrega usage.</summary>
    private static readonly Regex ContentPattern = new(
        "\\b(enough\\s+credits?|not\\s+enough\\s+credits?|please\\s+top\\s+up"
        + "|top\\s+up\\s+to\\s+continue|purchase\\s+(more\\s+)?credits?"
        + "|add\\s+credits?\\s+to\\s+continue"
        + "|credit\\s+balance\\s+(is\\s+)?(too\\s+low|exhausted|empty)"
        + "|recharge\\s+(your|the)\\s+account|out\\s+of\\s+funds)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Junta o texto dos campos de erro do envelope de um 200 JSON:
    /// <c>error.message/code/type</c>, <c>error</c> string solta, e
    /// <c>message</c>/<c>detail</c> top-level — espelha
    /// <c>extractEnvelopeErrorText</c>. Nunca olha dentro de
    /// <c>choices[].message.content</c> (prosa não é envelope).
    /// </summary>
    public static string? EnvelopeText(JsonNode? json)
    {
        if (json is not JsonObject obj)
        {
            return null;
        }
        var parts = ErrorFieldTexts(obj[ErrorKey])
            .Concat(StringProps(obj, MessageKey, "detail"))
            .ToList();
        return parts.Count > 0 ? string.Join(' ', parts) : null;
    }

    /// <summary>Textos do campo <c>error</c>: objeto
    /// <c>{message,code,type}</c> ou string solta.</summary>
    private static IEnumerable<string> ErrorFieldTexts(JsonNode? error) =>
        error switch
        {
            JsonObject errObj => StringProps(errObj, MessageKey, "code", "type"),
            JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => [s],
            _ => [],
        };

    /// <summary>Valor textual não-vazio de cada propriedade nomeada.</summary>
    private static IEnumerable<string> StringProps(JsonObject obj, params string[] names) =>
        names.Select(n => obj[n])
            .OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null)
            .OfType<string>();

    /// <summary>O texto do envelope carrega marcador de esgotamento?</summary>
    public static bool MatchesEnvelope(string text) =>
        EnvelopePattern.IsMatch(text);

    /// <summary>O content do assistant é um aviso de billing disfarçado?
    /// Pattern estreito — o caller decide o gate de usage.</summary>
    public static bool MatchesContent(string content) =>
        ContentPattern.IsMatch(content);

    /// <summary>
    /// Monta a exceção de falha mascarada: 402 + corpo OpenAI-shape
    /// (<c>insufficient_quota</c>/<c>credits_exhausted</c>, como o
    /// <c>upstreamFailure</c> do OmniRoute) — classifica como
    /// não-retentável na retry policy e elegível ao avanço da cadeia.
    /// </summary>
    public static ProviderHttpException As402(string snippet, bool midStream)
    {
        var cut = snippet.Length > 200 ? snippet[..200] + "…" : snippet;
        var body = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = $"masked-200: upstream exhaustion marker: {cut}",
                ["type"] = "insufficient_quota",
                ["code"] = "credits_exhausted",
            },
        }.ToJsonString();
        return new ProviderHttpException(
            HttpStatusCode.PaymentRequired, null, body, midStream);
    }

    private const string ErrorKey = "error";
    private const string MessageKey = "message";
}
