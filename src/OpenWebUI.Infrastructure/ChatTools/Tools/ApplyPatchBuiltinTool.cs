using System.Text;
using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:apply_patch</c> — patch multi-arquivo no formato OpenAI
/// (<c>*** Begin Patch</c> / <c>Add|Update|Delete File</c> /
/// <c>*** End Patch</c>) — SPEC-20261009-worktree-format-hooks RF-003.
/// Todos os paths passam pelo jail (<see cref="WorkspaceFiles.ResolveInside"/>,
/// incluindo o destino de <c>*** Move to:</c>); validação+pré-cálculo de
/// TODOS os arquivos antes de qualquer escrita — patch inválido não
/// aplica nada. Cada arquivo gera diff unificado persistido como
/// file_edit (array <c>files[]</c> no result). Depois roda o format
/// hook — falha vira warning, nunca erro. Mutável → gate de aprovação.
/// </summary>
/// <param name="formatHook">Format hook pós-patch (RF-004).</param>
public sealed class ApplyPatchBuiltinTool(FormatHookService? formatHook = null) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "apply_patch";

    /// <inheritdoc />
    public string Description =>
        "Apply a multi-file patch — call with the OpenAI apply_patch format "
        + "(*** Begin Patch / Add|Update|Delete File / *** End Patch).";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "patch": {
              "type": "string",
              "description": "OpenAI apply_patch text: '*** Begin Patch' … '*** Add File: p' (+lines) / '*** Update File: p' (optional '*** Move to: q'; '@@' separates hunks; ' ' context, '-' removed, '+' added) / '*** Delete File: p' … '*** End Patch'."
            }
          },
          "required": ["patch"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Write;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var patch = args.TryGetProperty("patch", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(patch))
        {
            return new BuiltinToolResult("Parâmetro 'patch' é obrigatório.");
        }

        var ops = ApplyPatch.Parse(patch, out var error);
        if (ops is null)
        {
            return new BuiltinToolResult(error);
        }

        // Fase 1: jail + existência + pré-cálculo de cada arquivo.
        // Nada é escrito aqui — um patch inválido não aplica nada.
        var (plans, planError) = await PlanAllAsync(ops, context, ct);
        if (planError is not null)
        {
            return new BuiltinToolResult(planError);
        }

        // Fase 2: escrita/movimento/remoção.
        var results = await ApplyAllAsync(plans!, ct);

        // Format hook uma vez com todos os arquivos tocados (RF-004).
        var formatWarning = formatHook is null ? null : await formatHook.RunForUserAsync(
            context.UserId, context.WorkspacePath,
            results.Select(r => r.Path).ToList(), ct, context.ChatId);

        return BuildResult(results, formatWarning);
    }

    /// <summary>Fase 1: valida e pré-calcula cada arquivo (nada é escrito).</summary>
    private static async Task<(List<FilePlan>? Plans, string? Error)> PlanAllAsync(
        IReadOnlyList<PatchOp> ops, BuiltinToolContext context, CancellationToken ct)
    {
        var root = Path.GetFullPath(context.WorkspacePath);
        var plans = new List<FilePlan>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in ops)
        {
            var (plan, error) = await PlanOpAsync(op, context, root, seen, ct);
            if (error is not null)
            {
                return (null, error);
            }
            plans.Add(plan!);
        }
        return (plans, null);
    }

    /// <summary>Valida uma op (jail, duplicata, existência) e calcula o novo conteúdo.</summary>
    private static async Task<(FilePlan? Plan, string? Error)> PlanOpAsync(
        PatchOp op, BuiltinToolContext context, string root,
        HashSet<string> seen, CancellationToken ct)
    {
        var full = WorkspaceFiles.ResolveInside(context.WorkspacePath, op.Path, out var jailError);
        if (full is null)
        {
            return (null, $"{op.Path}: {jailError}");
        }
        var rel = WorkspaceFiles.RelativeOf(root, full);
        if (!seen.Add(rel))
        {
            return (null, $"Arquivo '{rel}' aparece 2× no patch.");
        }

        var (fullMove, relMove, moveError) = ResolveMoveTarget(op, context, root);
        if (moveError is not null)
        {
            return (null, moveError);
        }

        var exists = File.Exists(full);
        var oldText = exists ? await File.ReadAllTextAsync(full, ct) : null;
        var (newText, error) = ComputeNewText(op, rel, exists, oldText, fullMove, relMove);
        if (error is not null)
        {
            return (null, error);
        }
        return (new FilePlan(op.Kind, rel, full, relMove, fullMove, oldText, newText), null);
    }

    /// <summary>Resolve o destino de <c>*** Move to:</c> pelo jail (quando presente).</summary>
    private static (string? FullMove, string? RelMove, string? Error) ResolveMoveTarget(
        PatchOp op, BuiltinToolContext context, string root)
    {
        if (op.MoveTo is null)
        {
            return (null, null, null);
        }
        var fullMove = WorkspaceFiles.ResolveInside(context.WorkspacePath, op.MoveTo, out var moveError);
        if (fullMove is null)
        {
            return (null, null, $"{op.MoveTo}: {moveError}");
        }
        return (fullMove, WorkspaceFiles.RelativeOf(root, fullMove), null);
    }

    /// <summary>Conteúdo resultante da op (add/update/delete) com a mesma ordem de checagens.</summary>
    private static (string? NewText, string? Error) ComputeNewText(
        PatchOp op, string rel, bool exists, string? oldText,
        string? fullMove, string? relMove) => op.Kind switch
    {
        "add" => ComputeAddText(op, rel, exists),
        "update" => ComputeUpdateText(op, rel, exists, oldText, fullMove, relMove),
        "delete" => ComputeDeleteText(rel, exists),
        _ => (null, $"Operação desconhecida '{op.Kind}'."),
    };

    private static (string? NewText, string? Error) ComputeAddText(
        PatchOp op, string rel, bool exists)
    {
        if (exists)
        {
            return (null, $"Add File '{rel}': arquivo já existe.");
        }
        var newText = string.Join('\n', op.AddLines!);
        if (Encoding.UTF8.GetByteCount(newText) > WorkspaceFiles.MaxFileBytes)
        {
            return (null, $"Add File '{rel}': conteúdo excede {WorkspaceFiles.MaxFileBytes / 1024}KB.");
        }
        return (newText, null);
    }

    private static (string? NewText, string? Error) ComputeUpdateText(
        PatchOp op, string rel, bool exists, string? oldText,
        string? fullMove, string? relMove)
    {
        if (!exists)
        {
            return (null, $"Update File '{rel}': arquivo não existe.");
        }
        string? newText;
        if (op.Hunks!.Count > 0)
        {
            newText = ApplyPatch.ApplyHunks(oldText!, op.Hunks, out var hunkError);
            if (newText is null)
            {
                return (null, $"Update File '{rel}': {hunkError}");
            }
        }
        else
        {
            newText = oldText;
        }
        if (fullMove is not null && File.Exists(fullMove))
        {
            return (null, $"Move to '{relMove}': destino já existe.");
        }
        if (Encoding.UTF8.GetByteCount(newText) > WorkspaceFiles.MaxFileBytes)
        {
            return (null, $"Update File '{rel}': resultado excede {WorkspaceFiles.MaxFileBytes / 1024}KB.");
        }
        return (newText, null);
    }

    private static (string? NewText, string? Error) ComputeDeleteText(string rel, bool exists) =>
        exists
            ? (null, null)
            : (null, $"Delete File '{rel}': arquivo não existe.");

    /// <summary>Fase 2: executa escrita/movimento/remoção de cada plano.</summary>
    private static async Task<List<(string Path, string Action, UnifiedDiff.Result Diff)>> ApplyAllAsync(
        IReadOnlyList<FilePlan> plans, CancellationToken ct)
    {
        var results = new List<(string Path, string Action, UnifiedDiff.Result Diff)>();
        foreach (var plan in plans)
        {
            results.Add(await ApplyPlanAsync(plan, ct));
        }
        return results;
    }

    /// <summary>Aplica um plano (o Kind já foi validado na fase 1).</summary>
    private static async Task<(string Path, string Action, UnifiedDiff.Result Diff)> ApplyPlanAsync(
        FilePlan plan, CancellationToken ct)
    {
        switch (plan.Kind)
        {
            case "add":
                Directory.CreateDirectory(Path.GetDirectoryName(plan.Full)!);
                await File.WriteAllTextAsync(plan.Full, plan.NewText!, ct);
                return (plan.Rel, "add", UnifiedDiff.Compute(plan.Rel, null, plan.NewText!));

            case "update":
                if (plan.FullMove is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(plan.FullMove)!);
                    File.Move(plan.Full, plan.FullMove);
                }
                if (plan.NewText != plan.OldText)
                {
                    await File.WriteAllTextAsync(plan.FullMove ?? plan.Full, plan.NewText!, ct);
                }
                return (plan.RelMove ?? plan.Rel,
                    plan.FullMove is not null ? "rename+update" : "update",
                    UnifiedDiff.Compute(
                        plan.RelMove ?? plan.Rel, plan.OldText, plan.NewText!));

            default: // "delete" — demais kinds foram rejeitados na fase 1.
                File.Delete(plan.Full);
                return (plan.Rel, "delete",
                    UnifiedDiff.Compute(plan.Rel, plan.OldText, ""));
        }
    }

    /// <summary>Resumo textual + payload estruturado (files[] com diff truncado).</summary>
    private static BuiltinToolResult BuildResult(
        List<(string Path, string Action, UnifiedDiff.Result Diff)> results,
        string? formatWarning)
    {
        var sb = new StringBuilder($"Patch aplicado: {results.Count} arquivo(s).");
        foreach (var (path, action, diff) in results)
        {
            sb.Append($"\n{action}: {path} (+{diff.Added}/-{diff.Removed})");
        }
        if (formatWarning is not null)
        {
            sb.Append($"\n[format] {formatWarning}");
        }

        return new BuiltinToolResult(sb.ToString(), new
        {
            files = results.Select(r => new
            {
                path = r.Path,
                action = r.Action,
                added = r.Diff.Added,
                removed = r.Diff.Removed,
                diff = FileWriteBuiltinTool.Truncate(r.Diff.Text, 8192),
            }).ToList(),
            filesChanged = results.Count,
            formatWarning,
        });
    }

    /// <summary>Plano de um arquivo depois da validação (fase 1).</summary>
    private sealed record FilePlan(
        string Kind, string Rel, string Full,
        string? RelMove, string? FullMove,
        string? OldText, string? NewText);
}
