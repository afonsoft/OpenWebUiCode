using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services.Gateway;

/// <summary>Resultado de uma tentativa de pareamento.</summary>
public enum GatewayPairResult
{
    /// <summary>Peer vinculado ao usuário.</summary>
    Ok,

    /// <summary>Código inexistente ou já consumido.</summary>
    Invalid,

    /// <summary>Código expirado (TTL).</summary>
    Expired,

    /// <summary>Tentativas esgotadas (5) — peça um código novo na plataforma.</summary>
    Exhausted,
}

/// <summary>
/// Pareamento de peers externos com usuários internos
/// (SPEC-20261023-messaging-gateway D/RF-002): código de 6 dígitos com
/// TTL (default 15min), máximo de 5 tentativas e consumo one-shot —
/// o código some ao parear ou ao expirar/esgotar.
/// </summary>
public sealed class GatewayPairingService(AppDbContext db, ConfigService config)
{
    /// <summary>Limite de tentativas de pair por código.</summary>
    public const int MaxPairAttempts = 5;

    /// <summary>TTL default do código em minutos (kv <c>agent.gateway.pairingTtlMinutes</c>).</summary>
    public const int DefaultTtlMinutes = 15;

    /// <summary>
    /// Devolve o código ativo do peer ou gera um novo (6 dígitos
    /// criptográficos, TTL configurável). Peer bloqueado não recebe código.
    /// </summary>
    public async Task<string?> EnsureCodeAsync(
        GatewayPeer peer, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (peer.Blocked)
        {
            return null;
        }
        if (peer.PairingCode is { Length: > 0 } existing
            && peer.PairingExpiresAt > now
            && peer.PairingAttempts < MaxPairAttempts)
        {
            return existing;
        }
        var ttl = await config.GetAsync(
            "agent.gateway.pairingTtlMinutes", DefaultTtlMinutes, ct);
        peer.PairingCode = RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString("D6");
        peer.PairingExpiresAt = now + Math.Max(1, ttl) * 60L;
        peer.PairingAttempts = 0;
        return peer.PairingCode;
    }

    /// <summary>
    /// Consome um código de pareamento: localiza o peer, valida TTL e
    /// tentativas (cada erro conta) e vincula ao usuário — one-shot.
    /// </summary>
    /// <returns>Resultado e o peer pareado, quando <see cref="GatewayPairResult.Ok"/>.</returns>
    public async Task<(GatewayPairResult Result, GatewayPeer? Peer)> PairAsync(
        string userId, string code, CancellationToken ct)
    {
        var normalized = (code ?? string.Empty).Trim();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var peer = await db.GatewayPeers
            .FirstOrDefaultAsync(p => p.PairingCode == normalized && !p.Blocked, ct);
        if (peer is null)
        {
            return (GatewayPairResult.Invalid, null);
        }
        if (peer.PairingAttempts >= MaxPairAttempts)
        {
            peer.PairingCode = null;
            peer.PairingExpiresAt = null;
            await db.SaveChangesAsync(ct);
            return (GatewayPairResult.Exhausted, null);
        }
        if (peer.PairingExpiresAt <= now)
        {
            peer.PairingCode = null;
            peer.PairingExpiresAt = null;
            await db.SaveChangesAsync(ct);
            return (GatewayPairResult.Expired, null);
        }
        peer.PairingCode = null;
        peer.PairingExpiresAt = null;
        peer.PairedUserId = userId;
        peer.PairedAt = now;
        await db.SaveChangesAsync(ct);
        return (GatewayPairResult.Ok, peer);
    }
}
