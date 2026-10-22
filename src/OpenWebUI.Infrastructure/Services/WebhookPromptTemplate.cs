using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Exceção de template de webhook (SPEC-20261021-harness-seams RF-D2):
/// carrega apenas o nome do campo faltante — nunca o payload (sem PII).
/// </summary>
public sealed class WebhookTemplateException(string field)
    : Exception($"campo '{field}' ausente no payload")
{
    /// <summary>Caminho do campo faltante.</summary>
    public string Field { get; } = field;
}

/// <summary>
/// Engine de template de webhook (RF-D2): placeholders
/// <c>{{campo.path}}</c> resolvidos do payload JSON por navegação de
/// propriedades (escalares viram texto, objetos viram JSON compacto);
/// <c>{{$raw}}</c> injeta o payload inteiro. Resultado limitado a 4KB.
/// Engine deliberadamente mínima (sem lógica) — placeholder simples
/// conforme recomendado na SPEC.
/// </summary>
public static partial class WebhookPromptTemplate
{
    /// <summary>Tamanho máximo do prompt interpolado.</summary>
    public const int MaxRenderedChars = 4096;

    /// <summary>
    /// Renderiza o template sobre o payload. Lança
    /// <see cref="WebhookTemplateException"/> quando um campo não existe.
    /// </summary>
    public static string Render(string template, JsonElement payload)
    {
        var rendered = PlaceholderRegex().Replace(template, m =>
        {
            var path = m.Groups[1].Value.Trim();
            if (path == "$raw")
            {
                return payload.GetRawText();
            }

            var current = payload;
            foreach (var seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind == JsonValueKind.Object
                    && current.TryGetProperty(seg, out var next))
                {
                    current = next;
                }
                else if (current.ValueKind == JsonValueKind.Array
                    && int.TryParse(seg, out var idx)
                    && idx >= 0 && idx < current.GetArrayLength())
                {
                    current = current[idx];
                }
                else
                {
                    throw new WebhookTemplateException(path);
                }
            }

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                JsonValueKind.Object or JsonValueKind.Array =>
                    current.GetRawText(),
                _ => current.ToString(),
            };
        });

        return rendered.Length <= MaxRenderedChars
            ? rendered
            : rendered[..MaxRenderedChars];
    }

    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}")]
    private static partial Regex PlaceholderRegex();
}

/// <summary>
/// Verificação de assinatura de webhook <c>X-Hub-Signature-256</c>
/// (HMAC-SHA256 hex sobre o corpo cru) com comparação timing-safe
/// (SPEC-20261021-harness-seams RF-D1).
/// </summary>
public static class WebhookSignature
{
    /// <summary>
    /// <see langword="true"/> quando <paramref name="signatureHeader"/>
    /// corresponde a <c>sha256=&lt;hmac(secret, rawBody)&gt;</c> em hex
    /// minúsculo. Comparação de tempo constante.
    /// </summary>
    public static bool Verify(
        string secret, string rawBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }
        var expected = "sha256=" + Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(secret),
                System.Text.Encoding.UTF8.GetBytes(rawBody)))
            .ToLowerInvariant();
        return System.Security.Cryptography.CryptographicOperations
            .FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(expected),
                System.Text.Encoding.ASCII.GetBytes(signatureHeader));
    }
}
