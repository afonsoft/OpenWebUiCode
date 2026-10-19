using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenWebUI.Infrastructure.Terminal;

/// <summary>
/// Sessão de bash interativo dentro de um PTY real via <c>script -qfc</c> —
/// mesmo truque do agent-harness: o <c>script</c> aloca um pseudo-terminal e
/// repassa IO pelos pipes padrão, então TUIs e prompts interativos funcionam.
/// Output vai para <see cref="OutputReceived"/>; input é escrito verbatim no
/// stdin do shell. Sem pacotes externos.
/// </summary>
public sealed class PtySession : IPtySession
{
    private readonly string _workingDirectory;
    private readonly ILogger _logger;
    private readonly Func<string, string?> _locator;
    private readonly int _cols;
    private readonly int _rows;
    private readonly IReadOnlyList<string>? _command;
    private readonly IReadOnlyDictionary<string, string>? _env;

    private Process? _process;
    private Task? _pumpTask;
    private CancellationTokenSource? _pumpCts;
    private bool _disposed;
    private int _lastCols;
    private int _lastRows;
    private string? _ptySlavePath;
    private bool _ptyPathResolved;

    /// <summary>
    /// Cria a sessão. <paramref name="command"/> é um argv arbitrário rodado
    /// dentro do PTY com cada elemento single-quoted (nunca interpolado) —
    /// nulo executa <c>bash -l</c>.
    /// </summary>
    public PtySession(string workingDirectory, ILogger logger, int cols = 120,
        int rows = 30, Func<string, string?>? executableLocator = null,
        IReadOnlyList<string>? command = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        _workingDirectory = workingDirectory;
        _logger = logger;
        _cols = cols;
        _rows = rows;
        _locator = executableLocator ?? FindExecutable;
        _command = command;
        _env = env;
        _lastCols = cols;
        _lastRows = rows;
        LastActivityUtc = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc/>
    public event Action<string>? OutputReceived;

    /// <inheritdoc/>
    public event Action<int>? Exited;

    /// <inheritdoc/>
    public DateTimeOffset LastActivityUtc { get; private set; }

    /// <inheritdoc/>
    public bool IsRunning
    {
        get
        {
            if (_disposed || _process is null)
            {
                return false;
            }

            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Sobe <c>script -qfc "stty …; exec bash -l" /dev/null</c>.</summary>
    public void Start()
    {
        if (_process is not null)
        {
            return;
        }

        var script = _locator("script")
            ?? throw new InvalidOperationException(
                "Binário 'script' não encontrado — terminal indisponível.");

        var startInfo = new ProcessStartInfo(script)
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"stty cols {_cols} rows {_rows} 2>/dev/null; exec {InnerCommand()}");
        startInfo.ArgumentList.Add("/dev/null");
        startInfo.Environment["TERM"] = "xterm-256color";
        startInfo.Environment["COLORTERM"] = "truecolor";
        // bash -l precisa de HOME para achar o rc do usuário (paridade
        // agent-harness); serviço bare-metal pode vir sem ele.
        if (!startInfo.Environment.ContainsKey("HOME"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                startInfo.Environment["HOME"] = home;
            }
        }

        if (_command is not { Count: > 0 })
        {
            ApplyDefaultShellColors(startInfo);
        }

        if (_env is not null)
        {
            // Secrets do usuário ∪ global ∪ managed GitHub (SPEC-20261017-agent-secrets-env).
            foreach (var (key, value) in _env)
            {
                startInfo.Environment[key] = value;
            }
        }

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Falha ao iniciar o processo do terminal.");
        _pumpCts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token), _pumpCts.Token);
    }

    /// <summary>ls --color: paleta escura quando o ambiente não traz uma.</summary>
    private const string DefaultLsColors =
        "di=01;34:ln=01;36:so=01;35:pi=33:ex=01;32:bd=01;33:cd=01;33:" +
        "su=01;37;41:sg=01;30;43:tw=01;30;42:ow=01;34;42:st=01;37;44:" +
        "*.tar=01;31:*.tgz=01;31:*.zip=01;31:*.gz=01;31:*.bz2=01;31:*.xz=01;31:" +
        "*.jpg=01;35:*.jpeg=01;35:*.png=01;35:*.gif=01;35:*.svg=01;35:" +
        "*.mp4=01;35:*.log=00;33:*.md=01;36:*.json=00;36";

    /// <summary>
    /// Shim avaliado antes de cada prompt: roda uma vez (guard
    /// <c>__owui_done</c>), define aliases de cor e troca a PS1 somente
    /// quando ela ainda é o default do bash (<c>\s-\v\$ </c>) — um rc do
    /// usuário que configure PS1/PROMPT_COMMAND vence sem disputa.
    /// </summary>
    private const string DefaultShellColorShim =
        """[ -n "${__owui_done:-}" ] || { __owui_done=1; alias ls='ls --color=auto' 2>/dev/null; alias grep='grep --color=auto' 2>/dev/null; alias egrep='egrep --color=auto' 2>/dev/null; alias fgrep='fgrep --color=auto' 2>/dev/null; alias ll='ls -alF --color=auto' 2>/dev/null; alias la='ls -A --color=auto' 2>/dev/null; case "$PS1" in '\s-\v\$ ') PS1='\[\e[1;32m\]\u@\h\[\e[0m\]:\[\e[1;34m\]\w\[\e[0m\]\$ ';; esac; }""";

