using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Infrastructure.Lsp;

/// <summary>
/// Gerenciador dos language servers por workdir (SPEC-20261009-lsp-diagnostics):
/// um <see cref="LspClient"/> por (workdir, linguagem), cap
/// <see cref="LspOptions.MaxServersPerWorkdir"/> por workdir, mapa de servidores
/// via <c>Lsp:Servers</c>. Linguagem sem binário instalado →
/// <see cref="LspServerState.Unavailable"/> (nada quebra — tools/endpoints
/// respondem erro amigável). Shutdown garante kill-tree de todos os filhos.
/// </summary>
public sealed class LspService : IAsyncDisposable
{
    private readonly ILogger<LspService> _logger;
    private readonly ConcurrentDictionary<string, LspClient> _clients =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byWorkdir =
        new(StringComparer.Ordinal);
    private int _disposed;

    public LspService(IConfiguration configuration, ILogger<LspService> logger,
        IHostApplicationLifetime? lifetime = null)
        : this(LspOptions.Load(configuration), logger, lifetime)
    {
    }

    public LspService(LspOptions options, ILogger<LspService> logger,
        IHostApplicationLifetime? lifetime = null)
    {
        Options = options;
        _logger = logger;
        lifetime?.ApplicationStopping.Register(() => _ = DisposeAsync());
    }

    /// <summary>Opções efetivas (config <c>Lsp:*</c> ou defaults).</summary>
    public LspOptions Options { get; }

    /// <summary>Ligado por <c>Lsp:Enabled</c>/<c>LSP_ENABLED</c> (default on).</summary>
    public bool Enabled => Options.Enabled;

    private static string Key(string workdir, string serverKey) =>
        $"{Path.GetFullPath(workdir)}::{serverKey.ToLowerInvariant()}";

    /// <summary>
    /// Linguagem LSP do arquivo (extensão → servidor); null quando não mapeada.
    /// </summary>
    public static LspLanguage? LanguageFor(string? path) => LspLanguageMap.ForPath(path);

    /// <summary>
    /// Estado do servidor de <paramref name="serverKey"/> no workdir
    /// (<see cref="LspServerState.NotStarted"/> quando nunca demandado).
    /// </summary>
    public LspServerState StateOf(string workdir, string serverKey) =>
        _clients.TryGetValue(Key(workdir, serverKey), out var client)
            ? client.State
            : LspServerState.NotStarted;

    /// <summary>Motivo da indisponibilidade, quando o estado é Unavailable.</summary>
    public string? ErrorOf(string workdir, string serverKey) =>
        _clients.TryGetValue(Key(workdir, serverKey), out var client)
            ? client.Error
            : null;

    /// <summary>Quantos servidores estão registrados no workdir (cap 2).</summary>
    public int ServerCount(string workdir) =>
        _byWorkdir.TryGetValue(Path.GetFullPath(workdir), out var set) ? set.Count : 0;

