using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace OpenWebUI.Infrastructure.ChatTools.Browser;

/// <summary>
/// Sessões de browser interativo por chat (SPEC-20261017-browser-visual-reasoning
/// RF-001/RF-002): um <see cref="IPlaywright"/>+<see cref="IBrowser"/> singleton de
/// processo e um <see cref="IBrowserContext"/> isolado por conversa — cookies e
/// storage nunca vazam entre chats. O Chromium é instalado sob demanda via
/// <c>Microsoft.Playwright.Program install chromium</c> quando o lançamento falha
/// com "Executable doesn't exist" (1 retry; flag <c>BrowserTools:AutoInstall</c>,
/// default true). Budgets por run limitam ações
/// (<c>BrowserTools:MaxActionsPerRun</c>, default 50) e timeouts
/// (<c>BrowserTools:ActionTimeoutSeconds</c>, default 30).
/// </summary>
public sealed class BrowserSessionService(
    IConfiguration configuration,
    ILogger<BrowserSessionService> logger) : IBrowserSessions, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();
    private readonly ConcurrentDictionary<string, int> _actionBudgets = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _disposed;

    /// <summary>Timeout por ação em ms (default 30s).</summary>
    public int ActionTimeoutMs => Math.Max(1,
        int.TryParse(configuration["BrowserTools:ActionTimeoutSeconds"], out var s) ? s : 30) * 1000;

    /// <summary>Máximo de ações (navigate/act) por run (default 50).</summary>
    public int MaxActionsPerRun =>
        int.TryParse(configuration["BrowserTools:MaxActionsPerRun"], out var m) && m > 0 ? m : 50;

    /// <summary>Viewport padrão das páginas (1280×800).</summary>
    public ViewportSize Viewport => new()
    {
        Width = int.TryParse(configuration["BrowserTools:ViewportWidth"], out var w) && w > 0 ? w : 1280,
        Height = int.TryParse(configuration["BrowserTools:ViewportHeight"], out var h) && h > 0 ? h : 800,
    };

    /// <summary>Auto-install do Chromium ligado por padrão.</summary>
    public bool AutoInstall =>
        !string.Equals(configuration["BrowserTools:AutoInstall"], "false",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>OCR habilitado (<c>BrowserTools:Ocr</c>, default false).</summary>
    public bool OcrEnabled =>
        string.Equals(configuration["BrowserTools:Ocr"], "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Consome uma ação do budget da run. Retorna false quando esgotado —
    /// a tool devolve mensagem amigável em vez de estourar.
    /// </summary>
    public bool TryConsumeAction(string runKey)
    {
        var used = _actionBudgets.AddOrUpdate(runKey, 1, (_, n) => n + 1);
        return used <= MaxActionsPerRun;
    }

    /// <summary>
    /// Retorna a página viva do chat (cria contexto isolado sob demanda).
    /// Contexto morto/crashado é descartado e recriado — o chat nunca fica
    /// preso num estado quebrado.
    /// </summary>
    public async Task<IPage> GetPageAsync(string chatKey, CancellationToken ct)
    {
        if (_sessions.TryGetValue(chatKey, out var existing)
            && existing.Page is { IsClosed: false }
            && existing.Context.Browser?.IsConnected == true)
        {
            return existing.Page;
        }

        var browser = await EnsureBrowserAsync(ct);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = Viewport,
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(ActionTimeoutMs);
        page.SetDefaultNavigationTimeout(ActionTimeoutMs * 2f);
        if (existing is not null)
        {
            await DisposeQuietlyAsync(existing.Context);
        }
        _sessions[chatKey] = new ChatSession(page, context);
        return page;
    }

    /// <summary>Fecha e descarta o contexto do chat (browser_close / fim de run).</summary>
    public async Task CloseChatAsync(string chatKey)
    {
        if (_sessions.TryRemove(chatKey, out var session))
        {
            await DisposeQuietlyAsync(session.Context);
        }
    }

    /// <summary>Descarta o contexto quebrado do chat — a próxima chamada recria.</summary>
    public Task ResetChatAsync(string chatKey) => CloseChatAsync(chatKey);

    /// <summary>
    /// True quando já há um Chromium resolvível — bundle do Playwright no
    /// cache ou browser no PATH/config (usado por <c>playwrightReady</c>).
    /// </summary>
    public bool IsBrowserReady()
    {
        if (_browser is { IsConnected: true })
        {
            return true;
        }
        var root = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "ms-playwright");
        try
        {
            return Directory.Exists(root)
                && Directory.GetDirectories(root)
                    .Any(d => Path.GetFileName(d).StartsWith("chromium", StringComparison.Ordinal));
        }
        catch (IOException)
        {
            return false;
        }
    }

    // Launch/install glue — depende do Chromium do host, não executável em CI
    // (coberto pelo smoke test quando o bundle existe).
    [ExcludeFromCodeCoverage]
    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_browser is { IsConnected: true })
        {
            return _browser;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_browser is { IsConnected: true })
            {
                return _browser;
            }

            _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync();
            try
            {
                _browser = await LaunchAsync();
            }
            catch (PlaywrightException ex) when (IsMissingBrowser(ex))
            {
                if (!AutoInstall)
                {
                    throw new InvalidOperationException(
                        "Chromium do Playwright não instalado e BrowserTools:AutoInstall está off — "
                        + "rode `playwright install chromium` no host/container.", ex);
                }
                logger.LogInformation("browser session: Chromium ausente — instalando via Playwright CLI");
                var code = await Task.Run(
                    () => Microsoft.Playwright.Program.Main(["install", "chromium"]), ct);
                if (code != 0)
                {
                    throw new InvalidOperationException(
                        "Instalação do Chromium falhou — rode `playwright install --with-deps chromium` no host.");
                }
                _browser = await LaunchAsync();
            }
            logger.LogInformation("browser session: Chromium lançado (headless)");
            return _browser;
        }
        finally
        {
            _gate.Release();
        }
    }

    [ExcludeFromCodeCoverage]
    private Task<IBrowser> LaunchAsync() => _playwright!.Chromium.LaunchAsync(
        new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = ["--no-sandbox", "--disable-dev-shm-usage", "--disable-gpu"],
        });

    [ExcludeFromCodeCoverage]
    private static bool IsMissingBrowser(PlaywrightException ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);

    [ExcludeFromCodeCoverage]
    private async Task DisposeQuietlyAsync(IBrowserContext? context)
    {
        if (context is null)
        {
            return;
        }
        try { await context.CloseAsync(); }
        catch (PlaywrightException) { /* contexto já morto */ }
        catch (InvalidOperationException) { /* contexto já morto */ }
    }

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var session in _sessions.Values)
        {
            await DisposeQuietlyAsync(session.Context);
        }
        _sessions.Clear();
        if (_browser is not null)
        {
            try { await _browser.CloseAsync(); }
            catch (PlaywrightException) { /* já fechado */ }
        }
        _playwright?.Dispose();
    }

    private sealed record ChatSession(IPage Page, IBrowserContext Context);
}
