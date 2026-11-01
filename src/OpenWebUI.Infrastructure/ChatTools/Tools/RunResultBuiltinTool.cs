using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:run_result</c> — lê o status/resultado de uma run
/// (SPEC-20261010-async-delegate): companheira do
/// <c>delegate_task wait=false</c>, que devolve <c>childRunId</c> na hora
/// e deixa a colheita para depois. Read-only e owner-scoped — sem aprovação.
/// </summary>
public sealed class RunResultBuiltinTool(AppDbContext db, WorktreeService worktrees)
    : IBuiltinChatTool
{
    /// <summary>Cap de caracteres do conteúdo devolvido.</summary>
    private const int MaxResultChars = 6000;

    /// <inheritdoc />
    public string Name => "run_result";

    /// <inheritdoc />
    public string Description =>
        "Read a run's status and result — collect a delegated child run "
            + "(run_id from delegate_task wait=false).";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "run_id": {
              "type": "string",
              "description": "Run id to read (e.g. childRunId returned by delegate_task)."
            }
          },
          "required": ["run_id"]
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
        var runId = args.TryGetProperty("run_id", out var el)
            && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(runId))
        {
            return new BuiltinToolResult("Parâmetro 'run_id' é obrigatório.");
        }

        var run = await db.ChatRuns.AsNoTracking()
            .Where(r => r.Id == runId && r.UserId == context.UserId)
            .Select(r => new
            {
                r.Id, r.ChatId, r.Status, r.PartialContent, r.Error,
            })
            .FirstOrDefaultAsync(ct);
        if (run is null)
        {
            return new BuiltinToolResult(
                $"Run '{runId}' não encontrada para este usuário.");
        }

        // SPEC-20261010-worktree-review: quando a run isolou um worktree,
        // o pai precisa saber que há mudanças a revisar
        // (builtin:worktree_diff) antes de mergear (builtin:worktree_merge).
        var worktree = worktrees.ResolveIsolated(context.UserId, run.Id);
        var hasChanges = worktree is not null
            && !string.IsNullOrWhiteSpace(
                await WorktreeService.StatusPorcelainAsync(worktree, ct));
        var reviewHint = hasChanges
            ? "\n\n[worktree isolado com mudanças pendentes — revise com "
                + "builtin_worktree_diff run_id=" + run.Id + " e mergeie com "
                + "builtin_worktree_merge]"
            : string.Empty;

        var payload = new
        {
            runId = run.Id, chatId = run.ChatId, status = run.Status,
            hasWorktreeChanges = hasChanges,
        };
        return run.Status switch
        {
            ChatRunStatus.Completed => new BuiltinToolResult(
                Truncate(run.PartialContent ?? "(sem conteúdo)", MaxResultChars)
                    + reviewHint,
                payload),
            ChatRunStatus.Failed or ChatRunStatus.Stopped => new BuiltinToolResult(
                $"Run terminou como '{run.Status}'"
                    + (run.Error is { } e ? $": {Truncate(e, 500)}" : string.Empty),
                payload),
            _ => new BuiltinToolResult(
                $"Run ainda '{run.Status}' — chame builtin_run_result novamente mais tarde.",
                payload),
        };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
