namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>Nível de risco de um comando para o gateway de execução.</summary>
public enum CommandRiskLevel
{
    /// <summary>Somente leitura dentro do workspace.</summary>
    Safe = 0,

    /// <summary>Escreve, mas confinado ao workspace do usuário.</summary>
    WorkspaceWrite = 1,

    /// <summary>Elevação, rede ou fora do workspace — executa, mas sempre
    /// exige aprovação (risco HIGH no preset "auto").</summary>
    Dangerous = 2,

    /// <summary>Destruição do sistema ou comando não executável — negado.
    /// Único nível que ainda recusa: o agente tem permissão total, mas não
    /// pode apagar a raiz nem quebrar a máquina que o hospeda.</summary>
    Forbidden = 3,
}

/// <summary>Veredito do classifier sobre um comando.</summary>
/// <param name="Level">Pior nível encontrado entre os segmentos.</param>
/// <param name="Reason">Motivo em linguagem natural.</param>
/// <param name="EscapesSandbox">Se o motivo é fuga do workspace.</param>
public sealed record CommandRiskAssessment(
    CommandRiskLevel Level, string Reason, bool EscapesSandbox = false)
{
    /// <summary>Atalho: pode executar (a tool decide aprovação, não aqui).
    /// Só <see cref="CommandRiskLevel.Forbidden"/> recusa.</summary>
    public bool Allowed => Level != CommandRiskLevel.Forbidden;
}

/// <summary>
/// Classifier pré-fork por lexer (SPEC-20261007-chat-agent-tools RF-003):
/// quebra a linha em segmentos de pipeline/cadeia, resolve argumentos de
/// caminho contra o workspace do usuário e retorna o pior risco.
/// Fail-closed: substituições, globs amplos e binários desconhecidos →
/// <see cref="CommandRiskLevel.Dangerous"/>. Portado do
/// DynamicCommandClassifier do agent-harness.
/// </summary>
public static class CommandRiskClassifier
{
    // Sempre negados — destroem o sistema de arquivos ou desligam a
    // máquina que hospeda o app (permissão total não cobre autodestruição).
    private static readonly HashSet<string> ForbiddenBinaries = new(StringComparer.Ordinal)
    {
        "dd", "mkfs", "fdisk", "mount", "umount",
        "shutdown", "reboot", "halt", "poweroff"
    };

    // Risco alto (exige aprovação no preset "auto") mas EXECUTA —
    // elevação, rede, intérpretes arbitrários, dumps de ambiente.
    private static readonly HashSet<string> DangerousBinaries = new(StringComparer.Ordinal)
    {
        "sudo", "su", "doas", "chmod", "chown", "chgrp",
        "curl", "wget", "nc", "ncat", "netcat", "ssh", "scp", "sftp", "ftp", "telnet",
        "kill", "killall", "pkill",
        "env", "printenv", "eval", "exec", "crontab",
        "systemctl", "service", "iptables", "useradd", "userdel", "usermod", "passwd",
        "apt", "apt-get", "yum", "dnf", "brew", "snap",
        "pip", "pip3", "gem", "npx",
        "bash", "sh", "zsh", "fish", "python", "python3", "perl", "ruby", "node",
        "docker", "podman", "kubectl", "terraform"
    };

    // Somente leitura — argumentos de caminho ainda conferidos contra o workspace.
    private static readonly HashSet<string> SafeBinaries = new(StringComparer.Ordinal)
    {
        "ls", "cat", "grep", "rg", "find", "pwd", "head", "tail", "wc",
        "file", "stat", "which", "whereis", "tree", "diff", "sed",
        "sort", "uniq", "tr", "cut", "awk", "jq", "date", "uname", "id",
        "whoami", "hostname", "df", "du", "ps", "true", "false", "test",
        "xargs", "readlink", "basename", "dirname", "realpath", "less", "more"
    };

    // Mutantes mas confinados ao workspace — caminhos verificados.
    private static readonly HashSet<string> WriteBinaries = new(StringComparer.Ordinal)
    {
        "touch", "mkdir", "rmdir", "cp", "mv", "ln", "tee", "echo", "printf",
        "npm", "yarn", "pnpm", "bun", "dotnet", "make", "cmake", "cargo", "go",
        "pytest",
        "tar", "zip", "unzip", "gzip", "gunzip", "rm"
    };

    /// <summary>Classifica a linha inteira; retorna o pior segmento.</summary>
    public static CommandRiskAssessment Classify(string command, string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new(CommandRiskLevel.Forbidden, "Comando vazio — fail-closed.");
        }

        // Substituição de comando / backticks não são auditáveis — risco
        // alto, mas executa (permissão total do agente).
        if (command.Contains("$(", StringComparison.Ordinal) || command.Contains('`'))
        {
            return new(CommandRiskLevel.Dangerous, "Substituição de comando detectada — não auditável.");
        }

