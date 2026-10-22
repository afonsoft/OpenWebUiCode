namespace OpenWebUI.Application.Interfaces;

/// <summary>
/// Evento interno de run emitido para observers
/// (SPEC-20261021-harness-seams RF-A1). O <see cref="Type"/> usa o
/// namespace <c>run.*</c>/<c>step.*</c>/<c>tool.*</c>; <see cref="PayloadJson"/>
/// é o mesmo payload persistido pelo <c>RunEventWriter</c> (já higienizado
/// pelo <c>SecretScrubber</c> — observers nunca veem secrets).
/// </summary>
/// <param name="RunId">Run que emitiu o evento.</param>
/// <param name="ChatId">Chat da run, quando conhecido pelo emissor.</param>
/// <param name="UserId">Dono da run, quando conhecido pelo emissor.</param>
/// <param name="Type">Tipo do evento (ex.: <c>run.started</c>, <c>tool_call</c>).</param>
/// <param name="PayloadJson">Payload JSON do evento (pode ser vazio).</param>
public sealed record AgentRunEvent(
    string RunId,
    string? ChatId,
    string? UserId,
    string Type,
    string PayloadJson);

/// <summary>
/// Observer read-only do ciclo de vida de uma run
/// (SPEC-20261021-harness-seams RF-A1): recebe cada evento emitido pela
/// run — os duráveis do <c>RunEventWriter</c> (<c>step_started</c>,
/// <c>tool_call</c>, <c>tool_result</c>, <c>step_finished</c>,
/// <c>messages_added</c>, <c>content_delta</c>, <c>phase</c>, <c>note</c>,
/// <c>snapshot</c>) e os de ciclo de vida emitidos pelo executor
/// (<c>run.started</c>, <c>run.completed</c>, <c>run.failed</c>,
/// <c>run.stopped</c>, <c>run.steer</c>, <c>interceptor.veto</c>).
/// Registrado via DI (<see cref="System.Collections.Generic.IEnumerable{T}"/>).
/// Observer lento/falho nunca aborta a run — o bus isola exceções com
/// timeout e <c>LogWarning</c>.
/// </summary>
public interface IAgentRunObserver
{
    /// <summary>Recebe um evento de run. Não deve lançar nem mutar a run.</summary>
    /// <param name="e">Evento emitido.</param>
    /// <param name="ct">Cancelamento (timeout do bus — default 5s).</param>
    Task OnRunEventAsync(AgentRunEvent e, CancellationToken ct);
}
