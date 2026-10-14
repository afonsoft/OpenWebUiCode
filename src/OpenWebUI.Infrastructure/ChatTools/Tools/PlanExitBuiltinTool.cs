using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:plan_exit</c> — saída do modo plan do agente
/// (SPEC-20261009-agent-modes-plan-build RF-003, padrão do
/// <c>plan_exit</c> do opencode): só é anunciada em modo <c>plan</c>.
/// Grava o plano em <c>.openwebui/plans/{runId}.md</c> dentro do workdir
/// (jail por <see cref="WorkspaceFiles.ResolveInside"/>) e o gate do
/// executor encadeia o prompt "Executar este plano?" — aprovado, promove
/// o chat a <c>build</c> e devolve a instrução de execução; negado,
/// devolve o caminho gravado para o modelo ajustar o plano.
/// </summary>
public sealed class PlanExitBuiltinTool : IBuiltinChatTool
{
    /// <summary>Prefixo jailed onde os planos ficam sob o workdir.</summary>
    public const string PlansDir = ".openwebui/plans";

    /// <inheritdoc />
    public string Name => "plan_exit";

    /// <inheritdoc />
    public string Description =>
        "Plan-mode only: call when the plan is ready — saves it to "
        + ".openwebui/plans/ and asks the user to approve execution.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "plan": {
              "type": "string",
              "description": "The full plan, in markdown."
            },
            "title": {
              "type": "string",
              "description": "Short plan title (optional)."
            }
          },
          "required": ["plan"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Exec;

    /// <inheritdoc />
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var plan = args.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(plan))
        {
            return Task.FromResult(new BuiltinToolResult(
                "Erro: informe o plano completo em `plan`.", Refused: true));
        }

        var title = args.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;
        var fileName = $"{SanitizeName(context.RunId ?? "plan")}.md";
        var relative = $"{PlansDir}/{fileName}";
        var full = WorkspaceFiles.ResolveInside(context.WorkspacePath, relative, out var error);
        if (full is null)
        {
            return Task.FromResult(new BuiltinToolResult(
                $"Erro: caminho do plano inválido — {error}", Refused: true));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var doc = $"# {title ?? "Plano"}\n\n{plan}\n\n---\n"
            + $"_Run `{context.RunId}` — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC_\n";
        File.WriteAllText(full, doc);
        return Task.FromResult(new BuiltinToolResult(
            $"Plano gravado em `{relative}`.",
            Result: new { path = relative }));
    }

    /// <summary>Nome de arquivo seguro a partir do id da run.</summary>
    private static string SanitizeName(string name)
    {
        var chars = name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length is 0 ? "plan" : new string(chars);
    }
}
