using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Fan-out de eventos SSE de uma run (SPEC-20261007-chat-detached-runs):
/// mantém backlog seq-numerado por run e distribui para assinantes ao vivo.
/// Cliente que anexa atrasado recebe backlog (replay sem gap) e depois o
/// stream vivo; backlog morre com o processo (runs órfãs são marcadas
/// <c>interrupted</c> no boot pelo dispatcher).
/// </summary>
public sealed class ChatRunBroadcaster
{
    /// <summary>Evento numerado: <paramref name="Payload"/> é a linha `data:` pronta.</summary>
    public sealed record RunEvent(int Seq, string Payload);

    private sealed class RunStream
    {
        public readonly List<RunEvent> Backlog = [];
        public readonly List<Channel<RunEvent>> Subscribers = [];
        public int LastSeq;
        public bool Closed;
    }

    private readonly ConcurrentDictionary<string, RunStream> _streams = new();
    private const int MaxBacklog = 4096;

    /// <summary>Publica um evento na run (backlog + assinantes vivos).</summary>
    public void Publish(string runId, string payload)
    {
        var stream = _streams.GetOrAdd(runId, _ => new RunStream());
        RunEvent evt;
        lock (stream)
        {
            if (stream.Closed)
            {
                return;
            }

            evt = new RunEvent(++stream.LastSeq, payload);
            stream.Backlog.Add(evt);
            if (stream.Backlog.Count > MaxBacklog)
            {
                stream.Backlog.RemoveRange(0, stream.Backlog.Count - MaxBacklog);
            }
        }

        Channel<RunEvent>[] subs;
        lock (stream)
        {
            subs = stream.Subscribers.ToArray();
        }
        foreach (var sub in subs)
        {
            sub.Writer.TryWrite(evt);
        }
    }

    /// <summary>Fecha o stream da run: assinantes recebem fim de canal.</summary>
    public void Complete(string runId)
    {
        if (!_streams.TryGetValue(runId, out var stream))
        {
            return;
        }

        lock (stream)
        {
            stream.Closed = true;
            foreach (var sub in stream.Subscribers)
            {
                sub.Writer.TryComplete();
            }
            stream.Subscribers.Clear();
        }
    }

    /// <summary>Descarta o backlog da run (limpeza periódica de runs finais).</summary>
    public void Drop(string runId) => _streams.TryRemove(runId, out _);

    /// <summary>
    /// Snapshot do backlog para o polling-fallback (SPEC-20261110-
    /// run-stream-polling-fallback): devolve os eventos com seq &gt;
    /// <paramref name="since"/> e se o stream já fechou — sem assinar.
    /// Run sem stream (sem evento publicado ou backlog descartado)
    /// responde vazio/aberto: o status no banco é quem fecha o ciclo.
    /// </summary>
    public (List<RunEvent> Events, bool Closed) GetEventsSince(string runId, int since)
    {
        if (!_streams.TryGetValue(runId, out var stream))
        {
            return ([], false);
        }

        lock (stream)
        {
            return (stream.Backlog.Where(e => e.Seq > since).ToList(), stream.Closed);
        }
    }

    /// <summary>
    /// Assina a run: primeiro os eventos com seq &gt; <paramref name="since"/>
    /// do backlog (replay), depois os vivos até o stream fechar.
    /// </summary>
    public async IAsyncEnumerable<RunEvent> SubscribeAsync(
        string runId, int since = 0, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var stream = _streams.GetOrAdd(runId, _ => new RunStream());
        var channel = Channel.CreateUnbounded<RunEvent>();
        List<RunEvent> backlog;
        bool subscribe;
        lock (stream)
        {
            backlog = stream.Backlog.Where(e => e.Seq > since).ToList();
            subscribe = !stream.Closed;
            if (subscribe)
            {
                stream.Subscribers.Add(channel);
            }
        }

        foreach (var evt in backlog)
        {
            yield return evt;
        }
        if (!subscribe)
        {
            yield break;
        }

        try
        {
            while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var evt))
                {
                    yield return evt;
                }
            }
        }
        finally
        {
            lock (stream)
            {
                stream.Subscribers.Remove(channel);
            }
        }
    }
}
