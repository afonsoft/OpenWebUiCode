using System.Collections.Concurrent;

namespace OpenWebUI.Infrastructure.Context;

/// <summary>
/// Orçamento de extensão de contexto por run (SPEC-20261031-agent-prompt-
/// pack RF-004): a tool <c>extend_context</c> concede extensões até o teto
/// configurado; cada concessão permite ao loop pular uma compactação de
/// emergência (o modelo pediu janela explicitamente em vez de resumir).
/// </summary>
public sealed class ExtendContextBudget
{
    /// <summary>Teto default de extensões por run.</summary>
    public const int DefaultCeiling = 2;

    private sealed class RunState { public int Granted; public int Pending; }

    private readonly ConcurrentDictionary<string, RunState> _runs = new();

    /// <summary>Status possíveis da tool extend_context.</summary>
    public enum Status { Granted, Limit, AtCeiling }

    /// <summary>
    /// Pedido de extensão: <see cref="Status.Granted"/> até
    /// <paramref name="ceiling"/> concessões; depois <see cref="Status.AtCeiling"/>.
    /// <paramref name="ceiling"/> ≤ 0 → <see cref="Status.Limit"/> (desligado).
    /// </summary>
    public Status Request(string runId, int ceiling = DefaultCeiling)
    {
        if (ceiling <= 0)
        {
            return Status.Limit;
        }
        var state = _runs.GetOrAdd(runId, _ => new RunState());
        lock (state)
        {
            if (state.Granted >= ceiling)
            {
                return Status.AtCeiling;
            }
            state.Granted++;
            state.Pending++;
            return Status.Granted;
        }
    }

    /// <summary>
    /// Consome uma concessão para pular a compactação de emergência —
    /// só devolve true quando o modelo já pediu extensão.
    /// </summary>
    public bool TryConsume(string runId)
    {
        if (!_runs.TryGetValue(runId, out var state))
        {
            return false;
        }
        lock (state)
        {
            if (state.Pending <= 0)
            {
                return false;
            }
            state.Pending--;
            return true;
        }
    }

    /// <summary>Concessões pendentes de consumo (testes/diagnóstico).</summary>
    public int Pending(string runId) =>
        _runs.TryGetValue(runId, out var s) ? s.Pending : 0;

    /// <summary>Limpa o estado da run.</summary>
    public void Clear(string runId) => _runs.TryRemove(runId, out _);
}
