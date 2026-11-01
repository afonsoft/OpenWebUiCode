using System.Text.Json;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:write_plan</c> (SPEC-20261031-plan-mode-v2 RF-001/RF-006):
/// grava o plano da sessão em <c>{plansDir}/YYYY-MM-DD-{slug}.md</c> —
/// chamadas repetidas na mesma sessão atualizam o mesmo arquivo. Única
/// escrita livre no modo plan (só toca o diretório de planos, sem
/// aprovação); em code mode também funciona — plano como artefato.
/// </summary>
public sealed class WritePlanBuiltinTool(PlanFileService plans)
    : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "write_plan";

    /// <inheritdoc />
    public string Description =>
        "Write the current plan to plans/YYYY-MM-DD-<slug>.md in the "
        + "workspace (repeat calls update the same file). Follows the plan "
        + "template: Summary, Implementation Steps, Files to Modify, "
        + "Verification, Risks.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "content": {
              "type": "string",
              "description": "The full plan document, in markdown."
            },
            "title": {
              "type": "string",
              "description": "Short plan title — overrides the slug derived from the first heading."
            }
          },
          "required": ["content"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var content = args.TryGetProperty("content", out var c)
            && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? string.Empty
                : string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(new BuiltinToolResult(
                "Erro: informe o plano completo em `content`.",
                Refused: true));
        }
        var title = args.TryGetProperty("title", out var t)
            && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

        var written = plans.Write(
            context.WorkspacePath,
            context.ChatId ?? context.RunId ?? "session",
            content, title, context.RunId);

        var text = $"Plano gravado em `{written.RelativePath}` "
            + $"({written.Bytes} bytes).";
        if (written.MissingSections.Count > 0)
        {
            text += " Aviso: seções do template ausentes — "
                + string.Join(", ", written.MissingSections) + ".";
        }
        return Task.FromResult(new BuiltinToolResult(text,
            Result: new
            {
                path = written.RelativePath,
                bytes = written.Bytes,
                missingSections = written.MissingSections,
            }));
    }
}
