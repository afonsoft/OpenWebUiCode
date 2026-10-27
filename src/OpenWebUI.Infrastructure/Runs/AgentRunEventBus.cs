using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Infrastructure.Runs;

/// <summary>
/// Barramento interno de eventos de run
/// (SPEC-20261021-harness-seams RF-A1): fan-out sequencial para os
/// <see cref="IAgentRunObserver"/> registrados. Observer lento ou falho
/// nunca aborta a run — cada chamada tem timeout próprio
/// (<c>interceptors.timeout_ms</c>, default 5s) e exceção vira
/// <c>LogWarning</c>. Sem observers registrados o publish é no-op.
/// </summary>
public sealed class AgentRunEventBus(
    IEnumerable<IAgentRunObserver> observers,
    IConfiguration configuration,
    ILogger<AgentRunEventBus> logger)
{
    private readonly List<IAgentRunObserver> _observers = [.. observers];

    private int TimeoutMs { get; } =
        int.TryParse(configuration["interceptors.timeout_ms"], out var ms)
            ? Math.Max(100, ms) : 5000;

    /// <summary>
    /// Timeout para eventos terminais (<c>run.completed/stopped/failed</c>):
    /// a run já encerrou e observers pós-run fazem trabalho pesado — o
    /// InstinctExtractor chama o LLM e sempre estourava os 5s dos
    /// interceptors (TaskCanceledException em todo run.completed).
    /// <c>observers.timeout_ms</c>, default 90s.
    /// </summary>
    private int TerminalTimeoutMs { get; } =
        int.TryParse(configuration["observers.timeout_ms"], out var ms)
            ? Math.Max(100, ms) : 90_000;

    /// <summary>Se há observers — permite ao emissor pular serialização.</summary>
    public bool HasObservers => _observers.Count > 0;

    /// <summary>
    /// Entrega o evento a cada observer em ordem de registro, isolando
    /// falhas e aplicando timeout por observer.
    /// </summary>
    public async Task PublishAsync(AgentRunEvent e, CancellationToken ct)
    {
        var timeoutMs = e.Type is "run.completed" or "run.stopped" or "run.failed"
            ? TerminalTimeoutMs
            : TimeoutMs;
        foreach (var observer in _observers)
        {
            try
            {
                using var timeout = CancellationTokenSource
                    .CreateLinkedTokenSource(ct);
                timeout.CancelAfter(timeoutMs);
                await observer.OnRunEventAsync(e, timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Observer {Type} falhou no evento {Event} da run {RunId} — ignorado.",
                    observer.GetType().Name, e.Type, e.RunId);
            }
        }
    }
}
