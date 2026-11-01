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
            await AuditAsync(new AuditInfo(channel.Id, null, "in", "rejected", null, null, null, "assinatura inválida"), ct);
            return new GatewayInboundResult(401, "assinatura inválida");
        }
        var inbound = adapter.ParseInbound(rawBody);
        if (inbound is null)
        {
            await AuditAsync(new AuditInfo(channel.Id, null, "in", "filtered", null, null, null, null), ct);
            return GatewayInboundResult.Ok;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var peer = await ResolvePeerAsync(channel, inbound, now, ct);
        if (peer.Blocked)
        {
            await AuditAsync(new AuditInfo(channel.Id, peer.Id, "in", "blocked", inbound.MessageId, null, Excerpt(inbound.Text), null), ct);
            return GatewayInboundResult.Ok;
        }

        var botToken = await ResolveBotTokenAsync(channel, ct);
        if (peer.PairedUserId is null)
        {
            await HandlePairingAsync(channel, peer, inbound, adapter, botToken, ct);
            return GatewayInboundResult.Ok;
        }

        var chat = await ResolveChatAsync(channel, peer, now, ct);
        var content = await BuildContentAsync(channel, inbound, ct);
        await AppendMessageAsync(chat, content, now, ct);

        var model = channel.DefaultModelId ?? FirstModel(chat.ModelsJson);
        if (model is null)
        {
            await AuditAsync(new AuditInfo(channel.Id, peer.Id, "in", "failed", inbound.MessageId, null, Excerpt(inbound.Text), "sem modelo: configure defaultModelId no canal"), ct);
            if (botToken is not null)
            {
                await adapter.SendReplyAsync(channel, peer,
                    "Este canal ainda não tem modelo configurado — peça ao "
                    + "dono para definir defaultModelId em Settings → Gateway.",
                    botToken, ct);
            }
            return GatewayInboundResult.Ok;
        }

        await EnqueueRunAsync(channel, peer, chat, model, content, inbound, now, ct);
        return GatewayInboundResult.Ok;
    }

    /// <summary>Peer por (channel, chat externo, usuário externo) — cria ou atualiza o nome.</summary>
    private async Task<GatewayPeer> ResolvePeerAsync(
        GatewayChannel channel, GatewayInboundMessage inbound,
        long now, CancellationToken ct)
    {
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
        return peer;
    }

    /// <summary>Peer não pareado: garante código, responde pelo canal e audita.</summary>
    private async Task HandlePairingAsync(
        GatewayChannel channel, GatewayPeer peer,
        GatewayInboundMessage inbound, IGatewayAdapter adapter,
        string? botToken, CancellationToken ct)
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
        await AuditAsync(new AuditInfo(channel.Id, peer.Id, "in", "pairing", inbound.MessageId, null, Excerpt(inbound.Text), sendError), ct);
    }

    /// <summary>Chat da sessão do peer — cria chat+session na primeira mensagem.</summary>
    private async Task<Chat> ResolveChatAsync(
        GatewayChannel channel, GatewayPeer peer, long now, CancellationToken ct)
    {
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
                UserId = peer.PairedUserId!,
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
        return chat;
    }

    /// <summary>Cap de caracteres + envelope <c>&lt;untrusted_input&gt;</c>.</summary>
    private async Task<string> BuildContentAsync(
        GatewayChannel channel, GatewayInboundMessage inbound, CancellationToken ct)
    {
        var maxChars = await config.GetAsync(
            "agent.gateway.maxInboundChars", DefaultMaxInboundChars, ct);
        var text = inbound.Text.Length > maxChars
            ? inbound.Text[..maxChars] : inbound.Text;
        // Doutrina <untrusted_input>: conteúdo de terceiros é dado, nunca
        // instrução — fecha aspas duplas no escape para não reabrir a tag.
        return $"<untrusted_input source=\"gateway:{channel.Platform}\" field=\"text\">"
            + text.Replace("</untrusted_input>", "<\\/untrusted_input>",
                StringComparison.OrdinalIgnoreCase)
            + "</untrusted_input>";
    }

    /// <summary>Anexa a mensagem do usuário na folha e atualiza o chat.</summary>
    private async Task AppendMessageAsync(
        Chat chat, string content, long now, CancellationToken ct)
    {
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
    }

    /// <summary>Persona do canal + persistência do run + enqueue no dispatcher.</summary>
    private async Task EnqueueRunAsync(
        GatewayChannel channel, GatewayPeer peer, Chat chat, string model,
        string content, GatewayInboundMessage inbound,
        long now, CancellationToken ct)
    {
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
            UserId = peer.PairedUserId!,
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
        await AuditAsync(new AuditInfo(channel.Id, peer.Id, "in", "accepted", inbound.MessageId, run.Id, Excerpt(inbound.Text), null), ct);

        dispatcher.Enqueue(run.Id);
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

    /// <summary>Dados de auditoria de entrega inbound (S107).</summary>
    public sealed record AuditInfo(
        string ChannelId, string? PeerId, string Direction, string Status,
        string? MessageId, string? RunId, string? Excerpt, string? Error);

    /// <summary>Persiste auditoria de entrega inbound (excerpt truncado).</summary>
    public async Task AuditAsync(AuditInfo a, CancellationToken ct)
    {
        db.GatewayDeliveries.Add(new GatewayDelivery
        {
            ChannelId = a.ChannelId,
            PeerId = a.PeerId,
            Direction = a.Direction,
            Status = a.Status,
            MessageId = a.MessageId,
            RunId = a.RunId,
            Excerpt = a.Excerpt,
            Error = a.Error is { Length: > 500 } ? a.Error[..500] : a.Error,
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
