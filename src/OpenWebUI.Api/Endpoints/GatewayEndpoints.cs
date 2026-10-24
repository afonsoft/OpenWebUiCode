using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Gateway;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Messaging gateway (SPEC-20261023-messaging-gateway): CRUD autenticado de
/// canais/peers do dono + pareamento por código, e o endpoint público de
/// webhook <c>POST /api/v1/gateway/{platform}/{token}</c> — anônimo,
/// rate-limited e verificado por assinatura da plataforma (RF-001).
/// O token do endpoint é o próprio Id (GUID) do canal; segredos de bot
/// vivem no <c>UserSecretsService</c> via <c>secretRef</c>, nunca planos.
/// </summary>
public static class GatewayEndpoints
{
    private const int MaxPayloadBytes = 256 * 1024;

    /// <summary>Mapeia o CRUD autenticado e o webhook público.</summary>
    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/gateway").RequireAuthorization();

        group.MapGet("/channels", ListChannelsAsync);
        group.MapPost("/channels", CreateChannelAsync);
        group.MapPatch("/channels/{id}", UpdateChannelAsync);
        group.MapDelete("/channels/{id}", DeleteChannelAsync);
        group.MapGet("/channels/{id}/deliveries", ListDeliveriesAsync);
        group.MapGet("/peers", ListPeersAsync);
        group.MapPatch("/peers/{id}", UpdatePeerAsync);
        group.MapPost("/pair", PairAsync);

