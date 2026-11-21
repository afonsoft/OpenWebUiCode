using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:agent_validate</c> — valida o projeto do workdir
/// (SPEC-20261121-dev-pipeline RF-002): detecta o stack e roda a suíte de
/// testes canônica (<c>dotnet test</c>, <c>npm test</c>, <c>pytest</c>,
/// <c>go test</c>, <c>cargo test</c>) ou o <c>command</c> explícito, e
/// devolve evidência estruturada (exit code + git status) — a mesma
/// evidência que o gate de entrega e o orquestrador procuram na run.
/// Comando customizado passa pelo <see cref="CommandRiskClassifier"/>;
/// os detectados são constantes internas. Mutável → gate de aprovação.
/// </summary>
public sealed class AgentValidateBuiltinTool : IBuiltinChatTool
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(300);

    /// <inheritdoc />
    public string Name => "agent_validate";

    /// <inheritdoc />
    public string Description =>
        "Validate the project — auto-detects the stack and runs its test "
            + "suite (dotnet test / npm test / pytest / go test / cargo test) "
            + "plus git status. Run it before committing: the returned "
            + "exit code and uncommitted count are the delivery evidence.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "Optional validation command override (default: auto-detected suite for the workspace stack)."
            },
            "timeout_seconds": {
              "type": "integer",
              "description": "Timeout (default 300, max 600).",
              "default": 300
            }
          }
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Exec;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var command = args.TryGetProperty("command", out var c)
            && c.ValueKind == JsonValueKind.String
                ? c.GetString()?.Trim()
                : null;
        var detected = command is null;
        command ??= ProjectValidation.Detect(context.WorkspacePath);
        if (string.IsNullOrWhiteSpace(command))
        {
            return new BuiltinToolResult(
                "Nenhum stack reconhecido no workdir (sem *.sln*/*.csproj, "
                    + "package.json, pytest/pyproject, go.mod ou Cargo.toml). "
                    + "Passe o comando de validação explicitamente em 'command'.",
                new { validated = false, detected = false });
        }

        // Comando customizado segue a mesma política do shell_exec — os
        // detectados são constantes internas e dispensam o classifier.
        if (!detected)
        {
            var assessment = CommandRiskClassifier.Classify(
                command, context.WorkspacePath);
            if (!assessment.Allowed)
            {
                return new BuiltinToolResult(
                    $"Comando negado: {assessment.Reason}",
                    new { validated = false, refused = true },
                    Refused: true, RefuseReason: assessment.Reason);
            }
        }

        var timeout = DefaultTimeout;
        if (args.TryGetProperty("timeout_seconds", out var t)
            && t.TryGetInt32(out var secs))
        {
            timeout = TimeSpan.FromSeconds(
                Math.Clamp(secs, 1, (int)ProjectValidation.MaxTimeout.TotalSeconds));
        }

        var result = await ProjectValidation.RunAsync(
            context.WorkspacePath, command, timeout, ct);
        var outcome = result.Outcome;
        var gitLine = result.GitPorcelain is null
            ? "git: workdir não é repositório git"
            : $"git: {(result.Uncommitted == 0 ? "tree limpa" : $"{result.Uncommitted} arquivo(s) não commitado(s)")}";
        var status = outcome.TimedOut
            ? $"timeout após {(int)timeout.TotalSeconds}s"
            : $"exit {outcome.ExitCode}";
        var text = $"[validate] {command} → {status} ({(result.Passed ? "passed" : "FAILED")})"
            + $"\n{gitLine}\n{outcome.Output}";
        if (outcome.Truncated)
        {
            text += "\n[saída truncada em 16KB]";
        }

        return new BuiltinToolResult(text, new
        {
            validated = result.Passed,
            command,
            exitCode = outcome.ExitCode,
            timedOut = outcome.TimedOut,
            gitClean = result.GitPorcelain is not null && result.Uncommitted == 0,
            uncommitted = result.Uncommitted,
        });
    }
}
