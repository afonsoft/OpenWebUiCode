using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Runs;

/// <summary>
/// Fecha o audit do turno de persona ao término da run
/// (SPEC-20261016-agent-personas-channels RF-004): preenche
/// <see cref="AgentTurn.OutText"/> (última resposta assistant do chat ou
/// erro da run) e <see cref="AgentTurn.Status"/>, e poda o log a 200
/// turnos por persona. Best-effort: falha nunca derruba o dispatcher.
/// </summary>
public sealed class PersonaTurnNotifier(AppDbContext db) : IChatRunNotifier
{
    /// <summary>Máximo de turnos auditados por persona.</summary>
    public const int MaxTurnsPerPersona = 200;

    /// <inheritdoc />
    public async Task RunCompletedAsync(ChatRunFinished run, CancellationToken cancellationToken)
    {
        var turns = await db.AgentTurns
            .Where(t => t.RunId == run.RunId && t.Status == "running")
            .ToListAsync(cancellationToken);
        if (turns.Count == 0)
        {
            return;
        }

        var outText = run.Status == ChatRunStatus.Completed
            ? await db.ChatMessages.AsNoTracking()
                .Where(m => m.ChatId == run.ChatId && m.Role == "assistant")
                .OrderByDescending(m => m.Position)
                .Select(m => m.Content)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        foreach (var turn in turns)
        {
            turn.OutText = PersonaDispatchService.Truncate(
                outText ?? run.Error ?? $"Run finalizada: {run.Status}");
            turn.Status = run.Status;
        }

        foreach (var personaId in turns.Select(t => t.PersonaId).Distinct())
        {
            var overflow = await db.AgentTurns
                .Where(t => t.PersonaId == personaId)
                .OrderByDescending(t => t.Timestamp)
                .Skip(MaxTurnsPerPersona)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);
            if (overflow.Count > 0)
            {
                await db.AgentTurns.Where(t => overflow.Contains(t.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
