using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services.Gateway;

/// <summary>Resultado interno do pipeline inbound (o endpoint mapeia para HTTP).</summary>
/// <param name="StatusCode">Código HTTP da resposta imediata.</param>
/// <param name="Detail">Detalhe genérico de erro (sem vazar plataforma/token).</param>
public sealed record GatewayInboundResult(int StatusCode, string? Detail = null)
{
    /// <summary>200 imediato — processamento segue assíncrono.</summary>
    public static readonly GatewayInboundResult Ok = new(200);
}

/// <summary>
/// Pipeline inbound do messaging gateway
/// (SPEC-20261023-messaging-gateway C/D/RF-001..006): resolve o canal pelo
/// token, valida a assinatura da plataforma antes de qualquer
/// processamento, aplica o gate de pareamento (peer novo recebe código;
/// bloqueado é ignorado), mantém sessão persistente por peer e enfileira
/// a run real via <see cref="IChatRunDispatcher"/> — o texto externo entra
/// embrulhado em <c>&lt;untrusted_input&gt;</c> e o toolset segue a
/// política do canal (defesa server-side, não prompt).
/// </summary>
public sealed class GatewayInboundService(
    AppDbContext db,
    IEnumerable<IGatewayAdapter> adapters,
    GatewayPairingService pairing,
    UserSecretsService secrets,
    ConfigService config,
    IChatRunDispatcher dispatcher)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Cap do texto inbound (kv <c>agent.gateway.maxInboundChars</c>).</summary>
    public const int DefaultMaxInboundChars = 8000;

    /// <summary>Chave kv do feature flag (<c>agent.gateway.enabled</c>, default false).</summary>
    public const string EnabledKey = "agent.gateway.enabled";

    /// <summary>
    /// Consome um update inbound. A assinatura é verificada antes de
    /// qualquer efeito; respostas de erro são genéricas (não vazam se o
    /// token existe ou qual plataforma atende).
    /// </summary>
    public async Task<GatewayInboundResult> HandleAsync(
        string platform, string token, string? signatureHeader,
        string rawBody, CancellationToken ct)
    {
        if (!await config.GetAsync(EnabledKey, false, ct))
        {
            return new GatewayInboundResult(404, "não encontrado");
        }
        var adapter = adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            return new GatewayInboundResult(404, "não encontrado");
        }
        var channel = await db.GatewayChannels
            .FirstOrDefaultAsync(c => c.Id == token && c.Enabled
                && c.Platform == adapter.Platform, ct);
        if (channel is null)
        {
            return new GatewayInboundResult(404, "não encontrado");
        }
        if (!adapter.VerifySignature(channel, signatureHeader, rawBody))
        {
            await AuditAsync(channel.Id, null, "in", "rejected",
                null, null, null, "assinatura inválida", ct);
            return new GatewayInboundResult(401, "assinatura inválida");
        }
        var inbound = adapter.ParseInbound(rawBody);
        if (inbound is null)
        {
            await AuditAsync(channel.Id, null, "in", "filtered",
                null, null, null, null, ct);
            return GatewayInboundResult.Ok;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var peer = await db.GatewayPeers.FirstOrDefaultAsync(p =>
            p.ChannelId == channel.Id
            && p.ExternalChatId == inbound.ExternalChatId
            && p.ExternalUserId == inbound.ExternalUserId, ct);
        if (peer is null)
        {
            peer = new GatewayPeer
            {
                ChannelId = channel.Id,
                ExternalChatId = inbound.ExternalChatId,
                ExternalUserId = inbound.ExternalUserId,
                ExternalName = inbound.ExternalName,
                CreatedAt = now,
            };
            db.GatewayPeers.Add(peer);
            await db.SaveChangesAsync(ct);
        }
        else if (inbound.ExternalName is { Length: > 0 }
            && peer.ExternalName != inbound.ExternalName)
        {
            peer.ExternalName = inbound.ExternalName;
        }

        if (peer.Blocked)
        {
            await AuditAsync(channel.Id, peer.Id, "in", "blocked",
                inbound.MessageId, null, Excerpt(inbound.Text), null, ct);
            return GatewayInboundResult.Ok;
        }

        var botToken = await ResolveBotTokenAsync(channel, ct);
        if (peer.PairedUserId is null)
        {
            var code = await pairing.EnsureCodeAsync(peer, ct);
            await db.SaveChangesAsync(ct);
            string? sendError = null;
            if (botToken is not null && code is not null)
            {
                var send = await adapter.SendReplyAsync(channel, peer,
                    $"Seu código de pareamento é {code} (válido por "
                    + $"{await config.GetAsync("agent.gateway.pairingTtlMinutes", GatewayPairingService.DefaultTtlMinutes, ct)} min). "
                    + "Informe-o em Settings → Gateway na web UI.",
                    botToken, ct);
                sendError = send.Ok ? null : send.Error;
            }
            await AuditAsync(channel.Id, peer.Id, "in", "pairing",
                inbound.MessageId, null, Excerpt(inbound.Text), sendError, ct);
            return GatewayInboundResult.Ok;
        }

        var session = await db.GatewaySessions
            .FirstOrDefaultAsync(s => s.PeerId == peer.Id, ct);
        Chat? chat = null;
        if (session is not null)
        {
            chat = await db.Chats.FirstOrDefaultAsync(
                c => c.Id == session.ChatId, ct);
        }
        if (chat is null)
        {
            chat = new Chat
            {
                UserId = peer.PairedUserId,
                Title = $"gateway:{channel.Platform}:{peer.ExternalName ?? peer.ExternalUserId}",
                TagsJson = JsonSerializer.Serialize(
                    new[] { "gateway", channel.Platform }, JsonOptions),
                ModelsJson = channel.DefaultModelId is { Length: > 0 } dm
                    ? JsonSerializer.Serialize(new[] { dm }, JsonOptions)
                    : "[]",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Chats.Add(chat);
            if (session is null)
            {
                session = new GatewaySession
                {
                    PeerId = peer.Id, ChatId = chat.Id, CreatedAt = now,
                };
                db.GatewaySessions.Add(session);
            }
            else
            {
                session.ChatId = chat.Id;
            }
        }
        session!.LastInboundAt = now;

        var maxChars = await config.GetAsync(
            "agent.gateway.maxInboundChars", DefaultMaxInboundChars, ct);
        var text = inbound.Text.Length > maxChars
            ? inbound.Text[..maxChars] : inbound.Text;
        // Doutrina <untrusted_input>: conteúdo de terceiros é dado, nunca
        // instrução — fecha aspas duplas no escape para não reabrir a tag.
        var content = $"<untrusted_input source=\"gateway:{channel.Platform}\" field=\"text\">"
            + text.Replace("</untrusted_input>", "<\\/untrusted_input>",
                StringComparison.OrdinalIgnoreCase)
            + "</untrusted_input>";

        var leaf = await db.ChatMessages
            .Where(m => m.ChatId == chat.Id)
            .OrderByDescending(m => m.Position)
            .Select(m => new { m.Id, m.Position })
            .FirstOrDefaultAsync(ct);
        var leafId = chat.LeafMessageId;
        if (leafId is not null && leaf?.Id != leafId
            && !await db.ChatMessages.AnyAsync(
                m => m.ChatId == chat.Id && m.Id == leafId, ct))
        {
            leafId = null;
        }
        leafId ??= leaf?.Id;

        var message = new ChatMessage
        {
            ChatId = chat.Id,
            Role = "user",
            Content = content,
            Position = (leaf?.Position ?? -1) + 1,
            Timestamp = now,
            ParentId = leafId,
        };
        db.ChatMessages.Add(message);
        chat.LeafMessageId = message.Id;
        chat.UpdatedAt = now;

        var model = channel.DefaultModelId ?? FirstModel(chat.ModelsJson);
        if (model is null)
        {
            await AuditAsync(channel.Id, peer.Id, "in", "failed",
                inbound.MessageId, null, Excerpt(inbound.Text),
                "sem modelo: configure defaultModelId no canal", ct);
            if (botToken is not null)
            {
                await adapter.SendReplyAsync(channel, peer,
                    "Este canal ainda não tem modelo configurado — peça ao "
                    + "dono para definir defaultModelId em Settings → Gateway.",
                    botToken, ct);
            }
            return GatewayInboundResult.Ok;
        }

        string? personaPrompt = null;
        if (channel.PersonaId is { Length: > 0 } personaSlug)
        {
            personaPrompt = await db.Skills.AsNoTracking()
                .Where(s => s.UserId == peer.PairedUserId && s.IsActive
                    && (s.Name == personaSlug || s.Id == personaSlug))
                .Select(s => s.Content)
                .FirstOrDefaultAsync(ct);
        }

        var run = new ChatRun
        {
            ChatId = chat.Id,
            UserId = peer.PairedUserId,
            Model = model,
            RequestJson = JsonSerializer.Serialize(
                new ChatCompletionRequest(
                    model,
                    [
                        .. personaPrompt is null
                            ? Array.Empty<ChatCompletionMessage>()
                            : new[] { new ChatCompletionMessage("system", personaPrompt) },
                        new ChatCompletionMessage("user", content),
                    ],
                    Stream: true,
                    // Política do canal aplicada server-side no toolset —
                    // em restricted o modelo nem vê tools mutáveis (RF-005).
                    ToolIds: channel.ToolsPolicy == GatewayToolsPolicies.Restricted
                        ? GatewayToolsPolicies.RestrictedToolIds : null),
                JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await AuditAsync(channel.Id, peer.Id, "in", "accepted",
            inbound.MessageId, run.Id, Excerpt(inbound.Text), null, ct);

        dispatcher.Enqueue(run.Id);
        return GatewayInboundResult.Ok;
    }

    /// <summary>Resolve o token do bot a partir do <c>SecretRef</c> no cofre do dono.</summary>
    public async Task<string?> ResolveBotTokenAsync(
        GatewayChannel channel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(channel.SecretRef))
        {
            return null;
        }
        var userSecrets = await secrets.GetUserSecretsAsync(
            channel.OwnerUserId, ct);
        return userSecrets.TryGetValue(channel.SecretRef, out var s)
            ? s.Value : null;
    }

    /// <summary>Persiste auditoria de entrega inbound (excerpt truncado).</summary>
    public async Task AuditAsync(
        string channelId, string? peerId, string direction, string status,
        string? messageId, string? runId, string? excerpt, string? error,
        CancellationToken ct)
    {
        db.GatewayDeliveries.Add(new GatewayDelivery
        {
            ChannelId = channelId,
            PeerId = peerId,
            Direction = direction,
            Status = status,
            MessageId = messageId,
            RunId = runId,
            Excerpt = excerpt,
            Error = error is { Length: > 500 } ? error[..500] : error,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync(ct);
    }

    private static string? Excerpt(string? text) =>
        text switch
        {
            null => null,
            { Length: <= 200 } => text,
            _ => text[..200],
        };

    private static string? FirstModel(string modelsJson)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(
                modelsJson, JsonOptions);
            return list?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
