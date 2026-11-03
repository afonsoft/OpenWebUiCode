using System.Collections.Concurrent;

namespace OpenWebUI.Infrastructure.Context;

/// <summary>
/// Registro single-flight de compactações em background por chat
/// (SPEC-20261102-context-compactor-v2 RF-002): o spawn dispara uma task
/// que resume o head sem bloquear o turno; uma segunda chamada para o
/// mesmo chat enquanto a task corre é no-op. Singleton DI — as tasks
/// sobrevivem ao fim da run para serem aplicadas na fronteira seguinte.
/// </summary>
public sealed class AsyncCompactionScheduler
{
    private readonly ConcurrentDictionary<string, Lazy<Task>> _inFlight = new();

    /// <summary>
    /// Inicia a task de compactação se nenhuma estiver em voo para o chat.
    /// <see cref="Lazy{T}"/> garante que perdedores de corrida nunca
    /// materializam uma task órfã. Devolve true quando esta chamada
    /// iniciou a task.
    /// </summary>
    public bool TryStart(string chatId, Func<CancellationToken, Task> work)
    {
        var lazy = new Lazy<Task>(
            () => RunAndEvictAsync(chatId, work),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var winner = _inFlight.GetOrAdd(chatId, lazy);
        if (!ReferenceEquals(winner, lazy))
        {
            return false;
        }
        _ = lazy.Value;
        return true;
    }

    /// <summary>
    /// Aguarda a task em voo do chat até <paramref name="timeout"/>.
    /// Devolve true quando completou dentro do prazo — sucesso ou falha
    /// tratada internamente pelo trabalho (o chamador confere o pending
    /// kv). Devolve false quando não há task ou o prazo esgotou.
    /// </summary>
    public async Task<bool> WaitAsync(
        string chatId, TimeSpan timeout, CancellationToken ct)
    {
        if (!_inFlight.TryGetValue(chatId, out var lazy))
        {
            return false;
        }
        try
        {
            await lazy.Value.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Há task em voo para o chat.</summary>
    public bool InFlight(string chatId) => _inFlight.ContainsKey(chatId);

    /// <summary>Esquece o registro do chat (a task em voo segue até o fim).</summary>
    public void Clear(string chatId) => _inFlight.TryRemove(chatId, out _);

    private async Task RunAndEvictAsync(
        string chatId, Func<CancellationToken, Task> work)
    {
        try
        {
            await work(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(chatId, out _);
        }
    }
}
