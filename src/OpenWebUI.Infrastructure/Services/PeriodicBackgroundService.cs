using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Base dos schedulers periódicos (SPEC-20261115-scheduler-robustness
/// RF-006): tick configurável + jitter + delay inicial opcionais; uma
/// exceção no tick é logada e não derruba o serviço. Substitui os loops
/// <c>while + Task.Delay</c> duplicados dos schedulers ad-hoc.
/// </summary>
public abstract class PeriodicBackgroundService(ILogger logger) : BackgroundService
{
    /// <summary>Intervalo entre ticks.</summary>
    protected abstract TimeSpan Tick { get; }

    /// <summary>Atraso antes do primeiro tick (default: nenhum).</summary>
    protected virtual TimeSpan InitialDelay => TimeSpan.Zero;

    /// <summary>Jitter máximo aplicado ao delay entre ticks (0–1).</summary>
    protected virtual double JitterPct => 0.05;

    /// <summary>Corpo de um tick — exceções são capturadas e logadas.</summary>
    protected abstract Task ExecuteTickAsync(CancellationToken ct);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (InitialDelay > TimeSpan.Zero)
            {
                await Task.Delay(Jittered(InitialDelay), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Tick de {Service} falhou — scheduler segue ativo.",
                    GetType().Name);
            }

            try
            {
                await Task.Delay(Jittered(Tick), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Aplica jitter uniforme de ±<see cref="JitterPct"/>.</summary>
    internal TimeSpan Jittered(TimeSpan baseDelay)
    {
        var jitter = 1.0 + ((Random.Shared.NextDouble() * 2 - 1) * JitterPct);
        return TimeSpan.FromTicks((long)(baseDelay.Ticks * jitter));
    }
}
