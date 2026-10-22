using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.Terminal;

/// <summary>
/// Registro de <see cref="IPtySession"/>s por usuário para o terminal do
/// browser: cria/lista/mata sessões, anexa assinantes de IO (o WebSocket),
/// mantém scrollback limitado para replay na reconexão e reaps sessões
/// ociosas. Transport-agnóstico — os endpoints REST/WS consomem
/// <see cref="Attach"/>.
/// </summary>
public sealed class TerminalSessionManager : IAsyncDisposable
{
    /// <summary>Máximo de sessões simultâneas por usuário.</summary>
    public const int MaxSessionsPerUser = 4;

    /// <summary>Sessão sem input e sem assinantes é encerrada após este tempo.</summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Teto do scrollback por sessão (replay na reconexão).</summary>
    internal const int ScrollbackChars = 64_000;

    /// <summary>Resumo de uma sessão para listagem.</summary>
    /// <param name="Id">Identificador da sessão.</param>
    /// <param name="IsRunning">Se o processo ainda está vivo.</param>
    /// <param name="CreatedAtUtc">Criação.</param>
    /// <param name="LastActivityUtc">Último input escrito.</param>
    /// <param name="ExitCode">Código de saída quando encerrada.</param>
    /// <param name="ChatId">Chat dono da sessão (SPEC-20261022 — binding).</param>
    /// <param name="RunId">Run dona da sessão (opcional).</param>
    /// <param name="Workdir">Diretório inicial do PTY.</param>
    public sealed record SessionInfo(
        string Id, bool IsRunning, DateTimeOffset CreatedAtUtc,
        DateTimeOffset LastActivityUtc, int? ExitCode,
        string? ChatId = null, string? RunId = null,
        string? Workdir = null);

    internal sealed class Entry
    {
        public required IPtySession Session { get; init; }
        public required string UserId { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public int? ExitCode { get; set; }
        /// <summary>Chat dono (SPEC-20261022) — delete do chat mata a sessão.</summary>
        public string? ChatId { get; init; }
        /// <summary>Run dona — sessão bindada ao worktree isolado dela.</summary>
        public string? RunId { get; init; }
        /// <summary>cwd resolvido no Create.</summary>
        public string? Workdir { get; init; }
        /// <summary>Kill deliberado (botão/delete de chat) não gera
        /// item de atenção — só saída espontânea do processo.</summary>
        public bool SkipAttention { get; set; }
        public StringBuilder Scrollback { get; } = new();
        public List<Subscriber> Subscribers { get; } = [];
    }

    internal sealed class Subscriber
    {
        public required Func<string, Task> OnOutput { get; init; }
        public required Func<int, Task> OnExit { get; init; }
    }

    /// <summary>Handle de uma sessão anexada — desanexa ao ser descartado.</summary>
    public sealed class SessionHandle : IDisposable
    {
        private readonly Entry _entry;
        private readonly Subscriber _subscriber;
        private bool _disposed;

        internal SessionHandle(
            Entry entry, Subscriber subscriber,
            string scrollback)
        {
            _entry = entry;
            _subscriber = subscriber;
            Scrollback = scrollback;
        }

        /// <summary>Saída recente da sessão (para replay na (re)conexão).</summary>
        public string Scrollback { get; }

        /// <summary>Se o processo ainda está vivo.</summary>
        public bool IsRunning => _entry.Session.IsRunning;

        /// <summary>Código de saída quando já encerrou.</summary>
        public int? ExitCode => _entry.ExitCode;

        /// <summary>Escreve input no shell.</summary>
        public Task WriteAsync(string data) => _entry.Session.WriteAsync(data);

