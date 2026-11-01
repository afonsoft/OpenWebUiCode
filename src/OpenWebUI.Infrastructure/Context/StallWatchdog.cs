using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace OpenWebUI.Infrastructure.Context;

/// <summary>
/// Watchdog de stall do loop (SPEC-20261031-agent-prompt-pack RF-005):
/// conta iterações consecutivas em que o modelo repete a mesma tool com os
/// mesmos argumentos <em>e</em> obtém o mesmo resultado (sem side-effect
/// observável). Ao atingir o threshold devolve um nudge para o executor
/// injetar como nota de contexto; thresholds múltiplos escalam o tom.
/// </summary>
public sealed class StallWatchdog
{
    /// <summary>Estado por run.</summary>
    private sealed class RunState
    {
        public string? LastHash;
        public int Streak;
        public int Nudges;
    }

    private readonly ConcurrentDictionary<string, RunState> _runs = new();

    /// <summary>Threshold default de iterações repetidas até o nudge.</summary>
    public const int DefaultThreshold = 4;

    /// <summary>
    /// Registra um tool result; devolve o nudge quando a streak atinge o
    /// threshold (a cada threshold adicional, com escalada após o 1º nudge).
    /// </summary>
    /// <param name="runId">Run atual.</param>
    /// <param name="toolName">Tool executada.</param>
    /// <param name="argsJson">Argumentos normalizados (JSON).</param>
    /// <param name="output">Texto do resultado (side-effect proxy).</param>
    /// <param name="threshold">Iterações repetidas até o nudge.</param>
    public string? Record(
        string runId, string toolName, string? argsJson, string output,
        int threshold = DefaultThreshold)
    {
        var state = _runs.GetOrAdd(runId, _ => new RunState());
        var hash = Hash(toolName, argsJson, output);
        lock (state)
        {
            state.Streak = hash == state.LastHash ? state.Streak + 1 : 0;
            state.LastHash = hash;
            if (state.Streak < threshold)
            {
                return null;
            }
            state.Streak = 0;
            state.Nudges++;
            return state.Nudges == 1
                ? $"[stall watch] You appear to be repeating {toolName} with "
                  + "the same arguments and identical results — reassess "
                  + "before retrying; pick a different approach or move on."
                : $"[stall watch] Still repeating {toolName} (stall #{state.Nudges}) "
                  + "— stop and report what is blocking you instead of retrying.";
        }
    }

    /// <summary>Limpa o estado da run (fim/cancelamento).</summary>
    public void Clear(string runId) => _runs.TryRemove(runId, out _);

    /// <summary>Streak atual (testes/diagnóstico).</summary>
    public int StreakOf(string runId) =>
        _runs.TryGetValue(runId, out var s) ? s.Streak : 0;

    private static string Hash(string tool, string? args, string output)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{tool}|{args ?? ""}|{output}"));
        return Convert.ToHexString(bytes);
    }
}
