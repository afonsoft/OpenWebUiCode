using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenWebUI.Infrastructure.Lsp;

/// <summary>
/// Um language server por (workdir, linguagem) via stdio JSON-RPC
/// (SPEC-20261009-lsp-diagnostics RF-001): <c>initialize(rootUri)</c> →
/// <c>initialized</c> → requests → <c>shutdown</c>+<c>exit</c>+kill-tree no
/// dispose. Crash → restart com backoff exponencial até
/// <see cref="LspOptions.MaxRestartAttempts"/>; falha de spawn/init →
/// <see cref="LspServerState.Unavailable"/>. Diagnósticos publicados pelo
/// servidor ficam cacheados por URI para as tools/endpoints.
/// </summary>
public sealed class LspClient : IAsyncDisposable
{
    private sealed record OpenDoc(string LanguageId, int Version);

    private readonly LspOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<string, OpenDoc> _openDocs =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<LspDiagnostic>> _diagnostics =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _diagWaiters =
        new(StringComparer.Ordinal);

    private Process? _process;
    private JsonRpcPeer? _peer;
    private int _restartAttempts;
    private long _startedAt;
    private int _disposed;
    private string? _initError;

    /// <summary>Janela de estabilidade: crash depois dela zera o contador de restart.</summary>
    private static readonly TimeSpan StableWindow = TimeSpan.FromSeconds(60);

    public LspClient(string workdir, LspLanguage language, LspServerSpec spec,
        LspOptions options, ILogger logger)
    {
        Workdir = Path.GetFullPath(workdir);
        Language = language;
        Spec = spec;
        _options = options;
        _logger = logger;
    }

    public string Workdir { get; }
    public LspLanguage Language { get; }
    public LspServerSpec Spec { get; }
    public LspServerState State { get; private set; } = LspServerState.NotStarted;

    /// <summary>Motivo da indisponibilidade (spawn/init/restarts esgotados).</summary>
    public string? Error => _initError;

    /// <summary>Path absoluto → URI <c>file://</c> (percent-encoded, UTF-8).</summary>
    public static string UriForPath(string absolutePath) =>
        new Uri(absolutePath).AbsoluteUri;

