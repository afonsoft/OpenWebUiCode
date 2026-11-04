using System.Text.Json;
using System.Text.RegularExpressions;
using OpenWebUI.Infrastructure.Sandbox;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:tool_install</c> — instala CLIs ausentes via gerenciadores
/// allowlisted (SPEC-20261103-tool-install-tool): comando montado por
/// template (sem shell string livre no nome do pacote), verificação
/// pós-install via <c>command -v</c> e resultado honesto. Mutável →
/// gate de aprovação do preset, igual ao <see cref="ShellExecBuiltinTool"/>.
/// </summary>
public sealed class ToolInstallBuiltinTool(
    AgentSecretsEnvService? agentEnv = null,
    ConfigService? config = null,
    Func<string, string, TimeSpan, CancellationToken, Task<ProcessOutcome>>?
        runner = null) : IBuiltinChatTool
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(300);

    /// <summary>Nome de pacote seguro — letras, dígitos e os separadores
    /// legítimos de cada ecossistema (escopo npm <c>@a/b</c>, extras pip
    /// <c>pkg[all]</c>, pin <c>==1.2</c>, caminho go). Sem espaços nem
    /// metacaracteres de shell → impossível injetar comando.</summary>
    private static readonly Regex SafePackage = new(
        @"^[A-Za-z0-9][A-Za-z0-9._+@/:~=\[\],\^\-]*$",
        RegexOptions.Compiled);

    /// <summary>Templates por gerenciador allowlisted; <c>{0}</c> é o
    /// pacote já validado por <see cref="SafePackage"/>.</summary>
    private static readonly IReadOnlyDictionary<string, string> Managers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["apt-get"] = "apt-get install -y {0}",
            ["pip"] = "pip install --user {0}",
            ["pip3"] = "pip3 install --user {0}",
            ["pipx"] = "pipx install {0}",
            ["npm"] = "npm install -g {0}",
            ["dotnet"] = "dotnet tool install -g {0}",
            ["cargo"] = "cargo install {0}",
            ["go"] = "go install {0}",
        };

    /// <inheritdoc />
    public string Name => "tool_install";

    /// <inheritdoc />
    public string Description =>
        "Install a missing CLI via an allowed package manager "
        + "(apt-get, pip, pipx, npm -g, dotnet tool, cargo, go install) — "
        + "curated command templates + post-install verification. "
        + "Approval follows the chat preset.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "manager": { "type": "string", "description": "apt-get | pip | pip3 | pipx | npm | dotnet | cargo | go." },
            "package": { "type": "string", "description": "Package name (may include version pin/extras, e.g. 'pkg==1.2', 'pkg[all]', '@scope/name', 'path/mod@latest')." },
            "binary": { "type": "string", "description": "Binary to verify after install (defaults to a name derived from package, e.g. '@angular/cli' → 'cli')." },
            "check_only": { "type": "boolean", "description": "true = only report whether the binary resolves; no install.", "default": false },
            "sudo_password": { "type": "string", "description": "Sudo password for system managers (apt-get), same convention as shell_exec." },
            "timeout_seconds": { "type": "integer", "description": "Install timeout (max 300).", "default": 120 }
          },
          "required": ["manager", "package"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Exec;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var manager = args.TryGetProperty("manager", out var m)
            ? m.GetString() : null;
        var package = args.TryGetProperty("package", out var p)
            ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(manager)
            || !Managers.TryGetValue(manager, out var template))
        {
            return new BuiltinToolResult(
                $"Gerenciador '{manager}' fora da allowlist "
                + $"({string.Join(", ", Managers.Keys)}).");
        }
        if (string.IsNullOrWhiteSpace(package)
            || package.Length > 200 || !SafePackage.IsMatch(package))
        {
            return new BuiltinToolResult(
                $"Nome de pacote inválido: '{package}'.");
        }

        var binary = args.TryGetProperty("binary", out var b)
            && b.GetString() is { Length: > 0 } bn
                ? bn : DeriveBinary(package);
        var checkOnly = args.TryGetProperty("check_only", out var co)
            && co.ValueKind is JsonValueKind.True;
        var timeout = ResolveTimeout(args);

        if (checkOnly)
        {
            return await VerifyAsync(binary, package, context, timeout, ct);
        }

        var command = BuildCommand(manager, package);
        var env = agentEnv is null || runner is not null
            ? AgentEnv.Empty
            : await agentEnv.BuildAsync(context.UserId, ct);
        var (prepared, stdin) = PrepareSudo(command, args);
        var install = await RunAsync(
            prepared, context, timeout,
            new ChatProcessRunner.RunOptions(
                Stdin: stdin, Env: env.Vars,
                SecretsToScrub: env.SecretsToScrub), ct);
        if (install.TimedOut || install.ExitCode != 0)
        {
            return new BuiltinToolResult(
                $"Install falhou ({manager} {package}): "
                + $"exit {install.ExitCode}"
                + $"{(install.TimedOut ? " (timeout)" : "")}\n"
                + Truncate(install.Output),
                new
                {
                    ok = false, installed = false, manager, package, command,
                    exit_code = install.ExitCode,
                    stderr_excerpt = Truncate(install.Output, 400),
                });
        }

        var verified = await VerifyAsync(
            binary, package, context, timeout, ct,
            new { manager, package, command });
        return verified;
    }

    /// <summary>Monta o comando do template do gerenciador; go install
    /// exige sufixo de versão → completa com <c>@latest</c>.</summary>
    internal static string BuildCommand(string manager, string package) =>
        string.Equals(manager, "go", StringComparison.OrdinalIgnoreCase)
            && !package.Contains('@')
                ? string.Format(Managers["go"], package + "@latest")
                : string.Format(Managers[manager], package);

    /// <summary>Deriva o binário do pacote: último segmento de path sem
    /// extras/pin (<c>@angular/cli</c>→cli, <c>black[all]</c>→black,
    /// <c>pkg==1.2</c>→pkg, <c>x/mod@v</c>→mod).</summary>
    internal static string DeriveBinary(string package)
    {
        var last = package.Split('/')[^1];
        var cut = last.IndexOfAny(['[', '=', '@']);
        return cut > 0 ? last[..cut] : last;
    }

    /// <summary>Verifica <c>command -v binary</c> e, quando presente,
    /// tenta <c>binary --version</c> (best-effort, 15s).</summary>
    private async Task<BuiltinToolResult> VerifyAsync(
        string binary, string package, BuiltinToolContext context,
        TimeSpan timeout, CancellationToken ct, object? extra = null)
    {
        var which = await RunAsync(
            $"command -v {binary}", context,
            TimeSpan.FromSeconds(Math.Min(15, timeout.TotalSeconds)),
            null, ct);
        if (which.ExitCode != 0)
        {
            return new BuiltinToolResult(
                $"Binário '{binary}' não resolvido após "
                + $"{(extra is null ? "check" : "install")} de '{package}'.",
                new
                {
                    ok = false, installed = false, package, binary,
                });
        }

        var resolvedPath = which.Output.Split('\n')[0].Trim();
        var version = await RunAsync(
            $"{binary} --version", context, TimeSpan.FromSeconds(15),
            null, ct);
        var versionLine = version.ExitCode == 0
            ? version.Output.Split('\n')[0].Trim() : null;

        return new BuiltinToolResult(
            $"ok — '{binary}' resolvido em {resolvedPath}"
            + $"{(versionLine is null ? "" : $" ({versionLine})")}",
            extra is null
                ? new
                {
                    ok = true, installed = true, binary,
                    resolved_path = resolvedPath, version = versionLine,
                }
                : new
                {
                    ok = true, installed = true, binary,
                    resolved_path = resolvedPath, version = versionLine,
                    manager = extra,
                });
    }

    /// <summary>Executa via runner injetado (testes) ou pelo pipeline real
    /// (secrets env + sandbox do shell_exec).</summary>
    private async Task<ProcessOutcome> RunAsync(
        string command, BuiltinToolContext context, TimeSpan timeout,
        ChatProcessRunner.RunOptions? options, CancellationToken ct)
    {
        Directory.CreateDirectory(context.WorkspacePath);
        if (runner is not null)
        {
            return await runner(
                command, context.WorkspacePath, timeout, ct);
        }
        if (config is null)
        {
            return await ChatProcessRunner.RunAsync(
                command, context.WorkspacePath, timeout, options, ct);
        }
        var boxed = await SandboxExec.RunAsync(
            Name, command, context, config, timeout, options, ct);
        return boxed.Outcome;
    }

    /// <summary>sudo: senha via ask_user primea <c>sudo -S -v</c>
    /// (mesma convenção do shell_exec).</summary>
    private static (string Command, string? Stdin) PrepareSudo(
        string command, JsonElement args)
    {
        var pw = args.TryGetProperty("sudo_password", out var p)
            ? p.GetString() : null;
        return !string.IsNullOrEmpty(pw)
            ? ("sudo -S -p '' -v; " + command, pw + "\n")
            : (command, null);
    }

    /// <summary><c>timeout_seconds</c> clampeado a [1, 300]s; default 120s.</summary>
    private static TimeSpan ResolveTimeout(JsonElement args)
    {
        var timeout = DefaultTimeout;
        if (args.TryGetProperty("timeout_seconds", out var t)
            && t.TryGetInt32(out var secs))
        {
            timeout = TimeSpan.FromSeconds(
                Math.Clamp(secs, 1, (int)MaxTimeout.TotalSeconds));
        }
        return timeout;
    }

    /// <summary>Trunca excertos de saída para o payload de erro.</summary>
    private static string Truncate(string s, int max = 800) =>
        s.Length <= max ? s : s[..max] + "…";
}
