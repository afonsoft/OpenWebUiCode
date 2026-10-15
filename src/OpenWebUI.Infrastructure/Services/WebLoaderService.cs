using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Baixa uma URL pública e extrai texto de HTML (remoção de scripts/styles/tags).
/// Timeout e tamanho máximo são configuráveis para conter loaders maliciosos.
/// </summary>
public class WebLoaderService(IHttpClientFactory httpFactory)
{
    /// <summary>Timeout de fetch.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Tamanho máximo de download em bytes.</summary>
    public int MaxBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// Baixa a URL e extrai texto. Lança <see cref="WebLoaderException"/> em falha.
    /// </summary>
    /// <param name="url">URL http(s) pública.</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<string> LoadTextAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new WebLoaderException("URL inválida — apenas http/https.");
        }

        var client = httpFactory.CreateClient(nameof(WebLoaderService));
        client.Timeout = Timeout;
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new WebLoaderException($"Fetch falhou com status {(int)response.StatusCode}.");
        }

        var content = response.Content;
        if (content.Headers.ContentLength is > 0 and var len && len > MaxBytes)
        {
            throw new WebLoaderException("Conteúdo excede o tamanho máximo permitido.");
        }

        var raw = await ReadBoundedAsync(content, ct);
        var isHtml = content.Headers.ContentType?.MediaType?.Contains("html") == true
            || raw.TrimStart().StartsWith('<');
        return isHtml ? HtmlToText(raw) : raw;
    }

    private async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxBytes)
            {
                throw new WebLoaderException("Conteúdo excede o tamanho máximo permitido.");
            }
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Extrai a transcrição de um vídeo do YouTube: baixa a página do vídeo,
    /// lê captionTracks do player e baixa a faixa em XML (timedtext).
    /// Best-effort — vídeos sem legenda geram <see cref="WebLoaderException"/>.
    /// </summary>
    /// <param name="url">URL do vídeo (watch?v=, youtu.be ou /shorts/).</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<string> LoadYoutubeTranscriptAsync(string url, CancellationToken ct = default)
    {
        var videoId = ExtractVideoId(url)
            ?? throw new WebLoaderException("URL do YouTube inválida.");

        var client = httpFactory.CreateClient(nameof(WebLoaderService));
        client.Timeout = Timeout;
        var page = await client.GetStringAsync(
            $"https://www.youtube.com/watch?v={videoId}", ct);

        var marker = "\"captionTracks\":[";
        var markerAt = page.IndexOf(marker, StringComparison.Ordinal);
        if (markerAt < 0)
        {
            throw new WebLoaderException("Vídeo sem transcrição disponível.");
        }
        var start = markerAt + marker.Length - 1;
        var depth = 0;
        var end = start;
        var closed = false;
        for (; end < page.Length; end++)
        {
            if (page[end] == '[') depth++;
            else if (page[end] == ']') { depth--; if (depth == 0) { closed = true; break; } }
        }
        if (!closed)
        {
            throw new WebLoaderException("Transcrição malformada na página.");
        }
        var tracksJson = page[start..(end + 1)];
        using var doc = JsonDocument.Parse(tracksJson);
        var baseUrl = doc.RootElement.EnumerateArray()
            .Select(t => t.TryGetProperty("baseUrl", out var b) ? b.GetString() : null)
            .FirstOrDefault(u => u is not null)
            ?? throw new WebLoaderException("Vídeo sem transcrição disponível.");

        var xml = await client.GetStringAsync(baseUrl, ct);
        var matches = Regex.Matches(xml, @"<text[^>]*>(.*?)</text>", RegexOptions.Singleline, RegexDefaults.MatchTimeout);
        if (matches.Count == 0)
        {
            throw new WebLoaderException("Transcrição vazia.");
        }

        var sb = new StringBuilder();
        foreach (Match m in matches)
        {
            sb.Append(WebUtility.HtmlDecode(m.Groups[1].Value).Trim()).Append(' ');
        }
        return sb.ToString().Trim();
    }

    private static string? ExtractVideoId(string url)
    {
        var m = Regex.Match(url,
            @"(?:youtube\.com/(?:watch\?.*v=|shorts/)|youtu\.be/)([\w-]{6,})",
            RegexOptions.None, RegexDefaults.MatchTimeout);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Conversão simples de HTML para texto (scripts/styles removidos).</summary>
    public static string HtmlToText(string html)
    {
        var noScripts = Regex.Replace(
            html, @"<(script|style|noscript|svg)[^>]*>.*?</\1>",
            " ", RegexOptions.IgnoreCase | RegexOptions.Singleline, RegexDefaults.MatchTimeout);
        var withBreaks = Regex.Replace(
            noScripts, @"<(br|p|div|li|h[1-6]|tr)[^>]*>", "\n", RegexOptions.IgnoreCase, RegexDefaults.MatchTimeout);
        var noTags = Regex.Replace(withBreaks, @"<[^>]+>", " ", RegexOptions.None, RegexDefaults.MatchTimeout);
        var unescaped = WebUtility.HtmlDecode(noTags);
        return Regex.Replace(unescaped, @"[ \t]*\n[ \t]*", "\n", RegexOptions.None, RegexDefaults.MatchTimeout).Trim();
    }
}

/// <summary>Falha de loader de URL (fetch ou extração).</summary>
public class WebLoaderException(string message) : Exception(message);
