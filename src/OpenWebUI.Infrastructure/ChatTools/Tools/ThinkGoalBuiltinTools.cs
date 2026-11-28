using System.Text.Json;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin think</c> — espaço de raciocínio privado do agente
/// (SPEC-20261128-think-goal-tools, inspiração Devin/opencode): o modelo
/// escreve um pensamento que volta como tool result — fica no histórico
/// e no feed de atividade, externalizando a linha de análise sem sair do
/// transcript. Não muta nada; nunca exige aprovação.
/// </summary>
public sealed class ThinkBuiltinTool : IBuiltinChatTool
{
    /// <summary>Cap do pensamento (evita dumps inteiros no histórico).</summary>
    private const int MaxThoughtChars = 2000;

    /// <inheritdoc />
    public string Name => "think";

    /// <inheritdoc />
    public string Description =>
        "Record a private reasoning note before acting — use it to analyze "
            + "the request, weigh options, or sanity-check a plan before "
            + "touching anything. The note stays in the run's history.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "thought": {
              "type": "string",
              "description": "The reasoning note to record."
            }
          },
          "required": ["thought"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var thought = args.TryGetProperty("thought", out var t)
            ? t.GetString()?.Trim()
            : null;
        if (string.IsNullOrEmpty(thought))
        {
            return Task.FromResult(new BuiltinToolResult(
                "Parâmetro 'thought' é obrigatório."));
        }
        if (thought.Length > MaxThoughtChars)
        {
            thought = thought[..MaxThoughtChars] + "…";
        }
        return Task.FromResult(new BuiltinToolResult(
            $"Nota registrada ({thought.Length} chars).",
            new { thought }));
    }
}

/// <summary>
/// <c>builtin goal</c> — declara o objetivo atual da run
/// (SPEC-20261128-think-goal-tools): o texto volta como tool result e
/// permanece no histórico, alinhando os próximos rounds ao objetivo
/// declarado. Útil antes de cadeias longas de ações.
/// </summary>
public sealed class GoalBuiltinTool : IBuiltinChatTool
{
    /// <summary>Cap do objetivo.</summary>
    private const int MaxGoalChars = 300;

    /// <inheritdoc />
    public string Name => "goal";

    /// <inheritdoc />
    public string Description =>
        "Declare the run's current objective in one sentence — keeps every "
            + "following step anchored to it. Use before long action chains "
            + "and update when the objective changes.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "goal": {
              "type": "string",
              "description": "Current objective, one sentence."
            }
          },
          "required": ["goal"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var goal = args.TryGetProperty("goal", out var g)
            ? g.GetString()?.Trim()
            : null;
        if (string.IsNullOrEmpty(goal))
        {
            return Task.FromResult(new BuiltinToolResult(
                "Parâmetro 'goal' é obrigatório."));
        }
        if (goal.Length > MaxGoalChars)
        {
            goal = goal[..MaxGoalChars] + "…";
        }
        return Task.FromResult(new BuiltinToolResult(
            $"Objetivo definido: {goal}",
            new { goal }));
    }
}
