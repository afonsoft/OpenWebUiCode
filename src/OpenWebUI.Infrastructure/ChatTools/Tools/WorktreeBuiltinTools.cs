using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:worktree_diff</c> — pré-visualiza o diff de um worktree de
/// run isolada (SPEC-20261010-worktree-review): o agente pai revisa o que
/// o sub-agente produziu antes de decidir mergear/descartar via
/// <c>builtin:worktree_merge</c>. Read-only e owner-scoped.
/// </summary>
public sealed class WorktreeDiffBuiltinTool(AppDbContext db, WorktreeService worktrees)
    : IBuiltinChatTool
{
    /// <summary>Cap de caracteres do patch devolvido.</summary>
    private const int MaxPatchChars = 5000;

    /// <inheritdoc />
    public string Name => "worktree_diff";

    /// <inheritdoc />
    public string Description =>
        "Preview the pending changes left by a run's isolated worktree "
            + "(files changed, stat and patch) before merging with "
            + "builtin_worktree_merge.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "run_id": {
              "type": "string",
              "description": "Run id whose worktree to inspect (e.g. childRunId from delegate_task)."
            },
            "stat_only": {
              "type": "boolean",
              "description": "true: only the diffstat (file list); false/omitted: also the patch (truncated)."
            }
          },
          "required": ["run_id"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

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
            .Select(r => new { r.Id, r.Status })
            .FirstOrDefaultAsync(ct);
        if (run is null)
        {
            return new BuiltinToolResult(
                $"Run '{runId}' não encontrada para este usuário.");
        }

        var worktree = worktrees.ResolveIsolated(context.UserId, run.Id);
        if (worktree is null)
        {
            return new BuiltinToolResult(
                $"Run {run.Id} não tem worktree isolado (shared ou já mergeado/removido).");
        }

        var statOnly = args.TryGetProperty("stat_only", out var so)
            && so.ValueKind == JsonValueKind.True;
        var status = await worktrees.StatusPorcelainAsync(worktree, ct);
        var stat = await worktrees.DiffAsync(worktree, ct, stat: true);
        var text = string.IsNullOrWhiteSpace(status)
            ? "Worktree sem mudanças pendentes."
            : $"Mudanças pendentes:\n{status.Trim()}\n\n{stat?.Trim() ?? "(sem diff)"}";
        if (!statOnly && !string.IsNullOrWhiteSpace(status))
        {
            var patch = await worktrees.DiffAsync(worktree, ct);
            if (!string.IsNullOrWhiteSpace(patch))
            {
                text += $"\n\n```diff\n{Truncate(patch, MaxPatchChars)}\n```";
            }
        }

        return new BuiltinToolResult(text, new
        {
            runId = run.Id,
            runStatus = run.Status,
            hasChanges = !string.IsNullOrWhiteSpace(status),
        });
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

/// <summary>
/// <c>builtin:worktree_merge</c> — decisão do agente pai sobre um
/// worktree isolado (SPEC-20261010-worktree-review): <c>merge</c> aplica
/// o diff no workdir do chat da run alvo (conflitos nunca forçados —
/// voltam listados para correção manual); <c>discard</c> remove o
/// worktree sem aplicar nada. Exige aprovação — muta o workspace.
/// </summary>
public sealed class WorktreeMergeBuiltinTool(
    AppDbContext db, WorktreeService worktrees, WorkspaceRepoService repos)
    : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "worktree_merge";

    /// <inheritdoc />
    public string Description =>
        "Merge or discard a run's isolated worktree changes — review first "
            + "with builtin_worktree_diff. action=merge applies the diff to "
            + "the target chat's workspace (conflicts are reported, never "
            + "forced); action=discard drops the worktree.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "run_id": {
              "type": "string",
              "description": "Run id whose worktree to merge or discard."
            },
            "action": {
              "type": "string",
              "enum": ["merge", "discard"],
              "description": "'merge' (default) applies the worktree diff; 'discard' removes it."
            }
          },
          "required": ["run_id"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Exec;

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

        var discard = args.TryGetProperty("action", out var act)
            && act.ValueKind == JsonValueKind.String
            && string.Equals(act.GetString(), "discard", StringComparison.OrdinalIgnoreCase);

        var run = await db.ChatRuns.AsNoTracking()
            .Where(r => r.Id == runId && r.UserId == context.UserId)
            .Select(r => new { r.Id, r.ChatId, r.Status })
            .FirstOrDefaultAsync(ct);
        if (run is null)
        {
            return new BuiltinToolResult(
                $"Run '{runId}' não encontrada para este usuário.");
        }

        var worktree = worktrees.ResolveIsolated(context.UserId, run.Id);
        if (worktree is null)
        {
            return new BuiltinToolResult(
                $"Run {run.Id} não tem worktree isolado (shared ou já mergeado/removido).");
        }

        // O merge vai pro workdir do chat DA RUN alvo (o filho pode ter
        // binding de repo próprio — delegate_task repo target).
        var mainWorkdir = await repos.ResolveWorkdirAsync(
            context.UserId, run.ChatId, ct);
        if (discard)
        {
            await worktrees.RemoveAsync(mainWorkdir, worktree, ct);
            return new BuiltinToolResult(
                $"Worktree da run {run.Id} descartado — nada foi aplicado.",
                new { runId = run.Id, action = "discard" });
        }

        var result = await worktrees.MergeAsync(mainWorkdir, worktree, ct);
        return result.Merged
            ? new BuiltinToolResult(
                result.Applied.Count == 0
                    ? "Worktree sem mudanças — nada aplicado; worktree removido."
                    : $"Merge aplicado ({result.Applied.Count} arquivo(s)): "
                        + string.Join(", ", result.Applied),
                new { runId = run.Id, action = "merge", merged = true,
                    applied = result.Applied })
            : new BuiltinToolResult(
                $"Merge parcial — {result.Conflicts.Count} conflito(s) não "
                    + $"aplicados (worktree preservado p/ correção manual): "
                    + string.Join("; ", result.Conflicts)
                    + (result.Error is { } err ? $" — {err}" : string.Empty),
                new { runId = run.Id, action = "merge", merged = false,
                    applied = result.Applied, conflicts = result.Conflicts });
    }
}
