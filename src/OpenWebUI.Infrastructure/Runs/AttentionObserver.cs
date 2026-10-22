using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.Runs;

/// <summary>
/// Observer que transforma término de run em item do inbox de atenção
/// (SPEC-20261022-ade-surfaces RF-C2): <c>run.completed</c>/
/// <c>run.failed</c> levantam <c>run:{runId}</c> para o dono — o usuário
/// que disparou e fechou a aba acha a run pronta ao voltar. Runs paradas
/// pelo usuário (<c>run.stopped</c>) não geram item — foram decisão dele.
/// Usa um scope independente: salvar no <see cref="AppDbContext"/>
/// compartilhado da run flusharia estado intermediário por acidente.
/// </summary>
public sealed class AttentionObserver(
    IServiceScopeFactory scopes,
    ILogger<AttentionObserver> logger) : IAgentRunObserver
{
    /// <inheritdoc/>
    public async Task OnRunEventAsync(AgentRunEvent e, CancellationToken ct)
    {
        var kind = e.Type switch
        {
            "run.completed" => "run_completed",
            "run.failed" => "run_failed",
            _ => null,
        };
        if (kind is null || e.UserId is null || e.ChatId is null)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var title = await db.Chats.AsNoTracking()
            .Where(c => c.Id == e.ChatId)
            .Select(c => c.Title)
            .FirstOrDefaultAsync(ct) ?? e.ChatId;

        await AttentionService.RaiseAsync(
            db, e.UserId, $"run:{e.RunId}", kind, title, e.ChatId, ct);
        logger.LogDebug("attention {Kind} levantado para run {RunId}.",
            kind, e.RunId);
    }
}
