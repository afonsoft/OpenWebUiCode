using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenWebUI.Infrastructure.Lsp;

/// <summary>
/// Peer JSON-RPC 2.0 sobre <see cref="LspFraming"/> (SPEC-20261009-lsp-diagnostics):
/// correla requests por <c>id</c> (<see cref="ConcurrentDictionary{TKey,TValue}"/>
/// de TCS), serializa writes com um semáforo e despacha notificações via evento
/// (<c>textDocument/publishDiagnostics</c> etc.). Requests do servidor para o
/// cliente (<c>workspace/configuration</c>, <c>client/registerCapability</c>)
/// recebem resposta padrão — <c>null</c> ou lista de nulls — para não travar o
/// peer. EOF/erro de parse falha todos os pendentes.
/// </summary>
public sealed class JsonRpcPeer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private long _nextId;
    private Task? _reader;
    private Exception? _failure;
    private int _disposed;

    public JsonRpcPeer(Stream input, Stream output, ILogger? logger = null)
    {
        _input = input;
        _output = output;
        _logger = logger;
    }

    /// <summary>Notificação recebida do servidor (method, params — ou null).</summary>
    public event Action<string, JsonElement>? Notification;

    /// <summary>Motivo da queda (EOF/parse) — requests subsequentes falham rápido.</summary>
    public Exception? Failure => _failure;

    /// <summary>
    /// Envia um request e aguarda a resposta (com timeout/cancelamento).
    /// Erro JSON-RPC vira <see cref="LspRequestException"/>; queda do stream
    /// vira <see cref="IOException"/>.
    /// </summary>
    public async Task<JsonElement> RequestAsync(
        string method, object? @params, TimeSpan timeout, CancellationToken ct)
    {
        if (_failure is not null)
        {
            throw new IOException($"Canal LSP fechado: {_failure.Message}", _failure);
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params }, ct);
            EnsureReader();
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Envia uma notificação (sem id, sem resposta).</summary>
    public async Task NotifyAsync(string method, object? @params, CancellationToken ct)
    {
        if (_failure is not null)
        {
            throw new IOException($"Canal LSP fechado: {_failure.Message}", _failure);
        }
        await WriteAsync(new { jsonrpc = "2.0", method, @params }, ct);
        EnsureReader();
    }

    /// <summary>Falha o peer: completa pendentes com <paramref name="error"/>.</summary>
    public void Fail(Exception error)
    {
        if (Interlocked.CompareExchange(ref _failure, error, null) is not null)
        {
            return;
        }
        foreach (var kv in _pending)
        {
            kv.Value.TrySetException(error);
        }
        _pending.Clear();
        _stop.Cancel();
    }

    private void EnsureReader() =>
        _reader ??= Task.Run(ReaderLoopAsync, _stop.Token);

    private async Task WriteAsync(object payload, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await _writeLock.WaitAsync(ct);
        try
        {
            await LspFraming.WriteAsync(_output, body, ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Fail(new IOException("Falha ao escrever no canal LSP.", ex));
            throw new IOException("Falha ao escrever no canal LSP.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReaderLoopAsync()
    {
        var ct = _stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var body = await LspFraming.ReadAsync(_input, ct);
                if (body is null)
                {
                    Fail(new IOException("Stream do servidor LSP fechou."));
                    return;
                }
                Dispatch(body);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Dispose — pendentes já foram completados por Fail().
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException
            or ObjectDisposedException)
        {
            Fail(new IOException("Canal LSP caiu durante a leitura.", ex));
        }
    }

    private void Dispatch(byte[] body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "LSP: mensagem não-JSON descartada");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var hasId = root.TryGetProperty("id", out var idEl);
            var hasMethod = root.TryGetProperty("method", out var methodEl);

            if (hasId && !hasMethod)
            {
                // Resposta (result | error) — correlaciona pelo id.
                if (idEl.ValueKind is JsonValueKind.Number
                    && idEl.TryGetInt64(out var id)
                    && _pending.TryGetValue(id, out var tcs))
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
                        var message = error.TryGetProperty("message", out var m)
                            ? m.GetString() ?? "erro" : "erro";
                        tcs.TrySetException(new LspRequestException(
                            $"id={id}", code, message));
                    }
                    else if (root.TryGetProperty("result", out var result))
                    {
                        tcs.TrySetResult(result.Clone());
                    }
                    else
                    {
                        tcs.TrySetResult(JsonDocument.Parse("null").RootElement.Clone());
                    }
                }
                return;
            }

            if (hasMethod && hasId)
            {
                // Request do servidor → resposta padrão (nunca deixar pendurado).
                var idJson = idEl.GetRawText();
                var method = methodEl.GetString() ?? string.Empty;
                _ = RespondToServerRequestAsync(method, idJson, root);
                return;
            }

            if (hasMethod)
            {
                var method = methodEl.GetString() ?? string.Empty;
                var @params = root.TryGetProperty("params", out var p)
                    ? p.Clone()
                    : JsonDocument.Parse("null").RootElement.Clone();
                try
                {
                    Notification?.Invoke(method, @params);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException
                    or ArgumentException or NotSupportedException)
                {
                    _logger?.LogWarning(ex, "LSP: handler de notificação {Method} falhou", method);
                }
            }
        }
    }

    /// <summary>
    /// Resposta padrão a requests server→client: <c>workspace/configuration</c>
    /// recebe um array de nulls do tamanho de <c>items</c>; demais recebem
    /// <c>result: null</c> (registerCapability, workDoneProgress, showMessage...).
    /// </summary>
    private async Task RespondToServerRequestAsync(string method, string idJson, JsonElement root)
    {
        try
        {
            var resultJson = "null";
            if (method == "workspace/configuration"
                && root.TryGetProperty("params", out var p)
                && p.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array)
            {
                resultJson = "[" + string.Join(",", Enumerable.Repeat("null", items.GetArrayLength())) + "]";
            }

            var payload = $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{resultJson}}}";
            await _writeLock.WaitAsync(_stop.Token);
            try
            {
                await LspFraming.WriteAsync(_output, Encoding.UTF8.GetBytes(payload), CancellationToken.None);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger?.LogDebug(ex, "LSP: resposta a '{Method}' não enviada (canal fechado)", method);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Fail(new IOException("Peer LSP descartado."));
        if (_reader is not null)
        {
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException
                or OperationCanceledException or ObjectDisposedException)
            {
                /* reader preso em stream zumbi — ignora */
            }
        }
        _writeLock.Dispose();
        _stop.Dispose();
    }
}