    /// <summary>
    /// Devolve o client pronto do (workdir, linguagem) — spawna sob demanda.
    /// Lança <see cref="LspUnavailableException"/> para binário ausente/init
    /// falha/cap de servers atingido.
    /// </summary>
    public async Task<LspClient> GetClientAsync(
        string workdir, LspLanguage language, CancellationToken ct)
    {
        if (!Enabled)
        {
            throw new LspUnavailableException("LSP desabilitado (Lsp:Enabled=false).");
        }
        if (_disposed != 0)
        {
            throw new LspUnavailableException("LspService encerrado.");
        }
        if (!Options.Servers.TryGetValue(language.ServerKey, out var spec))
        {
            throw new LspUnavailableException(
                $"Sem servidor configurado para '{language.ServerKey}' (Lsp:Servers).");
        }

        var fullWorkdir = Path.GetFullPath(workdir);
        var key = Key(fullWorkdir, language.ServerKey);
        var client = _clients.GetOrAdd(key, _ =>
        {
            var set = _byWorkdir.GetOrAdd(fullWorkdir,
                _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            // Cap por workdir — o claim é preliminar; quem excede é removido abaixo.
            set.TryAdd(language.ServerKey, 1);
            return new LspClient(fullWorkdir, language, spec, Options, _logger);
        });

        if (ServerCount(fullWorkdir) > Options.MaxServersPerWorkdir
            && client.State == LspServerState.NotStarted)
        {
            // Rejeita o novo server sem spawnar — estourou o cap do workdir.
            _clients.TryRemove(key, out _);
            if (_byWorkdir.TryGetValue(fullWorkdir, out var set))
            {
                set.TryRemove(language.ServerKey, out _);
            }
            throw new LspUnavailableException(
                $"Cap de {Options.MaxServersPerWorkdir} servidores LSP por workdir atingido.");
        }

        await client.EnsureRunningAsync(ct);
        return client;
    }

    /// <summary>Server do arquivo ou null (sem spawnar — usado p/ status).</summary>
    public static LspLanguage? LanguageOfFile(string? path) => LspLanguageMap.ForPath(path);

    // ---------- sync (didOpen/didChange/didSave/didClose) ----------

    /// <summary>
    /// didOpen com o texto dado (ou do disco quando <paramref name="text"/> é
    /// null). No-op silencioso quando a linguagem não é coberta/indisponível.
    /// </summary>
    public async Task<LspClient?> OpenDocumentAsync(
        string workdir, string absolutePath, string? text, CancellationToken ct)
    {
        var client = await TryClientAsync(workdir, absolutePath, ct);
        if (client is null)
        {
            return null;
        }
        text ??= await File.ReadAllTextAsync(absolutePath, ct);
        await client.DidOpenAsync(absolutePath, text, ct);
        return client;
    }

    /// <summary>didChange full-sync (abre antes se necessário).</summary>
    public async Task ChangeDocumentAsync(
        string workdir, string absolutePath, string text, CancellationToken ct)
    {
        var client = await TryClientAsync(workdir, absolutePath, ct);
        if (client is null)
        {
            return;
        }
        await client.DidChangeAsync(absolutePath, text, ct);
    }

    /// <summary>didSave — garante conteúdo real (disk) antes do save.</summary>
    public async Task SaveDocumentAsync(
        string workdir, string absolutePath, CancellationToken ct)
    {
        var client = await TryClientAsync(workdir, absolutePath, ct);
        if (client is null)
        {
            return;
        }
        var text = await File.ReadAllTextAsync(absolutePath, ct);
        await client.DidChangeAsync(absolutePath, text, ct);
        await client.DidSaveAsync(absolutePath, ct);
    }

    /// <summary>didClose best-effort.</summary>
    public async Task CloseDocumentAsync(string workdir, string absolutePath, CancellationToken ct)
    {
        var language = LanguageFor(absolutePath);
        if (language is null || !_clients.TryGetValue(Key(workdir, language.ServerKey), out var client))
        {
            return;
        }
        await client.DidCloseAsync(absolutePath);
    }

    /// <summary>didChange pós-escrita das tools <c>file_*</c> (lê do disco).</summary>
    public async Task NotifyFileWrittenAsync(
        string workdir, string absolutePath, CancellationToken ct)
    {
        try
        {
            if (File.Exists(absolutePath))
            {
                var text = await File.ReadAllTextAsync(absolutePath, ct);
                await ChangeDocumentAsync(workdir, absolutePath, text, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or LspUnavailableException
            or LspRequestException or InvalidOperationException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "LSP didChange falhou para {Path}", absolutePath);
        }
    }

    /// <summary>didSave pós-PUT do editor (lê do disco).</summary>
    public async Task NotifyFileSavedAsync(
        string workdir, string absolutePath, CancellationToken ct)
    {
        try
        {
            if (File.Exists(absolutePath))
            {
                await SaveDocumentAsync(workdir, absolutePath, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or LspUnavailableException
            or LspRequestException or InvalidOperationException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "LSP didSave falhou para {Path}", absolutePath);
        }
    }

    private async Task<LspClient?> TryClientAsync(
        string workdir, string absolutePath, CancellationToken ct)
    {
        var language = LanguageFor(absolutePath);
        if (language is null)
        {
            return null;
        }
        try
        {
            return await GetClientAsync(workdir, language, ct);
        }
        catch (LspUnavailableException)
        {
            return null;
        }
    }

    // ---------- consultas (tools + endpoints) ----------

    /// <summary>Diagnostics cacheados do workdir (opcional: só de um arquivo).</summary>
    public IReadOnlyList<LspDiagnostic> Diagnostics(
        string workdir, string? absolutePath, out bool anyServerRunning)
    {
        anyServerRunning = false;
        var filterUri = absolutePath is null ? null : LspClient.UriForPath(absolutePath);
        var list = new List<LspDiagnostic>();
        var root = Path.GetFullPath(workdir);
        foreach (var client in _clients.Values)
        {
            if (!string.Equals(client.Workdir, root, StringComparison.Ordinal))
            {
                continue;
            }
            if (client.State == LspServerState.Running)
            {
                anyServerRunning = true;
            }
            foreach (var (uri, diags) in client.DiagnosticsSnapshot())
            {
                if (filterUri is not null
                    && !string.Equals(uri, filterUri, StringComparison.Ordinal))
                {
                    continue;
                }
                list.AddRange(diags);
            }
        }
        return list;
    }

    /// <summary>Request bruto para as tools (já valida linguagem/servidor).</summary>
    public async Task<JsonElement> RequestAsync(
        string workdir, string absolutePath, string method, object? @params,
        CancellationToken ct)
    {
        var language = LanguageFor(absolutePath)
            ?? throw new LspUnavailableException(
                $"Sem linguagem LSP para '{Path.GetFileName(absolutePath)}'.");
        var client = await GetClientAsync(workdir, language, ct);
        return await client.RequestAsync(method, @params, ct);
    }

    /// <summary>Request de workspace/symbol (não atrelado a um arquivo).</summary>
    public async Task<JsonElement> WorkspaceSymbolAsync(
        string workdir, string serverKey, string query, CancellationToken ct)
    {
        var language = new LspLanguage(serverKey, serverKey);
        var client = await GetClientAsync(workdir, language, ct);
        return await client.RequestAsync("workspace/symbol", new { query }, ct);
    }

    /// <summary>Encerra todos os language servers (shutdown + kill-tree).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        foreach (var client in _clients.Values)
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException
                or OperationCanceledException or ObjectDisposedException)
            {
                _logger.LogDebug(ex, "LSP dispose falhou");
            }
        }
        _clients.Clear();
        _byWorkdir.Clear();
    }
}
