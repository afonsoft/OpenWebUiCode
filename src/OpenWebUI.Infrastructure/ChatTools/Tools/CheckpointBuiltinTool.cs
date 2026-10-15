using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:checkpoint</c> — abre um limiar de contexto
/// (SPEC-20261013-agent-context-management RF-001): marca a posição atual
/// do histórico persistido antes de um trecho exploratório; as mensagens
/// seguintes ficam cobertas por uma boundary aberta até a tool irmã
/// <c>rewind</c> fechá-la em relatório. Estado interno da run — sem
/// aprovação, nada é deletado.
/// </summary>
public sealed class CheckpointBuiltinTool(ContextBoundaryService boundaries) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "checkpoint";

    /// <inheritdoc />
    public string Description =>
        "Open a context checkpoint before an exploratory stretch — messages "
            + "after this point can later be collapsed by 'rewind' into a "
            + "short report, freeing context without losing the record.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "goal": {
              "type": "string",
              "description": "What the upcoming stretch explores (why the checkpoint exists)."
            }
          },
          "required": ["goal"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (!await boundaries.EnabledAsync(ct))
        {
            return new BuiltinToolResult("Tools de contexto estão desabilitadas pelo administrador.");
        }
        if (string.IsNullOrEmpty(context.ChatId))
        {
            return new BuiltinToolResult("checkpoint requer chat persistido — indisponível no playground.");
        }
        if (!args.TryGetProperty("goal", out var goalEl)
            || goalEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(goalEl.GetString()))
        {
            return new BuiltinToolResult("Parâmetro 'goal' (string) é obrigatório.");
        }

        var from = await boundaries.MarkAsync(
            context.ChatId, context.RunId, goalEl.GetString()!.Trim(), ct);
        return from is null
            ? new BuiltinToolResult(
                "Já existe um checkpoint aberto nesta run — feche com rewind(report) antes de abrir outro.")
            : new BuiltinToolResult(
                $"Checkpoint aberto na posição {from.Value} — explore à vontade; feche com rewind(report).");
    }
}

/// <summary>
/// <c>builtin:rewind</c> — fecha o checkpoint aberto e colapsa o trecho
/// explorado num relatório (SPEC-20261013-agent-context-management
/// RF-001/RF-002): as mensagens cobertas somem do contexto reconstruído e
/// o relatório passa a representá-las. Sem checkpoint aberto (ou intervalo
/// vazio) devolve erro de tool — a run nunca quebra.
/// </summary>
public sealed class RewindBuiltinTool(ContextBoundaryService boundaries) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "rewind";

    /// <inheritdoc />
    public string Description =>
        "Close the open checkpoint: collapse everything explored since "
            + "'checkpoint' into 'report' — the stretch leaves the model "
            + "context and only the report stays. Use when the exploration "
            + "finished and its details are no longer worth the tokens.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "report": {
              "type": "string",
              "description": "What the explored stretch found/decided — replaces it in context."
            }
          },
          "required": ["report"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (!await boundaries.EnabledAsync(ct))
        {
            return new BuiltinToolResult("Tools de contexto estão desabilitadas pelo administrador.");
        }
        if (string.IsNullOrEmpty(context.ChatId))
        {
            return new BuiltinToolResult("rewind requer chat persistido — indisponível no playground.");
        }
        if (!args.TryGetProperty("report", out var reportEl)
            || reportEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(reportEl.GetString()))
        {
            return new BuiltinToolResult(
                "Parâmetro 'report' (string) é obrigatório — o relatório não pode ser vazio.");
        }

        var committed = await boundaries.CommitAsync(
            context.ChatId, context.RunId, reportEl.GetString()!.Trim(), ct);
        if (committed is null)
        {
            return new BuiltinToolResult(
                "Nenhum checkpoint aberto — chame checkpoint(goal) antes do trecho a colapsar.");
        }
        if (committed.Value.Covered < 0)
        {
            return new BuiltinToolResult(
                "Checkpoint fechado sem intervalo a colapsar (nenhuma mensagem persistida depois dele).");
        }
        return new BuiltinToolResult(
            $"Contexto colapsado: {committed.Value.Covered} mensagens viraram o relatório "
            + "— só ele segue no contexto do modelo.");
    }
}
