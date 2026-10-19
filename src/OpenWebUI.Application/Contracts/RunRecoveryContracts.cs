namespace OpenWebUI.Application.Contracts;

/// <summary>Uso de tokens de um step do tool loop (SPEC-20261016-agent-steps-usage).</summary>
public sealed record StepUsage(
    int Step,
    int PromptTokens,
    int CompletionTokens,
    int? ReasoningTokens);

/// <summary>
/// Checkpoint do estado do tool loop numa fronteira de round
/// (SPEC-20261016-run-recovery RF-002): persistido como evento
/// <c>snapshot</c> e reutilizado como seed de retomada — o loop entra em
/// <see cref="Round"/> com <see cref="Messages"/>/<see cref="ToolMessages"/>
/// e os contadores restaurados.
/// </summary>
public sealed record RunLoopCheckpoint(
    int Round,
    IReadOnlyList<ChatCompletionMessage> Messages,
    IReadOnlyList<ChatCompletionMessage> ToolMessages,
    int StepCount,
    IReadOnlyList<StepUsage> StepUsages,
    int EmptyRounds,
    int LengthContinuations,
    string ContinuedAnswer,
    /// <summary>Última contagem de tarefas pendentes conhecida do loop;
    /// -1 quando a lista nunca foi escrita (SPEC-20261019 RF-003).</summary>
    int PendingTodos = -1);

/// <summary>
/// Plano de retomada reconstruído do stream de eventos
/// (SPEC-20261016-run-recovery RF-003/004): estado-base do último snapshot
/// mais o delta pós-snapshot (mensagens, tool results e chamadas órfãs
/// fechadas com resultado sintético <c>interrupted</c>).
/// </summary>
public sealed record RunResumePlan(
    int Round,
    List<ChatCompletionMessage> Messages,
    List<ChatCompletionMessage> ToolMessages,
    int StepCount,
    List<StepUsage> StepUsages,
    int EmptyRounds,
    int LengthContinuations,
    string ContinuedAnswer,
    int RecoveredTools,
    long LastSeq,
    /// <summary>Contagem de tarefas pendentes restaurada do snapshot;
    /// -1 quando o snapshot não registrava a lista.</summary>
    int PendingTodos = -1);
