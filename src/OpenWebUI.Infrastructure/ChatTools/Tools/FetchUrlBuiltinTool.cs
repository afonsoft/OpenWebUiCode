using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:fetch_url</c> — baixa uma URL pública e devolve texto
/// (SPEC-20261007-chat-agent-tools RF-005). Guard SSRF: só http/https,
/// e o host resolvido não pode ser loopback, RFC1918, link-local ou
/// reservado. HTML vira texto (script/style cortados, tags de bloco viram
/// quebra de linha, resto é stripado, entidades decodadas) e a saída é
/// truncada com segredos mascarados.
/// </summary>
public sealed partial class FetchUrlBuiltinTool(IHttpClientFactory httpFactory) : IBuiltinChatTool
{
    private const int MaxOutputChars = 8_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    public string Name => "fetch_url";

    /// <inheritdoc />
    public string Description =>
        "Fetch a URL — ALWAYS call when the user shares a link.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "http(s) URL to fetch." },
            "max_chars": { "type": "integer", "description": "Maximum characters returned.", "default": 8000 }
          },
          "required": ["url"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;


    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var raw = args.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return new BuiltinToolResult("URL inválida — use http(s) absoluta.");
        }

        if (await SsrfGuard.IsBlockedAsync(uri.Host, allowLoopback: false, ct))
        {
            return new BuiltinToolResult(
                $"Host '{uri.Host}' resolve para endereço privado/loopback — bloqueado.",
                Refused: true,
                RefuseReason: "ssrf-guard");
        }

        var maxChars = args.TryGetProperty("max_chars", out var m) && m.TryGetInt32(out var mc)
            ? Math.Clamp(mc, 500, 24_000)
            : MaxOutputChars;

        var client = httpFactory.CreateClient(nameof(FetchUrlBuiltinTool));
        client.Timeout = Timeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenWebUI-Chat/1.0");

        string body;
        string? contentType;
        int status;
        try
        {
            using var response = await client.GetAsync(uri, ct);
            status = (int)response.StatusCode;
            contentType = response.Content.Headers.ContentType?.MediaType;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var charset = response.Content.Headers.ContentType?.CharSet;
            var encoding = ResolveEncoding(charset);
            body = encoding.GetString(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new BuiltinToolResult($"Falha ao baixar a URL: {ex.Message}");
        }

        var text = contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
            ? HtmlToText(body)
            : body;

        var truncated = false;
        if (text.Length > maxChars)
        {
            text = text[..maxChars];
            truncated = true;
        }

        var scrubbed = SecretScrubber.Scrub(text) ?? string.Empty;
        var result = $"[HTTP {status} — {contentType ?? "text"}]\n{scrubbed}";
        if (truncated)
        {
            result += $"\n[truncado em {maxChars} chars]";
        }

        return new BuiltinToolResult(result, new
        {
            status,
            contentType,
            url = uri.ToString(),
            truncated,
        });
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException) { /* charset desconhecido → UTF-8 */ }
        }

        return Encoding.UTF8;
    }

    /// <summary>
    /// HTML → texto: corta script/style/noscript/template, vira quebra de
    /// linha em tags de bloco, stripa o resto e decoda entidades.
    /// </summary>
    internal static string HtmlToText(string html)
    {
        var text = DropBlocksRegex().Replace(html, " ");
        text = BlockToNewlineRegex().Replace(text, "\n");
        text = AnyTagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = CollapseWhitespaceRegex().Replace(text, " ");
        text = CollapseNewlinesRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(
        @"<(script|style|noscript|template|svg|head)[\s>][\s\S]*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DropBlocksRegex();

    [GeneratedRegex(
        @"</?(p|div|br|hr|li|ul|ol|tr|table|section|article|header|footer|h[1-6]|blockquote|pre|form|figure|figcaption|nav|aside|main|dl|dt|dd)[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BlockToNewlineRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"[^\S\n]+", RegexOptions.Compiled)]
    private static partial Regex CollapseWhitespaceRegex();

    [GeneratedRegex(@"\n{3,}", RegexOptions.Compiled)]
    private static partial Regex CollapseNewlinesRegex();
}
