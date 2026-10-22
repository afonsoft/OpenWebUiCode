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

    /// <summary>Se há observers — permite ao emissor pular serialização.</summary>
    public bool HasObservers => _observers.Count > 0;

    /// <summary>
    /// Entrega o evento a cada observer em ordem de registro, isolando
    /// falhas e aplicando timeout por observer.
    /// </summary>
    public async Task PublishAsync(AgentRunEvent e, CancellationToken ct)
    {
        foreach (var observer in _observers)
        {
            try
            {
                using var timeout = CancellationTokenSource
                    .CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeoutMs);
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
