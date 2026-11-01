using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Checkpoints do workdir (SPEC-20261009-checkpoints-revert, E16 S6):
/// snapshots por turno criados pelo <see cref="CheckpointService"/> durante
/// runs de agente + revert granular com guarda de drift. Rotas no mesmo
/// grupo <c>/api/v1/workspace/repo</c> da file API — exigem repo vinculado.
///
/// Revert é <b>proibido enquanto houver run ativa</b> (Queued/Running/
/// Paused) do usuário — a run escreve no mesmo workdir e uma restauração
/// concorrente corromperia ambos os lados (decisão registrada na SPEC:
/// bloquear com 409, sem auto-pausa).
/// </summary>
public static class CheckpointEndpoints
{
    /// <summary>Máximo de checkpoints listados (mais novo primeiro).</summary>
    private const int ListTake = 50;

    /// <summary>Mapeia as rotas de checkpoint dentro do grupo workspace/repo.</summary>
    public static void MapCheckpointEndpoints(this IEndpointRouteBuilder app)
    {
        var repo = app.MapGroup("/api/v1/workspace/repo").RequireAuthorization();
        repo.MapGet("/checkpoints", ListAsync);
        repo.MapGet("/checkpoints/{hash}", DetailAsync);
        repo.MapPost("/checkpoints/{hash}/revert", RevertAsync);
    }

    /// <summary>Guard comum: usuário autenticado + repo vinculado → workdir.</summary>
    private static async Task<(string? UserId, string? Workdir, IResult? Reject)> BoundWorkdirAsync(
        HttpContext http, AppDbContext db, WorkspaceRepoService repos, string? chatId, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null, Results.Unauthorized());
        }

        if (chatId is not null
            && !await db.Chats.AsNoTracking().AnyAsync(c => c.Id == chatId && c.UserId == user.Id, ct))
        {
            return (null, null, Results.NotFound(new { detail = "Chat não encontrado." }));
        }

        var binding = await repos.ResolveBindingAsync(user.Id, chatId, ct);
        if (binding.Binding is null)
        {
            return (null, null, Results.NotFound(
                new { detail = "Nenhum repositório vinculado.", bound = false }));
        }

        return (user.Id, await repos.ResolveWorkdirAsync(user.Id, chatId, ct), null);
    }

    // ---------- GET /checkpoints ----------

    /// <summary>Lista os checkpoints do workdir (mais novo primeiro).</summary>
    private static async Task<IResult> ListAsync(
        string? chatId, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        CheckpointService checkpoints, CancellationToken ct)
    {
        var (_, workdir, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }

        var items = await checkpoints.ListAsync(workdir!, ListTake, ct);
        return Results.Ok(items
            .Select(c => new WorkspaceCheckpointItem(c.Hash, c.Turn, c.CreatedAt))
            .ToList());
    }

    // ---------- GET /checkpoints/{hash} ----------

    /// <summary>Detalhe do checkpoint: arquivos cobertos + diff de preview.</summary>
    private static async Task<IResult> DetailAsync(
        HttpContext http, string hash, string? chatId, AppDbContext db, WorkspaceRepoService repos,
        CheckpointService checkpoints, CancellationToken ct)
    {
        var (_, workdir, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }

        var files = await checkpoints.PatchAsync(workdir!, hash, ct);
        var diff = await checkpoints.DiffAsync(workdir!, hash, ct);
        if (files.Count == 0 && diff is null)
        {
            return Results.NotFound(new { detail = "Checkpoint não encontrado." });
        }
        return Results.Ok(new WorkspaceCheckpointDetailResponse(hash, files, diff));
    }

    // ---------- POST /checkpoints/{hash}/revert ----------

    /// <summary>
    /// Reverte o workdir ao conteúdo do checkpoint sobre os arquivos que
    /// divergem dele. Bloqueado (409) enquanto o usuário tiver run ativa —
    /// os dois lados escreveriam no mesmo workdir. Arquivo com drift fora
    /// da trilha (conteúdo atual ≠ checkpoint mais novo) vira conflito e
    /// não é sobrescrito; <c>force=true</c> explícito ignora a guarda.
    /// </summary>
    private static async Task<IResult> RevertAsync(
        HttpContext http, string hash, WorkspaceCheckpointRevertRequest? body, [FromQuery] string? chatId,
        [AsParameters] CheckpointRevertDeps deps, CancellationToken ct)
    {
        var (userId, workdir, reject) = await BoundWorkdirAsync(http, deps.Db, deps.Repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }

        var active = await deps.Db.ChatRuns.AsNoTracking()
            .AnyAsync(r => r.UserId == userId
                && (r.Status == ChatRunStatus.Queued
                    || r.Status == ChatRunStatus.Running
                    || r.Status == ChatRunStatus.Paused), ct);
        if (active)
        {
            return Results.Conflict(new
            {
                detail = "Revert bloqueado: há uma run ativa neste workspace.",
                activeRun = true,
            });
        }

        var result = await deps.Checkpoints.RevertAsync(
            workdir!, hash, body?.Force == true, ct);
        if (result.Reverted.Count == 0 && result.Conflicts.Count == 0)
        {
            return Results.NotFound(new { detail = "Checkpoint não encontrado." });
        }
        return Results.Ok(
            new WorkspaceCheckpointRevertResponse(result.Reverted, result.Conflicts));
    }
}

/// <summary>Bundle DI do revert de checkpoint (S107).</summary>
internal sealed record CheckpointRevertDeps(
    AppDbContext Db, WorkspaceRepoService Repos, CheckpointService Checkpoints);