    /// <summary>URI <c>file://</c> → path local (decodifica %XX/unicode).</summary>
    public static string? PathForUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)
            || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeFile)
        {
            return null;
        }
        return parsed.LocalPath;
    }

    /// <summary>Snapshot dos diagnostics por URI (chave = URI file://).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LspDiagnostic>> DiagnosticsSnapshot()
    {
        var map = new Dictionary<string, IReadOnlyList<LspDiagnostic>>(StringComparer.Ordinal);
        foreach (var kv in _diagnostics)
        {
            map[kv.Key] = kv.Value;
        }
        return map;
    }

    /// <summary>
    /// Garante servidor pronto: spawna (ou respawna pós-crash respeitando o
    /// cap de restarts) e completa o handshake initialize. Estado
    /// <see cref="LspServerState.Unavailable"/> lança <see cref="LspUnavailableException"/>.
    /// </summary>
    public async Task EnsureRunningAsync(CancellationToken ct)
    {
        if (State == LspServerState.Running && _process is { HasExited: false })
        {
            return;
        }
        ThrowIfUnavailable();

        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (State == LspServerState.Running && _process is { HasExited: false })
            {
                return;
            }
            ThrowIfUnavailable();

            if (State == LspServerState.Crashed)
            {
                if (_restartAttempts >= _options.MaxRestartAttempts)
                {
                    MarkUnavailable(
                        $"Servidor LSP '{Language.ServerKey}' esgotou "
                        + $"{_options.MaxRestartAttempts} restarts.");
                    throw new LspUnavailableException(_initError!);
                }
                var delay = _options.RestartBaseDelayMs * (1 << _restartAttempts);
                await Task.Delay(delay, CancellationToken.None);
            }

            _restartAttempts += State == LspServerState.Crashed ? 1 : 0;
            await SpawnAndInitializeAsync(ct);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Request LSP com garantia de lifecycle + timeout por request.</summary>
    public async Task<JsonElement> RequestAsync(string method, object? @params, CancellationToken ct)
    {
        await EnsureRunningAsync(ct);
        try
        {
            return await _peer!.RequestAsync(
                method, @params, TimeSpan.FromSeconds(_options.RequestTimeoutSeconds), ct);
        }
        catch (IOException) when (_process is { HasExited: true } && !IsDisposed)
        {
            // Servidor morreu durante a request — marca crashed; próxima call respawna.
            MarkCrashed();
            throw;
        }
    }

    /// <summary>Notificação LSP (didOpen/didChange/didSave/didClose, exit).</summary>
    public async Task NotifyAsync(string method, object? @params, CancellationToken ct)
    {
        await EnsureRunningAsync(ct);
        try
        {
            await _peer!.NotifyAsync(method, @params, ct);
        }
        catch (IOException) when (_process is { HasExited: true } && !IsDisposed)
        {
            MarkCrashed();
            throw;
        }
    }

    /// <summary>didOpen: registra o doc e envia a notificação ao servidor.</summary>
    public async Task DidOpenAsync(string absolutePath, string text, CancellationToken ct)
    {
        var uri = UriForPath(absolutePath);
        await NotifyAsync("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri,
                languageId = Language.LanguageId,
                version = 1,
                text,
            },
        }, ct);
        _openDocs[uri] = new OpenDoc(Language.LanguageId, 1);
    }

    /// <summary>didChange full-sync (v1): envia o texto inteiro do documento.</summary>
    public async Task DidChangeAsync(string absolutePath, string text, CancellationToken ct)
    {
        var uri = UriForPath(absolutePath);
        if (!_openDocs.TryGetValue(uri, out var doc))
        {
            await DidOpenAsync(absolutePath, text, ct);
            return;
        }

        var version = doc.Version + 1;
        await NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri, version },
            contentChanges = new[] { new { text } },
        }, ct);
        _openDocs[uri] = doc with { Version = version };
    }

    /// <summary>didSave (a escrita já aconteceu em disco).</summary>
    public async Task DidSaveAsync(string absolutePath, CancellationToken ct)
    {
        var uri = UriForPath(absolutePath);
        await NotifyAsync("textDocument/didSave", new
        {
            textDocument = new { uri },
        }, ct);
    }

    /// <summary>didClose (best-effort — falhas não propagam).</summary>
    public async Task DidCloseAsync(string absolutePath)
    {
        var uri = UriForPath(absolutePath);
        _openDocs.TryRemove(uri, out _);
        _diagnostics.TryRemove(uri, out _);
        if (State != LspServerState.Running || _peer is null)
        {
            return;
        }
        try
        {
            await _peer.NotifyAsync("textDocument/didClose", new
            {
                textDocument = new { uri },
            }, CancellationToken.None);
        }
        catch (IOException)
        {
            // Fechar doc em servidor morto é no-op — já foi crashed.
        }
    }

    private bool IsDisposed => _disposed != 0;

    private void MarkCrashed()
    {
        // Crash depois de uma janela estável não conta contra o cap —
        // senão instabilidade esporádica mataria o servidor para sempre.
        if (Environment.TickCount64 - _startedAt >= StableWindow.TotalMilliseconds)
        {
            _restartAttempts = 0;
        }
        if (State == LspServerState.Running)
        {
            State = LspServerState.Crashed;
        }
    }

    private void ThrowIfUnavailable()
    {
        if (State == LspServerState.Unavailable)
        {
            throw new LspUnavailableException(_initError
                ?? $"Servidor LSP '{Language.ServerKey}' indisponível.");
        }
    }

    private void MarkUnavailable(string reason)
    {
        _initError = reason;
        State = LspServerState.Unavailable;
        _logger.LogWarning("LSP {ServerKey}@{Workdir}: {Reason}", Language.ServerKey, Workdir, reason);
    }

    private async Task SpawnAndInitializeAsync(CancellationToken ct)
    {
        // Limpa restos da encarnação anterior (crash).
        if (_peer is not null)
        {
            await _peer.DisposeAsync();
            _peer = null;
        }
        KillQuietly(_process);
        _process = null;

        var psi = new ProcessStartInfo
        {
            FileName = Spec.Command,
            WorkingDirectory = Workdir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in Spec.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or FileNotFoundException)
        {
            MarkUnavailable(
                $"Binário LSP '{Spec.Command}' não encontrado — instale o servidor "
                + $"para '{Language.ServerKey}' ou ajuste Lsp:Servers. ({ex.Message})");
            throw new LspUnavailableException(_initError!);
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            if (!IsDisposed)
            {
                MarkCrashed();
                _peer?.Fail(new IOException($"Servidor LSP '{Spec.Command}' morreu."));
            }
        };
        process.ErrorDataReceived += (_, _) => { }; // drena stderr p/ não travar o filho
        process.BeginErrorReadLine();

        var peer = new JsonRpcPeer(process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream, _logger);
        peer.Notification += OnNotification;
        _process = process;
        _peer = peer;

        var rootUri = UriForPath(Workdir);
        try
        {
            var timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);
            await peer.RequestAsync("initialize", new
            {
                processId = Environment.ProcessId,
                rootUri,
                capabilities = new
                {
                    textDocument = new
                    {
                        synchronization = new { didSave = true },
                        publishDiagnostics = new { },
                        definition = new { },
                        references = new { },
                        hover = new { contentFormat = new[] { "markdown", "plaintext" } },
                        documentSymbol = new
                        {
                            hierarchicalDocumentSymbolSupport = true,
                            symbolKind = new { valueSet = Array.Empty<int>() },
                        },
                    },
                    workspace = new
                    {
                        workspaceFolders = true,
                        symbol = new { },
                    },
                },
                workspaceFolders = new[] { new { uri = rootUri, name = Path.GetFileName(Workdir) } },
                initializationOptions = new { },
            }, timeout, ct);
            await peer.NotifyAsync("initialized", new { }, ct);
        }
        catch (Exception ex) when (ex is not LspUnavailableException)
        {
            MarkUnavailable(
                $"Handshake LSP '{Spec.Command}' falhou: {ex.Message}");
            KillQuietly(process);
            _process = null;
            _peer = null;
            throw new LspUnavailableException(_initError!);
        }

        State = LspServerState.Running;
        _startedAt = Environment.TickCount64;

        // Replay dos docs abertos (restart pós-crash): servidor vê conteúdo real.
        foreach (var (uri, doc) in _openDocs)
        {
            var path = PathForUri(uri);
            if (path is null || !File.Exists(path))
            {
                continue;
            }
            try
            {
                var text = await File.ReadAllTextAsync(path, ct);
                await peer.NotifyAsync("textDocument/didOpen", new
                {
                    textDocument = new
                    {
                        uri, languageId = doc.LanguageId, version = doc.Version, text,
                    },
                }, ct);
            }
            catch (IOException) { /* arquivo sumiu entre a checagem e a leitura */ }
        }
    }

    private void OnNotification(string method, JsonElement @params)
    {
        if (method != "textDocument/publishDiagnostics")
        {
            return;
        }

        var uri = @params.TryGetProperty("uri", out var u) ? u.GetString() : null;
        if (uri is null)
        {
            return;
        }

        var list = new List<LspDiagnostic>();
        if (@params.TryGetProperty("diagnostics", out var diags)
            && diags.ValueKind == JsonValueKind.Array)
        {
            var path = PathForUri(uri) ?? uri;
            foreach (var d in diags.EnumerateArray())
            {
                list.Add(new LspDiagnostic(
                    path,
                    ReadPos(d, "start", "line"),
                    ReadPos(d, "start", "character"),
                    ReadPos(d, "end", "line"),
                    ReadPos(d, "end", "character"),
                    d.TryGetProperty("severity", out var s) && s.TryGetInt32(out var sev) ? sev : 3,
                    d.TryGetProperty("code", out var c) ? c.ToString() : null,
                    d.TryGetProperty("source", out var so) ? so.GetString() : null,
                    d.TryGetProperty("message", out var m) ? m.GetString() ?? "" : ""));
            }
        }
        _diagnostics[uri] = list;
        // Acorda quem espera publishDiagnostics desse URI (lsp_diagnostics/tool).
        if (_diagWaiters.TryRemove(uri, out var waiter))
        {
            waiter.TrySetResult();
        }
    }

    /// <summary>
    /// Espera o próximo <c>publishDiagnostics</c> do URI (ou o cache atual
    /// quando já há publicação). Devolve false no timeout — servidor pode
    /// simplesmente não publicar (arquivo limpo → lista vazia ainda chega,
    /// mas servidor lento não deve travar a tool além do cap).
    /// </summary>
    public async Task<bool> AwaitDiagnosticsAsync(
        string absolutePath, TimeSpan timeout, CancellationToken ct)
    {
        var uri = UriForPath(absolutePath);
        if (_diagnostics.ContainsKey(uri))
        {
            return true;
        }

        var waiter = _diagWaiters.GetOrAdd(uri, _ =>
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await waiter.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return _diagnostics.ContainsKey(uri);
        }
    }

    private static int ReadPos(JsonElement diag, string which, string field) =>
        diag.TryGetProperty("range", out var r)
        && r.TryGetProperty(which, out var pos)
        && pos.TryGetProperty(field, out var v)
        && v.TryGetInt32(out var n) ? n : 0;

    private static void KillQuietly(Process? process)
    {
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Processo já morreu — best-effort.
        }
        process.Dispose();
    }

    /// <summary>shutdown+exit com timeout curto; kill-tree garantido.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var process = _process;
        var peer = _peer;
        if (peer is not null && process is { HasExited: false })
        {
            try
            {
                await peer.RequestAsync("shutdown", null,
                    TimeSpan.FromSeconds(_options.ShutdownTimeoutSeconds), CancellationToken.None);
                await peer.NotifyAsync("exit", null, CancellationToken.None);
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is IOException or TimeoutException
                or InvalidOperationException or LspRequestException)
            {
                // Servidor travou no shutdown — kill abaixo garante a morte.
            }
        }

        KillQuietly(process);
        if (peer is not null)
        {
            await peer.DisposeAsync();
        }
        _lifecycleLock.Dispose();
        State = LspServerState.NotStarted;
    }
}
