using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Detalhe real do último erro upstream anexado à mensagem de falha
/// definitiva — status HTTP e o recorte sanitizado do corpo que a
/// <see cref="ProviderHttpException"/> já captura. Prefere o campo
/// <c>error.message</c> do envelope JSON do gateway; fallback é o corpo
/// bruto truncado, sempre em linha única.
/// </summary>
public static class UpstreamErrorFormatter
{
    /// <summary>Formata " Último erro: {status} — {snippet}" para anexar à
    /// mensagem de falha; <see cref="string.Empty"/> quando a exceção não é
    /// <see cref="ProviderHttpException"/>.</summary>
    public static string For(Exception ex)
    {
        if (ex is not ProviderHttpException http)
            return string.Empty;
        var detail = $" Último erro: {(int)(http.StatusCode ?? 0)} {http.StatusCode}";
        if (http.Body is not { Length: > 0 } body)
            return detail;
        var snippet = ExtractUpstreamMessage(body) ?? body;
        snippet = Regex.Replace(
            snippet.Trim(), @"\s+", " ",
            RegexOptions.None, RegexDefaults.MatchTimeout);
        if (snippet.Length > 300)
            snippet = snippet[..300] + "…";
        return snippet.Length > 0 ? $"{detail} — {snippet}" : detail;
    }

    /// <summary>Extrai <c>error.message</c> (ou <c>message</c> top-level) do
    /// corpo JSON de erro do gateway; <c>null</c> quando não é JSON ou não
    /// há mensagem — o caller cai no recorte bruto.</summary>
    private static string? ExtractUpstreamMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (root.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("message", out var msg)
                && msg.ValueKind == JsonValueKind.String)
                return msg.GetString();
            if (root.TryGetProperty("message", out var top)
                && top.ValueKind == JsonValueKind.String)
                return top.GetString();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
