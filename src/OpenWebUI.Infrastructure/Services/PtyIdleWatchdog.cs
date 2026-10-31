using System.Diagnostics;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Monitor de ociosidade (idle timeout) para sessões PTY de sub-agentes externos (ex: Claude Code / Codex).
/// Detecta término de turno por silêncio no fluxo de saída.
/// # Covers RF-003
/// </summary>
public sealed class PtyIdleWatchdog
{
    private readonly TimeSpan _idleThreshold;
    private readonly TimeSpan _maxTimeout;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private readonly Stopwatch _totalWatch = Stopwatch.StartNew();
    private bool _isIdle;

    public PtyIdleWatchdog(TimeSpan? idleThreshold = null, TimeSpan? maxTimeout = null)
    {
        _idleThreshold = idleThreshold ?? TimeSpan.FromSeconds(2);
        _maxTimeout = maxTimeout ?? TimeSpan.FromMinutes(5);
    }

    public void RecordActivity()
    {
        _lastActivity = DateTimeOffset.UtcNow;
        _isIdle = false;
    }

    public bool CheckStatus(out bool timedOut)
    {
        var now = DateTimeOffset.UtcNow;
        timedOut = _totalWatch.Elapsed > _maxTimeout;

        if (timedOut)
        {
            return true; // Força conclusão por timeout máximo
        }

        if (!_isIdle && (now - _lastActivity) >= _idleThreshold)
        {
            _isIdle = true;
            return true; // Turno concluído por ociosidade (silêncio no PTY)
        }

        return false;
    }
}
