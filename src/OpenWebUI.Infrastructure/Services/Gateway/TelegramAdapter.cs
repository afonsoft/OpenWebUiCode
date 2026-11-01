using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Infrastructure.Services.Gateway;

/// <summary>
/// Adapter Telegram do messaging gateway
/// (SPEC-20261023-messaging-gateway G): verificação por
/// <c>X-Telegram-Bot-Api-Secret-Token</c>, parse de
/// <c>update.message</c> (DM de texto) e envio por
/// <c>sendMessage</c> com chunk ≤4000 chars e fallback para texto puro
/// quando a plataforma rejeita o markup.
/// </summary>
public sealed class TelegramAdapter(IHttpClientFactory httpFactory) : IGatewayAdapter
{
    /// <summary>Host fixo da Bot API — validado pelo <see cref="SsrfGuard"/> antes do envio.</summary>
    public const string ApiHost = "api.telegram.org";

    /// <summary>Limite de chars por mensagem do Telegram (4096; 4000 com folga).</summary>
    public const int MaxMessageChars = 4000;

    /// <inheritdoc />
    public string Platform => GatewayPlatforms.Telegram;

    /// <inheritdoc />
    public string SignatureHeader => "X-Telegram-Bot-Api-Secret-Token";

    /// <inheritdoc />
    public bool VerifySignature(
        GatewayChannel channel, string? headerValue, string rawBody)
    {
        if (string.IsNullOrEmpty(channel.WebhookSecret)
            || string.IsNullOrEmpty(headerValue))
        {
            return false;
        }
        var expected = Encoding.UTF8.GetBytes(channel.WebhookSecret);
        var actual = Encoding.UTF8.GetBytes(headerValue);
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <inheritdoc />
    public GatewayInboundMessage? ParseInbound(string rawBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out var msg)
                || msg.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (!msg.TryGetProperty("text", out var textEl)
                || textEl.ValueKind != JsonValueKind.String
                || textEl.GetString() is not { Length: > 0 } text)
            {
                return null;
            }
            var chatId = msg.TryGetProperty("chat", out var chat)
                && chat.TryGetProperty("id", out var cid)
                    ? cid.ToString() : null;
            var userId = msg.TryGetProperty("from", out var from)
                && from.TryGetProperty("id", out var uid)
                    ? uid.ToString() : null;
            if (chatId is null || userId is null)
            {
                return null;
            }
            string? name = null;
            if (msg.TryGetProperty("from", out var fromEl))
            {
                if (fromEl.TryGetProperty("username", out var un)
                    && un.ValueKind == JsonValueKind.String)
                {
                    name = un.GetString();
                }
                else if (fromEl.TryGetProperty("first_name", out var fn)
                    && fn.ValueKind == JsonValueKind.String)
                {
                    name = fn.GetString();
                }
                else
                {
                    name = null;
                }
            }
            var messageId = msg.TryGetProperty("message_id", out var mid)
                ? mid.ToString() : null;
            return new GatewayInboundMessage(
                chatId, userId, name, text, messageId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<GatewaySendResult> SendReplyAsync(
        GatewayChannel channel, GatewayPeer peer, string text,
        string botToken, CancellationToken ct)
    {
        if (await SsrfGuard.IsBlockedAsync(ApiHost, allowLoopback: false, ct))
        {
            return new GatewaySendResult(false, "ssrf: host da API bloqueado");
        }
        var client = httpFactory.CreateClient("gateway");
        var url = $"https://{ApiHost}/bot{botToken}/sendMessage";
        var chunks = Chunk(text, MaxMessageChars);
        string? lastError = null;
        foreach (var chunk in chunks)
        {
            var (ok, error) = await SendChunkAsync(
                client, url, peer.ExternalChatId, chunk, useMarkdown: true, ct);
            if (!ok)
            {
                // Markup rejeitado (entidade markdown inválida) → texto puro.
                (ok, error) = await SendChunkAsync(
                    client, url, peer.ExternalChatId, chunk,
                    useMarkdown: false, ct);
            }
            if (!ok)
            {
                lastError = error;
                break;
            }
        }
        return lastError is null
            ? new GatewaySendResult(true, null)
            : new GatewaySendResult(false, lastError);
    }

    private static async Task<(bool Ok, string? Error)> SendChunkAsync(
        HttpClient client, string url, string chatId, string chunk,
        bool useMarkdown, CancellationToken ct)
    {
        try
        {
            var payload = useMarkdown
                ? (object)new
                {
                    chat_id = chatId, text = chunk, parse_mode = "MarkdownV2",
                }
                : new { chat_id = chatId, text = chunk };
            using var res = await client.PostAsJsonAsync(url, payload, ct);
            if (res.IsSuccessStatusCode)
            {
                return (true, null);
            }
            var body = await res.Content.ReadAsStringAsync(ct);
            var detail = body.Length > 200 ? body[..200] : body;
            return (false, $"telegram {(int)res.StatusCode}: {detail}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, $"telegram send: {ex.Message}");
        }
    }

    /// <summary>Divide o texto em blocos ≤ <paramref name="size"/> sem cortar palavras quando possível.</summary>
    internal static IReadOnlyList<string> Chunk(string text, int size)
    {
        if (text.Length <= size)
        {
            return [text];
        }
        var chunks = new List<string>();
        var remaining = text;
        while (remaining.Length > size)
        {
            var cut = remaining.LastIndexOf('\n', size);
            if (cut <= 0)
            {
                cut = remaining.LastIndexOf(' ', size);
            }
            if (cut <= 0)
            {
                cut = size;
            }
            chunks.Add(remaining[..cut]);
            remaining = remaining[cut..].TrimStart('\n', ' ');
        }
        if (remaining.Length > 0)
        {
            chunks.Add(remaining);
        }
        return chunks;
    }
}
