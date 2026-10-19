using System.Text.Json;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Reconstrução de estado de uma run a partir dos eventos duráveis
/// (SPEC-20261016-run-recovery RF-003/004): o último <c>snapshot</c> é a
/// base; eventos posteriores são re-aplicados (<c>step_finished</c> abre o
/// conjunto de calls do round, <c>messages_added</c> repõe mensagens,
/// <c>tool_result</c> repõe resultados) e toda call que ficou sem
/// <c>tool_result</c> recebe um resultado sintético
/// <c>interrupted/server restarted</c> — ferramentas potencialmente
/// mutáveis nunca são re-executadas.
/// </summary>
public static class RunRecoveryPlanner
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>Resultado sintético de tool call órfã (RF-004).</summary>
    public const string InterruptedToolResult =
        "{\"interrupted\":true,\"error\":\"server restarted\"}";

    private sealed record SnapshotPayload(
        int Round,
        List<ChatCompletionMessage>? Messages,
        List<ChatCompletionMessage>? ToolMessages,
        int StepCount,
        List<StepUsage>? StepUsages,
        int EmptyRounds,
        int LengthContinuations,
        string? ContinuedAnswer,
        int PendingTodos = -1);

    private sealed record StepFinishedPayload(
        int Round,
        List<string>? Calls);

    private sealed record MessagesAddedPayload(
        List<ChatCompletionMessage>? Messages,
        List<ChatCompletionMessage>? ToolMessages);

    private sealed record ToolResultPayload(
        string? CallId,
        string? Output);

    /// <summary>
    /// Replay dos eventos (ordenados por <see cref="ChatRunEvent.Seq"/>).
    /// Retorna null quando não há snapshot válido — a run cai no legado.
    /// </summary>
    public static RunResumePlan? Replay(IReadOnlyList<ChatRunEvent> events)
    {
        var snapshot = events.LastOrDefault(
            e => e.Type == RunEventWriter.Types.Snapshot);
        if (snapshot is null)
        {
            return null;
        }

        var baseState = TryRead<SnapshotPayload>(snapshot.PayloadJson);
        if (baseState?.Messages is null)
        {
            return null;
        }

        var state = new ReplayState(baseState);
        var lastSeq = snapshot.Seq;
        foreach (var ev in events.Where(e => e.Seq > snapshot.Seq))
        {
            lastSeq = ev.Seq;
            ApplyEvent(ev, state);
        }

        // RF-004: calls registradas pelo step_finished sem tool_result —
        // nunca re-executar; fecha com resultado sintético interrupted.
        var synthetics = state.PendingCalls
            .Select(callId => new ChatCompletionMessage(
                "tool", InterruptedToolResult, ToolCallId: callId))
            .ToList();
        state.Messages.AddRange(synthetics);
        state.ToolMessages.AddRange(synthetics);

        // Snapshot do topo do round R captura o estado pré-round: se o
        // completion de R terminou (step_finished), as mensagens já foram
        // re-aplicadas e o loop entra no round seguinte ao último
        // step_finished; se não, o texto parcial é descartado e o round do
        // snapshot refaz o call ao modelo.
        var resumeRound = state.LastFinishedRound is { } finishedRound
            ? finishedRound + 1
            : Math.Max(baseState.Round, 0);

        return new RunResumePlan(
            resumeRound,
            state.Messages,
            state.ToolMessages,
            baseState.StepCount,
            (baseState.StepUsages ?? []).ToList(),
            baseState.EmptyRounds,
            baseState.LengthContinuations,
            baseState.ContinuedAnswer ?? string.Empty,
            synthetics.Count,
            lastSeq,
            baseState.PendingTodos);
    }

    /// <summary>Estado mutável acumulado pelo replay pós-snapshot.</summary>
    private sealed class ReplayState(SnapshotPayload baseState)
    {
        public List<ChatCompletionMessage> Messages { get; } =
            baseState.Messages!.ToList();
        public List<ChatCompletionMessage> ToolMessages { get; } =
            (baseState.ToolMessages ?? []).ToList();
        public List<string> PendingCalls { get; } = [];
        public int? LastFinishedRound { get; set; }
    }

    /// <summary>Aplica um evento pós-snapshot ao estado do replay.</summary>
    private static void ApplyEvent(ChatRunEvent ev, ReplayState state)
    {
        switch (ev.Type)
        {
            case RunEventWriter.Types.StepFinished:
                if (TryRead<StepFinishedPayload>(ev.PayloadJson) is { } finished)
                {
                    state.LastFinishedRound = finished.Round;
                    state.PendingCalls.AddRange(finished.Calls ?? []);
                }
                break;
            case RunEventWriter.Types.MessagesAdded:
                if (TryRead<MessagesAddedPayload>(ev.PayloadJson) is { } added)
                {
                    state.Messages.AddRange(added.Messages ?? []);
                    state.ToolMessages.AddRange(added.ToolMessages ?? []);
                }
                break;
            case RunEventWriter.Types.ToolResult:
                if (TryRead<ToolResultPayload>(ev.PayloadJson) is { CallId: { } callId } result)
                {
                    var tool = new ChatCompletionMessage(
                        "tool", result.Output ?? string.Empty,
                        ToolCallId: callId);
                    state.Messages.Add(tool);
                    state.ToolMessages.Add(tool);
                    state.PendingCalls.Remove(callId);
                }
                break;
        }
    }

    /// <summary>Serializa o checkpoint do loop para o payload do snapshot.</summary>
    public static string SerializeCheckpoint(RunLoopCheckpoint checkpoint) =>
        JsonSerializer.Serialize(new SnapshotPayload(
            checkpoint.Round,
            checkpoint.Messages.ToList(),
            checkpoint.ToolMessages.ToList(),
            checkpoint.StepCount,
            checkpoint.StepUsages.ToList(),
            checkpoint.EmptyRounds,
            checkpoint.LengthContinuations,
            checkpoint.ContinuedAnswer,
            checkpoint.PendingTodos), JsonOptions);

    private static T? TryRead<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