        var worst = new CommandRiskAssessment(CommandRiskLevel.Safe, "Sem segmentos avaliados.");
        var segments = SplitSegments(command);
        if (segments.Count == 0)
        {
            return new(CommandRiskLevel.Forbidden, "Comando não parseável — fail-closed.");
        }

        return segments
            .Select(segment => ClassifySegment(segment, workspacePath))
            .Aggregate(worst, (w, a) => a.Level > w.Level ? a : w);
    }

    private static CommandRiskAssessment ClassifySegment(
        IReadOnlyList<string> tokens, string workspacePath)
    {
        if (tokens.Count == 0)
        {
            return new(CommandRiskLevel.Forbidden, "Segmento vazio — fail-closed.");
        }

        var index = 0;
        while (index < tokens.Count && IsEnvAssignment(tokens[index]))
        {
            index++;
        }

        if (index >= tokens.Count)
        {
            return new(CommandRiskLevel.Forbidden, "Sem binário após atribuições — fail-closed.");
        }

        var binary = Path.GetFileName(tokens[index]);
        var args = tokens.Skip(index + 1).ToList();

        return binary switch
        {
            "git" => ClassifyGit(args, workspacePath),
            "dotnet" => ClassifyDotnet(args, workspacePath),
            "npm" or "yarn" or "pnpm" => ClassifyNpm(args, workspacePath),
            "rm" => ClassifyRm(args, workspacePath),
            _ when ForbiddenBinaries.Contains(binary) =>
                new(CommandRiskLevel.Forbidden, $"'{binary}' destrói ou desliga o sistema — negado."),
            _ when DangerousBinaries.Contains(binary) =>
                new(CommandRiskLevel.Dangerous, $"'{binary}' requer elevação, rede ou execução arbitrária."),
            _ when binary is "sed" or "awk" && args.Any(a => a.StartsWith("-i"))
                => CheckPaths(args, workspacePath, CommandRiskLevel.WorkspaceWrite),
            _ when SafeBinaries.Contains(binary) => CheckPaths(args, workspacePath, CommandRiskLevel.Safe),
            _ when WriteBinaries.Contains(binary) => CheckPaths(args, workspacePath, CommandRiskLevel.WorkspaceWrite),
            _ => new(CommandRiskLevel.Dangerous, $"Binário desconhecido '{binary}' — executa sob permissão total.")
        };
    }

    private static CommandRiskAssessment ClassifyGit(
        IReadOnlyList<string> args, string workspacePath)
    {
        var sub = args.FirstOrDefault(a => !a.StartsWith('-'));
        switch (sub)
        {
            case null:
                return new(CommandRiskLevel.Safe, "git sem subcomando.");
            case "status" or "diff" or "log" or "show" or "blame" or "rev-parse"
                or "ls-files" or "ls-tree" or "describe" or "shortlog" or "reflog"
                or "remote" or "config" or "branch" or "tag" or "stash":
                return ClassifyReadOnlyGit(sub, args, workspacePath);
            case "push" or "pull" or "fetch" or "clone" or "remote-update":
                return new(CommandRiskLevel.Dangerous, $"git {sub} toca rede/remoto.");
            case "reset" when args.Any(a => a is "--hard"):
                return new(CommandRiskLevel.Dangerous, "git reset --hard descarta trabalho.");
            case "clean" when args.Any(a => a.StartsWith("-f") || a.Contains('f')):
                return new(CommandRiskLevel.Dangerous, "git clean -f remove arquivos não rastreados.");
            case "add" or "commit" or "checkout" or "switch" or "restore" or "merge"
                or "rebase" or "cherry-pick" or "mv" or "rm" or "init" or "worktree"
                or "reset" or "clean" or "apply" or "format-patch" or "am":
                return CheckPaths(args.Skip(1).ToList(), workspacePath, CommandRiskLevel.WorkspaceWrite);
            default:
                return new(CommandRiskLevel.Dangerous, $"Subcomando git '{sub}' desconhecido — fail-closed.");
        }
    }

    private static CommandRiskAssessment ClassifyReadOnlyGit(
        string sub, IReadOnlyList<string> args, string workspacePath)
    {
        if (sub is "branch" or "tag" && args.Any(a => a is "-D" or "--delete" or "-d"))
        {
            return new(CommandRiskLevel.Dangerous, "Deleção de ref local detectada.");
        }

        if (sub == "stash" && args.Any(a => a is "drop" or "clear"))
        {
            return new(CommandRiskLevel.Dangerous, "Descarte de stash detectado.");
        }

        if (sub == "remote" && args.Any(a => a is "add" or "remove" or "set-url"))
        {
            return CheckPaths(args.Skip(1).ToList(), workspacePath, CommandRiskLevel.WorkspaceWrite);
        }

        if (sub == "config" && args.Any(a => a is "--global" or "--system"))
        {
            return new(CommandRiskLevel.Dangerous, "git config fora do repositório.");
        }

        return CheckPaths(args.Skip(1).ToList(), workspacePath, CommandRiskLevel.Safe);
    }

    private static CommandRiskAssessment ClassifyDotnet(
        IReadOnlyList<string> args, string workspacePath)
    {
        var sub = args.FirstOrDefault(a => !a.StartsWith('-'));
        switch (sub)
        {
            case "test" or "vstest":
                return CheckPaths(args.Skip(1).ToList(), workspacePath, CommandRiskLevel.Safe);
            case "build" or "restore" or "run" or "watch" or "publish" or "pack"
                or "format" or "clean" or "new" or "add" or "remove" or "list"
                or "sln" or "build-server":
                return CheckPaths(args.Skip(1).ToList(), workspacePath, CommandRiskLevel.WorkspaceWrite);
            case "nuget" or "tool" or "workload":
                return new(CommandRiskLevel.Dangerous, $"dotnet {sub} instala/publica fora do workspace.");
            default:
                return new(CommandRiskLevel.Dangerous, $"Subcomando dotnet '{sub}' desconhecido — fail-closed.");
        }
    }

    private static CommandRiskAssessment ClassifyNpm(
        IReadOnlyList<string> args, string workspacePath)
    {
        var sub = args.FirstOrDefault(a => !a.StartsWith('-'));
        return sub switch
        {
            "publish" or "login" or "logout" or "token" or "config" =>
                new(CommandRiskLevel.Dangerous, $"npm {sub} toca credenciais/registry remoto."),
            null or "test" or "run" or "install" or "ci" or "exec" or "build"
                or "start" or "pack" or "link" or "update" or "audit" or "outdated"
                or "list" or "ls" or "why" or "dedupe" or "prune" =>
                CheckPaths(args.Skip(1).ToList(), workspacePath, CommandRiskLevel.WorkspaceWrite),
            _ => new(CommandRiskLevel.Dangerous, $"Subcomando npm '{sub}' desconhecido — fail-closed.")
        };
    }

    private static CommandRiskAssessment ClassifyRm(
        IReadOnlyList<string> args, string workspacePath)
    {
        var targets = args.Where(a => !a.StartsWith('-')).ToList();
        if (targets.Count == 0)
        {
            return new(CommandRiskLevel.Forbidden, "rm sem alvo — fail-closed.");
        }

        // Deletar a raiz (ou montes dela) segue negado — o resto do
        // filesystem é alcançável com permissão total.
        if (targets.Any(t => Path.GetFullPath(t, workspacePath) == "/"))
        {
            return new(CommandRiskLevel.Forbidden,
                "Deleção da raiz do sistema — negado.");
        }

        var outside = targets.FirstOrDefault(target => !TryResolveInsideWorkspace(target, workspacePath));
        if (outside is not null)
        {
            return new(CommandRiskLevel.Dangerous,
                $"Alvo '{outside}' fora ou irresolúvel no workspace — executa sob permissão total.",
                EscapesSandbox: true);
        }

        return new(CommandRiskLevel.WorkspaceWrite,
            "Deleção de arquivos dentro do workspace.");
    }

    /// <summary>
    /// Aplica o check de confinamento a cada argumento que parece caminho; o
    /// piso é <paramref name="baseLevel"/> e qualquer alvo fora escala para
    /// Dangerous. Globs amplos e <c>$VAR</c> são irresolúveis → Dangerous.
    /// </summary>
    private static CommandRiskAssessment CheckPaths(
        IReadOnlyList<string> args, string workspacePath, CommandRiskLevel baseLevel)
    {
        var level = baseLevel;
        var sawRedirect = false;

        var i = 0;
        while (i < args.Count)
        {
            var arg = args[i];
            if (IsRedirect(arg))
            {
                if (RedirectEscapes(args, i, workspacePath))
                {
                    return new(CommandRiskLevel.Dangerous, "Redirect para fora do workspace.", EscapesSandbox: true);
                }

                sawRedirect = true;
                level = CommandRiskLevel.WorkspaceWrite;
                i++;
                continue;
            }

            if (IsSkippableArg(arg))
            {
                i++;
                continue;
            }

            if (!TryResolveInsideWorkspace(arg, workspacePath))
            {
                return new(CommandRiskLevel.Dangerous,
                    $"Caminho '{arg}' escapa ou não resolve dentro do workspace.",
                    EscapesSandbox: true);
            }

            i++;
        }

        if (sawRedirect)
        {
            return new(CommandRiskLevel.WorkspaceWrite, "Escrita via redirect dentro do workspace.");
        }

        var message = baseLevel == CommandRiskLevel.Safe
            ? "Leitura confinada ao workspace."
            : "Escrita confinada ao workspace.";
        return new(level, message);
    }

    private static bool IsRedirect(string arg) => arg is ">" or ">>" or "1>" or "2>" or "&>";

    private static bool RedirectEscapes(IReadOnlyList<string> args, int i, string workspacePath)
        => i + 1 >= args.Count || !TryResolveInsideWorkspace(args[i + 1], workspacePath);

    private static bool IsSkippableArg(string arg)
        => arg.StartsWith('-') || arg == "--" || !LooksLikePath(arg);

    private static bool LooksLikePath(string arg)
        => arg.Contains('/') || arg.Contains('\\') || arg.StartsWith('~')
            || arg.StartsWith('.') || arg.StartsWith('$')
            || arg.Contains('*') || arg.Contains('?');

    private static bool TryResolveInsideWorkspace(string arg, string workspacePath)
    {
        if (arg is "*" or "**" or "*.*")
        {
            return false;
        }

        if (arg.Contains('*') || arg.Contains('?'))
        {
            return !arg.StartsWith('/') && !arg.StartsWith('~') && !arg.StartsWith('$')
                && !arg.Split('/').Any(seg => seg == "..");
        }

        if (arg.StartsWith('~') || arg.StartsWith('$')
            || arg.Contains('(') || arg.Contains(')'))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(arg, workspacePath);
            return PathInside(workspacePath, full);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="full"/> está dentro de (ou é) <paramref name="root"/>
    /// — comparação por segmentos após normalização.
    /// </summary>
    public static bool PathInside(string root, string full)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var normalizedFull = Path.GetFullPath(full);
        var prefix = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedFull.Equals(
                   normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
               || normalizedFull.StartsWith(
                   prefix,
                   OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsEnvAssignment(string token)
    {
        var eq = token.IndexOf('=');
        if (eq <= 0)
        {
            return false;
        }

        var name = token[..eq];
        return (char.IsLetter(name[0]) || name[0] == '_')
            && name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>
    /// Quebra em <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c> fora de
    /// aspas e tokeniza cada segmento respeitando aspas simples/duplas e
    /// operadores de redirect como tokens próprios.
    /// </summary>
    private static List<IReadOnlyList<string>> SplitSegments(string command) =>
        new Splitter(command).Run();

    private sealed class Splitter
    {
        private readonly string _command;
        private readonly List<IReadOnlyList<string>> _segments = new();
        private readonly List<string> _tokens = new();
        private readonly System.Text.StringBuilder _current = new();
        private bool _inSingle;
        private bool _inDouble;
        private int _i;

        public Splitter(string command) => _command = command;

        public List<IReadOnlyList<string>> Run()
        {
            while (_i < _command.Length)
            {
                var c = _command[_i];
                if (_inSingle || _inDouble)
                {
                    if (c == (_inSingle ? '\'' : '"'))
                    {
                        _inSingle = _inDouble = false;
                    }
                    else
                    {
                        _current.Append(c);
                    }

                    _i++;
                    continue;
                }

                ConsumeUnquoted(c);
                _i++;
            }

            FlushSegment();
            return _segments;
        }

        private void FlushToken()
        {
            if (_current.Length > 0)
            {
                _tokens.Add(_current.ToString());
                _current.Clear();
            }
        }

        private void FlushSegment()
        {
            FlushToken();
            if (_tokens.Count > 0)
            {
                _segments.Add(_tokens.ToArray());
                _tokens.Clear();
            }
        }

        private void ConsumeUnquoted(char c)
        {
            switch (c)
            {
                case '\'':
                    _inSingle = true;
                    break;
                case '"':
                    _inDouble = true;
                    break;
                case ' ' or '\t' or '\n' or '\r':
                    FlushToken();
                    break;
                case '&' when _i + 1 < _command.Length && _command[_i + 1] == '&':
                    FlushSegment();
                    _i++;
                    break;
                case '|':
                    FlushSegment();
                    if (_i + 1 < _command.Length && _command[_i + 1] == '|')
                    {
                        _i++;
                    }

                    break;
                case ';':
                    FlushSegment();
                    break;
                case '>' or '<':
                    ConsumeRedirect(c);
                    break;
                default:
                    _current.Append(c);
                    break;
            }
        }

        private void ConsumeRedirect(char c)
        {
            FlushToken();
            var run = 1;
            while (_i + run < _command.Length && _command[_i + run] == c)
            {
                run++;
            }

            _tokens.Add(new string(c, run));
            _i += run - 1;
        }
    }
}
