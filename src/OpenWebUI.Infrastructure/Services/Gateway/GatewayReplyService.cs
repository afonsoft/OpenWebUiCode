using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services.Gateway;

/// <summary>
/// Entrega a resposta final de runs de gateway de volta à plataforma
/// (SPEC-20261023-messaging-gateway E/RF-004): implementa
/// <see cref="IChatRunNotifier"/> — em run terminal cujo chat pertence a
/// uma <see cref="GatewaySession"/>, envia o conteúdo final do assistente
/// pelo adapter do canal (chunking + fallback do adapter) e audita a
/// entrega <c>out</c>. Best-effort: falha vira delivery <c>failed</c>,
/// nunca derruba o dispatcher.
/// </summary>
public sealed class GatewayReplyService(
    AppDbContext db,
    IEnumerable<IGatewayAdapter> adapters,
    GatewayInboundService inbound,
    ILogger<GatewayReplyService> logger) : IChatRunNotifier
{
    /// <inheritdoc />
    public async Task RunCompletedAsync(
        ChatRunFinished run, CancellationToken cancellationToken)
    {
        var session = await db.GatewaySessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ChatId == run.ChatId, cancellationToken);
        if (session is null)
        {
            return;
        }
        var peer = await db.GatewayPeers.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == session.PeerId, cancellationToken);
        var channel = peer is null ? null
            : await db.GatewayChannels.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == peer.ChannelId,
                    cancellationToken);
        var adapter = channel is null ? null
            : adapters.FirstOrDefault(a => a.Platform == channel.Platform);
        if (peer is null || channel is null || adapter is null)
        {
            return;
        }
        // Peer bloqueado/canal desligado após o enqueue → não entrega.
        if (peer.Blocked || !channel.Enabled)
        {
            await inbound.AuditAsync(channel.Id, peer.Id, "out", "skipped",
                null, run.RunId, null, null, cancellationToken);
            return;
        }

        // Erro genérico ao peer externo — run.Error detalha internals
        // (provider, paths, modelo) e não sai da superfície interna.
        var text = run.Status == ChatRunStatus.Completed
            ? run.PartialContent
              ?? await LastAssistantTextAsync(run.ChatId, cancellationToken)
            : $"A execução terminou com status '{run.Status}'.";
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var botToken = await inbound.ResolveBotTokenAsync(channel, cancellationToken);
        if (botToken is null)
        {
            await inbound.AuditAsync(channel.Id, peer.Id, "out", "failed",
                null, run.RunId, null,
                "secretRef do bot não resolvido", cancellationToken);
            return;
        }
        var send = await adapter.SendReplyAsync(
            channel, peer, text, botToken, cancellationToken);
        if (!send.Ok)
        {
            logger.LogWarning(
                "Gateway reply falhou (canal {Channel}, run {Run}): {Error}",
                channel.Id, run.RunId, send.Error);
        }
        await inbound.AuditAsync(channel.Id, peer.Id, "out",
            send.Ok ? "ok" : "failed", null, run.RunId,
            text.Length <= 200 ? text : text[..200],
            send.Error, cancellationToken);
    }

    private async Task<string?> LastAssistantTextAsync(
        string chatId, CancellationToken ct) =>
        await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.Role == "assistant")
            .OrderByDescending(m => m.Position)
            .Select(m => m.Content)
            .FirstOrDefaultAsync(ct);
}