    /// <summary>
    /// Defaults de cor do shell interativo default — preenche somente o
    /// que está vazio. LS_COLORS/PROMPT_COMMAND herdados, o .bashrc do
    /// usuário e o <c>_env</c> do caller sempre prevalecem; comandos
    /// custom (<c>_command</c>) não recebem shim nenhum.
    /// </summary>
    private static void ApplyDefaultShellColors(ProcessStartInfo startInfo)
    {
        if (!startInfo.Environment.ContainsKey("LS_COLORS"))
        {
            startInfo.Environment["LS_COLORS"] = DefaultLsColors;
        }

        if (!startInfo.Environment.ContainsKey("PROMPT_COMMAND"))
        {
            startInfo.Environment["PROMPT_COMMAND"] = DefaultShellColorShim;
        }
    }

    /// <summary>Comando interno do PTY — login bash ou argv single-quoted.</summary>
    private string InnerCommand() =>
        _command is { Count: > 0 }
            ? string.Join(' ', _command.Select(ShellQuote))
            : "bash -l";

    /// <summary>POSIX single-quote: 'a'b' → 'a'"'"'b'.</summary>
    internal static string ShellQuote(string arg) =>
        "'" + arg.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    /// <inheritdoc/>
    public async Task WriteAsync(string data)
    {
        if (_process is null)
        {
            return;
        }

        LastActivityUtc = DateTimeOffset.UtcNow;
        try
        {
            await _process.StandardInput.WriteAsync(data.AsMemory(), CancellationToken.None)
                .ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao escrever input no terminal.");
        }
    }

    /// <summary>
    /// Redimensiona via <c>ioctl(TIOCSWINSZ)</c> no device slave — resize em
    /// nível de kernel que chega nos apps em foreground (vim/top). Quando o
    /// pts não resolve, no-op: injetar <c>stty</c> no stdin ecoa como lixo
    /// visível no terminal.
    /// </summary>
    public Task ResizeAsync(int cols, int rows)
    {
        if (cols is < 1 or > 500 || rows is < 1 or > 500)
        {
            return Task.CompletedTask;
        }

        if (cols == _lastCols && rows == _lastRows)
        {
            return Task.CompletedTask;
        }

        _lastCols = cols;
        _lastRows = rows;
        if (!TryIoctlResize(cols, rows))
        {
            _logger.LogDebug(
                "PTY resize para {Cols}x{Rows} ignorado — slave indisponível.", cols, rows);
        }

        return Task.CompletedTask;
    }

    private const ulong TiocsWinsz = 0x5414;

    [StructLayout(LayoutKind.Sequential)]
    private struct Winsize
    {
        public ushort Rows;
        public ushort Cols;
        public ushort XPixel;
        public ushort YPixel;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(SafeHandle fd, ulong request, ref Winsize size);

    private bool TryIoctlResize(int cols, int rows)
    {
        try
        {
            var path = ResolvePtySlavePath();
            if (path is null)
            {
                return false;
            }

            using var slave = new FileStream(
                path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var size = new Winsize { Rows = (ushort)rows, Cols = (ushort)cols };
            return ioctl(slave.SafeFileHandle, TiocsWinsz, ref size) == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ioctl TIOCSWINSZ falhou.");
            return false;
        }
    }

    /// <summary>
    /// Acha o device slave (<c>/dev/pts/N</c>) do shell filho do
    /// <c>script</c>: <c>/proc/{pid}/task/{pid}/children</c> dá o pid do
    /// shell, cujo symlink de stdin aponta pro pts.
    /// </summary>
    private string? ResolvePtySlavePath()
    {
        if (_ptyPathResolved)
        {
            return _ptySlavePath;
        }

        try
        {
            var pid = _process?.Id;
            if (pid is null)
            {
                return null;
            }

            var childrenFile = $"/proc/{pid}/task/{pid}/children";
            if (!File.Exists(childrenFile))
            {
                return null;
            }

            foreach (var token in File.ReadAllText(childrenFile)
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token, out var childPid))
                {
                    continue;
                }

                var target = File.ResolveLinkTarget(
                    $"/proc/{childPid}/fd/0", returnFinalTarget: true);
                if (target is not null &&
                    target.FullName.StartsWith("/dev/pts/", StringComparison.Ordinal))
                {
                    // Só cacheia em sucesso — null pode ser race de spawn e
                    // deve ser tentado de novo no próximo resize.
                    _ptySlavePath = target.FullName;
                    _ptyPathResolved = true;
                    return _ptySlavePath;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Não foi possível resolver o path do PTY slave.");
        }

        return null;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _process!.StandardOutput
                    .ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                OutputReceived?.Invoke(new string(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelamento é o caminho esperado de shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pump de output do terminal encerrou com erro.");
        }
        finally
        {
            var exitCode = -1;
            try
            {
                if (_process is not null)
                {
                    // O pump encerra no EOF do stdout, que pode chegar antes
                    // do processo registrar exit — espera um prazo curto.
                    await _process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                        .ConfigureAwait(false);
                    exitCode = _process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Não foi possível ler o ExitCode do terminal.");
            }

            Exited?.Invoke(exitCode);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await (_pumpCts?.CancelAsync() ?? Task.CompletedTask).ConfigureAwait(false);
            _pumpCts?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao cancelar pump do terminal.");
        }

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao matar processo do terminal.");
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Terminal não encerrou dentro do prazo.");
            }

            _process.Dispose();
        }

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Task de pump não encerrou limpa.");
            }
        }
    }

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Join(dir, name))
            .FirstOrDefault(File.Exists);
    }
}