        // Webhook inbound — anônimo (token = segredo do endpoint), com
        // rate-limit por IP e assinatura verificada no service.
        app.MapPost("/api/v1/gateway/{platform}/{token}", InboundAsync)
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> ListChannelsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var channels = await db.GatewayChannels.AsNoTracking()
            .Where(c => c.OwnerUserId == user.Id)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(channels.Select(ToResponse));
    }

    private static async Task<IResult> CreateChannelAsync(
        HttpContext http, AppDbContext db, UserSecretsService secrets,
        GatewayChannelCreateRequest? request, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var platform = (request?.Platform ?? string.Empty).Trim().ToLowerInvariant();
        if (!GatewayPlatforms.IsSupported(platform))
        {
            return Results.BadRequest(
                new { detail = "platform deve ser 'telegram'." });
        }
        if (string.IsNullOrWhiteSpace(request?.BotToken))
        {
            return Results.BadRequest(new { detail = "botToken é obrigatório." });
        }
        var policy = (request.ToolsPolicy ?? GatewayToolsPolicies.Restricted)
            .Trim().ToLowerInvariant();
        if (policy is not (GatewayToolsPolicies.Restricted or GatewayToolsPolicies.Standard))
        {
            return Results.BadRequest(
                new { detail = "toolsPolicy deve ser restricted|standard." });
        }

        var channel = new GatewayChannel
        {
            OwnerUserId = user.Id,
            Platform = platform,
            WebhookSecret = Guid.NewGuid().ToString("N"),
            ToolsPolicy = policy,
            DefaultModelId = string.IsNullOrWhiteSpace(request.DefaultModelId)
                ? null : request.DefaultModelId.Trim(),
            PersonaId = string.IsNullOrWhiteSpace(request.PersonaId)
                ? null : request.PersonaId.Trim(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        channel.SecretRef = $"GATEWAY_BOT_{channel.Id[..8].ToUpperInvariant()}";
        var error = await secrets.SetUserAsync(
            user.Id, channel.SecretRef, request.BotToken.Trim(), ct);
        if (error is not null)
        {
            return Results.BadRequest(new { detail = $"botToken: {error}" });
        }
        db.GatewayChannels.Add(channel);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(channel));
    }

    private static async Task<IResult> UpdateChannelAsync(
        HttpContext http, AppDbContext db, UserSecretsService secrets,
        string id, GatewayChannelUpdateRequest? request, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var channel = await db.GatewayChannels.FirstOrDefaultAsync(
            c => c.Id == id && c.OwnerUserId == user.Id, ct);
        if (channel is null)
        {
            return Results.NotFound(new { detail = "canal não encontrado." });
        }
        if (request?.Enabled is { } enabled)
        {
            channel.Enabled = enabled;
        }
        if (request?.ToolsPolicy is { Length: > 0 } policy)
        {
            var normalized = policy.Trim().ToLowerInvariant();
            if (normalized is not (GatewayToolsPolicies.Restricted
                or GatewayToolsPolicies.Standard))
            {
                return Results.BadRequest(
                    new { detail = "toolsPolicy deve ser restricted|standard." });
            }
            channel.ToolsPolicy = normalized;
        }
        if (request?.DefaultModelId is { } model)
        {
            channel.DefaultModelId = string.IsNullOrWhiteSpace(model)
                ? null : model.Trim();
        }
        if (request?.PersonaId is { } persona)
        {
            channel.PersonaId = string.IsNullOrWhiteSpace(persona)
                ? null : persona.Trim();
        }
        if (request?.BotToken is { Length: > 0 } token)
        {
            var error = await secrets.SetUserAsync(
                user.Id, channel.SecretRef, token.Trim(), ct);
            if (error is not null)
            {
                return Results.BadRequest(new { detail = $"botToken: {error}" });
            }
        }
        if (request?.RotateWebhookSecret == true)
        {
            channel.WebhookSecret = Guid.NewGuid().ToString("N");
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(channel));
    }

    private static async Task<IResult> DeleteChannelAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var deleted = await db.GatewayChannels
            .Where(c => c.Id == id && c.OwnerUserId == user.Id)
            .ExecuteDeleteAsync(ct);
        return deleted > 0
            ? Results.Ok(new { deleted = true }) : Results.NotFound();
    }

    /// <summary>Auditoria in/out do canal — dono only.</summary>
    private static async Task<IResult> ListDeliveriesAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var owns = await db.GatewayChannels.AsNoTracking()
            .AnyAsync(c => c.Id == id && c.OwnerUserId == user.Id, ct);
        if (!owns)
        {
            return Results.NotFound(new { detail = "canal não encontrado." });
        }
        var deliveries = await db.GatewayDeliveries.AsNoTracking()
            .Where(d => d.ChannelId == id)
            .OrderByDescending(d => d.CreatedAt)
            .Take(100)
            .ToListAsync(ct);
        return Results.Ok(deliveries);
    }

    private static async Task<IResult> ListPeersAsync(
        HttpContext http, AppDbContext db, string? channelId, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var channelIds = db.GatewayChannels.AsNoTracking()
            .Where(c => c.OwnerUserId == user.Id)
            .Select(c => c.Id);
        var query = db.GatewayPeers.AsNoTracking()
            .Where(p => channelIds.Contains(p.ChannelId));
        if (channelId is { Length: > 0 })
        {
            query = query.Where(p => p.ChannelId == channelId);
        }
        var peers = await query
            .OrderByDescending(p => p.CreatedAt)
            .Take(200)
            .ToListAsync(ct);
        return Results.Ok(peers.Select(p => new
        {
            p.Id, p.ChannelId, p.ExternalUserId, p.ExternalChatId,
            p.ExternalName, paired = p.PairedUserId != null, p.PairedAt,
            p.Blocked, p.CreatedAt,
        }));
    }

    private static async Task<IResult> UpdatePeerAsync(
        HttpContext http, AppDbContext db, string id,
        GatewayPeerUpdateRequest? request, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var peer = await db.GatewayPeers
            .Join(db.GatewayChannels, p => p.ChannelId, c => c.Id, (p, c) => new { p, c })
            .Where(x => x.p.Id == id && x.c.OwnerUserId == user.Id)
            .Select(x => x.p)
            .FirstOrDefaultAsync(ct);
        if (peer is null)
        {
            return Results.NotFound(new { detail = "peer não encontrado." });
        }
        if (request?.Blocked is { } blocked)
        {
            peer.Blocked = blocked;
            if (blocked)
            {
                peer.PairingCode = null;
                peer.PairingExpiresAt = null;
            }
        }
        if (request?.Unpair == true)
        {
            peer.PairedUserId = null;
            peer.PairedAt = null;
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new
        {
            peer.Id, paired = peer.PairedUserId != null, peer.Blocked,
        });
    }

    /// <summary>Vincula um código de pareamento ao usuário logado (RF-002).</summary>
    private static async Task<IResult> PairAsync(
        HttpContext http, AppDbContext db, GatewayPairingService pairing,
        GatewayPairRequest? request, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var code = (request?.Code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return Results.BadRequest(new { detail = "code é obrigatório." });
        }
        var (result, peer) = await pairing.PairAsync(user.Id, code, ct);
        return result switch
        {
            GatewayPairResult.Ok => Results.Ok(new
            {
                paired = true,
                channel = peer!.ChannelId,
            }),
            GatewayPairResult.Expired => Results.BadRequest(
                new { detail = "código expirado — envie outra mensagem no bot." }),
            GatewayPairResult.Exhausted => Results.BadRequest(
                new { detail = "tentativas esgotadas — envie outra mensagem no bot." }),
            _ => Results.BadRequest(new { detail = "código inválido." }),
        };
    }

    /// <summary>
    /// Webhook inbound público (RF-001): rate-limit por IP, cap de payload
    /// e delegação ao <see cref="GatewayInboundService"/> — que verifica a
    /// assinatura antes de qualquer efeito. Resposta imediata.
    /// </summary>
    private static async Task<IResult> InboundAsync(
        HttpContext http, ConfigService config, RateLimitService limits,
        GatewayInboundService inbound, IEnumerable<IGatewayAdapter> adapters,
        string platform, string token, CancellationToken ct)
    {
        var rateConfig = await config.GetAsync("ratelimit", RateLimitConfig.Default, ct);
        if (rateConfig.Enabled)
        {
            var key = $"gw:{http.Connection.RemoteIpAddress?.ToString() ?? "anon"}";
            var retryAfter = limits.TryAcquire(
                key, rateConfig, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (retryAfter is { } retry)
            {
                http.Response.Headers.RetryAfter = retry.ToString();
                return Results.Json(
                    new { detail = "Limite de chamadas excedido." },
                    statusCode: 429);
            }
        }

        string raw;
        using (var reader = new StreamReader(http.Request.Body))
        {
            raw = await reader.ReadToEndAsync(ct);
        }
        if (raw.Length > MaxPayloadBytes)
        {
            return Results.Json(
                new { detail = "payload excede 256KB." }, statusCode: 413);
        }

        var adapter = adapters.FirstOrDefault(a =>
            string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase));
        var adapterHeader = adapter is null ? null
            : http.Request.Headers[adapter.SignatureHeader].ToString();
        var result = await inbound.HandleAsync(
            platform, token,
            string.IsNullOrEmpty(adapterHeader) ? null : adapterHeader,
            raw, ct);
        return result.StatusCode switch
        {
            200 => Results.Ok(new { }),
            401 => Results.Json(new { detail = result.Detail }, statusCode: 401),
            _ => Results.NotFound(new { detail = result.Detail ?? "não encontrado." }),
        };
    }

    private static object ToResponse(GatewayChannel c) => new
    {
        c.Id, c.Platform, c.SecretRef, webhookSecret = c.WebhookSecret,
        c.ToolsPolicy, c.DefaultModelId, c.PersonaId, c.Enabled, c.CreatedAt,
        webhookUrl = $"/api/v1/gateway/{c.Platform}/{c.Id}",
        setWebhookHint = c.Platform == GatewayPlatforms.Telegram
            ? $"https://api.telegram.org/bot<TOKEN>/setWebhook"
              + $"?url=<BASE>/api/v1/gateway/{c.Platform}/{c.Id}"
              + $"&secret_token={c.WebhookSecret}"
            : null,
    };

    private static Task<User?> CurrentUserAsync(
        HttpContext http, AppDbContext db, CancellationToken ct) =>
        AuthEndpoints.FindUserAsync(http, db, ct);
}

/// <summary>Payload de criação de canal de gateway.</summary>
public sealed record GatewayChannelCreateRequest(
    string? Platform, string? BotToken, string? ToolsPolicy,
    string? DefaultModelId, string? PersonaId);

/// <summary>Payload de atualização de canal (campos opcionais).</summary>
public sealed record GatewayChannelUpdateRequest(
    bool? Enabled, string? ToolsPolicy, string? DefaultModelId,
    string? PersonaId, string? BotToken, bool? RotateWebhookSecret);

/// <summary>Payload de atualização de peer (block/unpair).</summary>
public sealed record GatewayPeerUpdateRequest(bool? Blocked, bool? Unpair);

/// <summary>Payload do pareamento por código.</summary>
public sealed record GatewayPairRequest(string? Code);