        /// <summary>Redimensiona o PTY.</summary>
        public Task ResizeAsync(int cols, int rows) => _entry.Session.ResizeAsync(cols, rows);

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_entry.Subscribers)
            {
                _entry.Subscribers.Remove(_subscriber);
            }
        }
    }

    private readonly ConcurrentDictionary<string, Entry> _sessions = new();
    private readonly IHostEnvironment _env;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TerminalSessionManager> _logger;
    private readonly IServiceScopeFactory? _scopes;
    private readonly Func<string, int, int, IPtySession>? _sessionFactory;
    private readonly TimeSpan _idleTimeout;
    private readonly CancellationTokenSource _sweepCts = new();
    private readonly Task _sweepTask;

    /// <summary>Cria o manager com o factory real de PTY.</summary>
    public TerminalSessionManager(
        IHostEnvironment env, ILoggerFactory loggerFactory,
        ILogger<TerminalSessionManager> logger,
        IServiceScopeFactory scopes)
        : this(env, loggerFactory, logger, null, IdleTimeout, scopes)
    {
    }

    /// <summary>Ctor de teste: factory fake + idle-timeout customizado.</summary>
    internal TerminalSessionManager(
        IHostEnvironment env, ILoggerFactory loggerFactory,
        ILogger<TerminalSessionManager> logger,
        Func<string, int, int, IPtySession>? sessionFactory, TimeSpan idleTimeout,
        IServiceScopeFactory? scopes = null)
    {
        _env = env;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _scopes = scopes;
        _sessionFactory = sessionFactory;
        _idleTimeout = idleTimeout;
        _sweepTask = Task.Run(SweepLoopAsync, _sweepCts.Token);
    }

    /// <summary>
    /// Cria e inicia uma sessão PTY — por padrão no workspace do usuário
    /// (<c>data/workspaces/{userId}</c>), ou em
    /// <paramref name="workdir"/> quando a sessão é bindada a um chat/run
    /// (SPEC-20261022: o endpoint resolve via workspace binding); lança
    /// quando o limite por usuário é atingido. <paramref name="env"/>
    /// injeta as variáveis resolvidas de secrets do usuário
    /// (SPEC-20261017-agent-secrets-env).
    /// </summary>
    public string Create(string userId, int cols = 120, int rows = 30,
        IReadOnlyDictionary<string, string>? env = null,
        string? workdir = null, string? chatId = null, string? runId = null)
    {
        var count = _sessions.Values.Count(e => e.UserId == userId);
        if (count >= MaxSessionsPerUser)
        {
            throw new InvalidOperationException(
                $"Limite de {MaxSessionsPerUser} sessões de terminal por usuário atingido.");
        }

        var workspace = workdir ?? Path.Join(
            DataPaths.Root(_env.ContentRootPath), "workspaces", userId);
        Directory.CreateDirectory(workspace);

        var session = (_sessionFactory
            ?? ((dir, c, r) => new PtySession(
                dir, _loggerFactory.CreateLogger<PtySession>(), c, r,
                env: env)))(workspace, cols, rows);

        var entry = new Entry
        {
            Session = session,
            UserId = userId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ChatId = chatId,
            RunId = runId,
            Workdir = workspace,
        };
        var id = $"t{Guid.NewGuid():N}"[..13];
        session.OutputReceived += chunk => OnOutput(entry, chunk);
        session.Exited += code => OnExit(id, entry, code);
        _sessions[id] = entry;
        try
        {
            session.Start();
        }
        catch
        {
            _sessions.TryRemove(id, out _);
            throw;
        }

        return id;
    }

    /// <summary>Lista as sessões do usuário — filtra por chat dono
    /// quando <paramref name="chatId"/> é informado.</summary>
    public List<SessionInfo> List(string userId, string? chatId = null) =>
        _sessions
            .Where(kv => kv.Value.UserId == userId
                && (chatId is null || kv.Value.ChatId == chatId))
            .Select(kv => new SessionInfo(
                kv.Key, kv.Value.Session.IsRunning, kv.Value.CreatedAtUtc,
                kv.Value.Session.LastActivityUtc, kv.Value.ExitCode,
                kv.Value.ChatId, kv.Value.RunId, kv.Value.Workdir))
            .OrderBy(s => s.CreatedAtUtc)
            .ToList();

    /// <summary>
    /// Mata todas as sessões bindadas a um chat (SPEC-20261022): chamado no
    /// delete de conversa antes da remoção do worktree — PTY órfão em cwd
    /// deletado ficaria preso a inode morto.
    /// </summary>
    /// <returns>Quantas sessões foram encerradas.</returns>
    public async Task<int> KillForChatAsync(string userId, string chatId)
    {
        var ids = _sessions
            .Where(kv => kv.Value.UserId == userId
                && kv.Value.ChatId == chatId)
            .Select(kv => kv.Key)
            .ToList();
        var killed = 0;
        foreach (var id in ids)
        {
            if (await KillAsync(userId, id).ConfigureAwait(false))
            {
                killed++;
            }
        }

        return killed;
    }

    /// <summary>
    /// Anexa assinantes de IO numa sessão do usuário — retorna null quando a
    /// sessão não existe ou pertence a outro usuário. O
    /// <see cref="SessionHandle.Scrollback"/> carrega a saída recente para
    /// replay imediato.
    /// </summary>
    public SessionHandle? Attach(
        string userId, string id,
        Func<string, Task> onOutput, Func<int, Task> onExit)
    {
        if (!_sessions.TryGetValue(id, out var entry) || entry.UserId != userId)
        {
            return null;
        }

        var subscriber = new Subscriber { OnOutput = onOutput, OnExit = onExit };
        lock (entry.Subscribers)
        {
            entry.Subscribers.Add(subscriber);
        }

        string snapshot;
        lock (entry.Scrollback)
        {
            snapshot = entry.Scrollback.ToString();
        }

        return new SessionHandle(entry, subscriber, snapshot);
    }

    /// <summary>Encerra e remove uma sessão do usuário (kill).</summary>
    public async Task<bool> KillAsync(string userId, string id)
    {
        if (!_sessions.TryGetValue(id, out var entry)
            || entry.UserId != userId
            || !_sessions.TryRemove(id, out entry))
        {
            return false;
        }

        entry.SkipAttention = true;
        try
        {
            await entry.Session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao descartar sessão {Id}.", id);
        }

        return true;
    }

    private void OnOutput(Entry entry, string chunk)
    {
        lock (entry.Scrollback)
        {
            entry.Scrollback.Append(chunk);
            if (entry.Scrollback.Length > ScrollbackChars)
            {
                entry.Scrollback.Remove(0, entry.Scrollback.Length - ScrollbackChars);
            }
        }

        List<Subscriber> subs;
        lock (entry.Subscribers)
        {
            subs = [.. entry.Subscribers];
        }

        foreach (var sub in subs)
        {
            _ = SafeInvokeAsync(sub.OnOutput, chunk);
        }
    }

    private void OnExit(string id, Entry entry, int code)
    {
        entry.ExitCode = code;
        List<Subscriber> subs;
        lock (entry.Subscribers)
        {
            subs = [.. entry.Subscribers];
        }

        foreach (var sub in subs)
        {
            _ = SafeInvokeAsync(sub.OnExit, code);
        }

        // RF-C4: sessão encerrada vira item de atenção — o usuário acha o
        // resultado ao voltar, mesmo sem ninguém assinando o output.
        _ = RaiseTerminalAttentionAsync(id, entry);
    }

    private async Task RaiseTerminalAttentionAsync(string id, Entry entry)
    {
        if (_scopes is null || entry.SkipAttention)
        {
            return;
        }
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await AttentionService.RaiseAsync(
                db, entry.UserId, $"term:{id}", "terminal",
                $"terminal encerrado ({entry.Workdir})", entry.ChatId,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Falha ao registrar attention de terminal {Id}.", id);
        }
    }

    private async Task SafeInvokeAsync(Func<string, Task> fn, string arg)
    {
        try
        {
            await fn(arg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Assinante de output do terminal falhou.");
        }
    }

    private async Task SafeInvokeAsync(Func<int, Task> fn, int arg)
    {
        try
        {
            await fn(arg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Assinante de exit do terminal falhou.");
        }
    }

    // Loop de varredura com timing real de processo — não determinístico em CI.
    [ExcludeFromCodeCoverage]
    private async Task SweepLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_sweepCts.Token).ConfigureAwait(false))
            {
                var cutoff = DateTimeOffset.UtcNow - _idleTimeout;
                foreach (var (id, entry) in _sessions)
                {
                    int subs;
                    lock (entry.Subscribers)
                    {
                        subs = entry.Subscribers.Count;
                    }

                    if (subs == 0 && entry.Session.LastActivityUtc < cutoff)
                    {
                        _logger.LogInformation(
                            "Sessão de terminal {Id} ociosa — encerrando.", id);
                        await KillAsync(entry.UserId, id).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown esperado.
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _sweepCts.CancelAsync().ConfigureAwait(false);
        _sweepCts.Dispose();
        try
        {
            await _sweepTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sweep do terminal não encerrou limpo.");
        }

        foreach (var (id, entry) in _sessions.ToList())
        {
            await KillAsync(entry.UserId, id).ConfigureAwait(false);
        }
    }
}
