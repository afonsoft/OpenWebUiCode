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
        string? ContinuedAnswer);

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

        SnapshotPayload? baseState;
        try
        {
            baseState = JsonSerializer.Deserialize<SnapshotPayload>(
                snapshot.PayloadJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        if (baseState?.Messages is null)
        {
            return null;
        }

        var messages = baseState.Messages.ToList();
        var toolMessages = (baseState.ToolMessages ?? []).ToList();
        var pendingCalls = new List<string>();
        int? lastFinishedRound = null;
        var recovered = 0;
        var lastSeq = snapshot.Seq;

        foreach (var ev in events.Where(e => e.Seq > snapshot.Seq))
        {
            lastSeq = ev.Seq;
            switch (ev.Type)
            {
                case RunEventWriter.Types.StepFinished:
                    var finished = TryRead<StepFinishedPayload>(ev.PayloadJson);
                    if (finished is not null)
                    {
                        lastFinishedRound = finished.Round;
                        pendingCalls.AddRange(finished.Calls ?? []);
                    }
                    break;
                case RunEventWriter.Types.MessagesAdded:
                    var added = TryRead<MessagesAddedPayload>(ev.PayloadJson);
                    if (added is not null)
                    {
                        messages.AddRange(added.Messages ?? []);
                        toolMessages.AddRange(added.ToolMessages ?? []);
                    }
                    break;
                case RunEventWriter.Types.ToolResult:
                    var result = TryRead<ToolResultPayload>(ev.PayloadJson);
                    if (result?.CallId is { } callId)
                    {
                        var tool = new ChatCompletionMessage(
                            "tool", result.Output ?? string.Empty,
                            ToolCallId: callId);
                        messages.Add(tool);
                        toolMessages.Add(tool);
                        pendingCalls.Remove(callId);
                    }
                    break;
            }
        }

        // RF-004: calls registradas pelo step_finished sem tool_result —
        // nunca re-executar; fecha com resultado sintético interrupted.
        foreach (var callId in pendingCalls)
        {
            var synthetic = new ChatCompletionMessage(
                "tool", InterruptedToolResult, ToolCallId: callId);
            messages.Add(synthetic);
            toolMessages.Add(synthetic);
            recovered++;
        }

        // Snapshot do topo do round R captura o estado pré-round: se o
        // completion de R terminou (step_finished), as mensagens já foram
        // re-aplicadas e o loop entra no round seguinte ao último
        // step_finished; se não, o texto parcial é descartado e o round do
        // snapshot refaz o call ao modelo.
        var resumeRound = lastFinishedRound is { } finishedRound
            ? finishedRound + 1
            : Math.Max(baseState.Round, 0);

        return new RunResumePlan(
            resumeRound,
            messages,
            toolMessages,
            baseState.StepCount,
            (baseState.StepUsages ?? []).ToList(),
            baseState.EmptyRounds,
            baseState.LengthContinuations,
            baseState.ContinuedAnswer ?? string.Empty,
            recovered,
            lastSeq);
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
            checkpoint.ContinuedAnswer), JsonOptions);

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
