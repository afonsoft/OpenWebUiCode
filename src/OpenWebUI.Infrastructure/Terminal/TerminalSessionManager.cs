using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;

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
    public sealed record SessionInfo(
        string Id, bool IsRunning, DateTimeOffset CreatedAtUtc,
        DateTimeOffset LastActivityUtc, int? ExitCode);

    internal sealed class Entry
    {
        public required IPtySession Session { get; init; }
        public required string UserId { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public int? ExitCode { get; set; }
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
    private readonly Func<string, int, int, IPtySession>? _sessionFactory;
    private readonly TimeSpan _idleTimeout;
    private readonly CancellationTokenSource _sweepCts = new();
    private readonly Task _sweepTask;

    /// <summary>Cria o manager com o factory real de PTY.</summary>
    public TerminalSessionManager(
        IHostEnvironment env, ILoggerFactory loggerFactory,
        ILogger<TerminalSessionManager> logger)
        : this(env, loggerFactory, logger, null, IdleTimeout)
    {
    }

    /// <summary>Ctor de teste: factory fake + idle-timeout customizado.</summary>
    internal TerminalSessionManager(
        IHostEnvironment env, ILoggerFactory loggerFactory,
        ILogger<TerminalSessionManager> logger,
        Func<string, int, int, IPtySession>? sessionFactory, TimeSpan idleTimeout)
    {
        _env = env;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _sessionFactory = sessionFactory;
        _idleTimeout = idleTimeout;
        _sweepTask = Task.Run(SweepLoopAsync, _sweepCts.Token);
    }

    /// <summary>
    /// Cria e inicia uma sessão PTY no workspace do usuário
    /// (<c>data/workspaces/{userId}</c>); lança quando o limite por usuário é
    /// atingido.
    /// </summary>
    public string Create(string userId, int cols = 120, int rows = 30)
    {
        var count = _sessions.Values.Count(e => e.UserId == userId);
        if (count >= MaxSessionsPerUser)
        {
            throw new InvalidOperationException(
                $"Limite de {MaxSessionsPerUser} sessões de terminal por usuário atingido.");
        }

        var workspace = Path.Join(
            DataPaths.Root(_env.ContentRootPath), "workspaces", userId);
        Directory.CreateDirectory(workspace);

        var session = (_sessionFactory
            ?? ((dir, c, r) => new PtySession(
                dir, _loggerFactory.CreateLogger<PtySession>(), c, r)))(workspace, cols, rows);

        var entry = new Entry
        {
            Session = session,
            UserId = userId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        session.OutputReceived += chunk => OnOutput(entry, chunk);
        session.Exited += code => OnExit(entry, code);

        var id = $"t{Guid.NewGuid():N}"[..13];
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

    /// <summary>Lista as sessões do usuário.</summary>
    public List<SessionInfo> List(string userId) =>
        _sessions
            .Where(kv => kv.Value.UserId == userId)
            .Select(kv => new SessionInfo(
                kv.Key, kv.Value.Session.IsRunning, kv.Value.CreatedAtUtc,
                kv.Value.Session.LastActivityUtc, kv.Value.ExitCode))
            .OrderBy(s => s.CreatedAtUtc)
            .ToList();

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

    private void OnExit(Entry entry, int code)
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
