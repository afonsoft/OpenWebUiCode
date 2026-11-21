using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Detecção e execução da suíte de testes do projeto no workdir
/// (SPEC-20261121-dev-pipeline RF-001): mapeia marcadores de stack para o
/// comando de teste canônico e roda via <see cref="ChatProcessRunner"/>,
/// junto de <c>git status --porcelain</c> do mesmo diretório. Compartilhado
/// entre <c>builtin:agent_validate</c> e o orquestrador <c>dev_pipeline</c>
/// — a mesma detecção vale para tool e para a fase determinística.
/// </summary>
public static class ProjectValidation
{
    /// <summary>Timeout máximo de uma execução de validação (testes grandes).</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Resultado da validação: comando executado, saída e estado git.</summary>
    /// <param name="Command">Comando efetivamente executado.</param>
    /// <param name="Outcome">Resultado do processo (exit code, saída, timeout).</param>
    /// <param name="GitPorcelain">
    /// Saída do <c>git status --porcelain</c>; <c>null</c> quando o workdir
    /// não é repositório git.
    /// </param>
    public sealed record Result(
        string Command, ProcessOutcome Outcome, string? GitPorcelain)
    {
        /// <summary>Suíte passou (exit 0 sem timeout).</summary>
        public bool Passed => Outcome is { ExitCode: 0, TimedOut: false };

        /// <summary>Arquivos não commitados (linhas do porcelain).</summary>
        public int Uncommitted => GitPorcelain is null
            ? 0
            : GitPorcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    /// Comando de teste detectado pelos marcadores do workdir, ou
    /// <c>null</c> quando nenhum stack é reconhecido (caller decide —
    /// pede comando explícito ou reporta skip).
    /// </summary>
    public static string? Detect(string workdir)
    {
        if (!Directory.Exists(workdir))
        {
            return null;
        }
        if (Directory.GetFiles(workdir, "*.sln*").Length > 0
            || Directory.GetFiles(workdir, "*.csproj").Length > 0)
        {
            return "dotnet test --nologo";
        }
        if (File.Exists(Path.Join(workdir, "package.json")))
        {
            return "npm test";
        }
        if (File.Exists(Path.Join(workdir, "pytest.ini"))
            || File.Exists(Path.Join(workdir, "conftest.py"))
            || File.Exists(Path.Join(workdir, "pyproject.toml"))
            || File.Exists(Path.Join(workdir, "requirements.txt")))
        {
            return "pytest -q";
        }
        if (File.Exists(Path.Join(workdir, "go.mod")))
        {
            return "go test ./...";
        }
        if (File.Exists(Path.Join(workdir, "Cargo.toml")))
        {
            return "cargo test";
        }
        return null;
    }

    /// <summary>
    /// Roda <paramref name="command"/> no workdir com timeout clampeado a
    /// <see cref="MaxTimeout"/> e lê o <c>git status --porcelain</c> do
    /// mesmo diretório (não-repo → null — ausência de git não é falha de
    /// validação).
    /// </summary>
    public static async Task<Result> RunAsync(
        string workdir, string command, TimeSpan timeout, CancellationToken ct)
    {
        var outcome = await ChatProcessRunner.RunAsync(
            command, workdir,
            timeout > MaxTimeout ? MaxTimeout : timeout,
            null, ct);
        var porcelain = await WorktreeService.StatusPorcelainAsync(workdir, ct);
        return new Result(command, outcome, porcelain);
    }
}
