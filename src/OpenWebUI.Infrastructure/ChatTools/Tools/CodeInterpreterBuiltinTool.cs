using System.Text.Json;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:code_interpreter</c> — executa um snippet de código e devolve
/// stdout (SPEC-20261007-chat-agent-tools RF-002). O snippet vai para um
/// arquivo em <c>{workspace}/.chat-tmp/</c> e roda com o runtime pedido
/// (<c>python3</c> ou <c>node</c>), timeout de 60s (teto 300s), kill tree no
/// cancel e saída truncada em 16KB com segredos mascarados.
/// Mutável → passa pelo gate de aprovação do preset de tool safety.
/// </summary>
public sealed class CodeInterpreterBuiltinTool : IBuiltinChatTool
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(300);

    /// <inheritdoc />
    public string Name => "code_interpreter";

    /// <inheritdoc />
    public string Description =>
        "Run python3/node code — call for calcs, parse, verify.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "language": {
              "type": "string",
              "enum": ["python3", "node"],
              "description": "Snippet runtime."
            },
            "code": { "type": "string", "description": "Source code to execute." },
            "timeout_seconds": { "type": "integer", "description": "Timeout (max 300).", "default": 60 }
          },
          "required": ["language", "code"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Exec;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var language = args.TryGetProperty("language", out var lang) ? lang.GetString() : null;
        var code = args.TryGetProperty("code", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(code))
        {
            return new BuiltinToolResult("Parâmetro 'code' é obrigatório.");
        }

        var (binary, extension) = language switch
        {
            "python3" or "python" => ("python3", ".py"),
            "node" => ("node", ".js"),
            _ => (null, null),
        };
        if (binary is null)
        {
            return new BuiltinToolResult(
                $"Linguagem '{language}' não suportada — use python3 ou node.");
        }

        var timeout = DefaultTimeout;
        if (args.TryGetProperty("timeout_seconds", out var t) && t.TryGetInt32(out var secs))
        {
            timeout = TimeSpan.FromSeconds(Math.Clamp(secs, 1, (int)MaxTimeout.TotalSeconds));
        }

        var tmpDir = Path.Join(context.WorkspacePath, ".chat-tmp");
        Directory.CreateDirectory(tmpDir);
        var scriptPath = Path.Join(tmpDir, $"{Guid.NewGuid():N}{extension}");
        await File.WriteAllTextAsync(scriptPath, code, ct);

        try
        {
            var outcome = await ChatProcessRunner.RunAsync(
                $"{binary} \"{scriptPath}\"",
                context.WorkspacePath,
                timeout,
                ct: ct);

            var status = outcome.TimedOut
                ? $"timeout após {(int)timeout.TotalSeconds}s"
                : $"exit {outcome.ExitCode}";
            var text = $"[{language} — {status}]\n{outcome.Output}";
            if (outcome.Truncated)
            {
                text += "\n[saída truncada em 16KB]";
            }

            return new BuiltinToolResult(text, new
            {
                language,
                exitCode = outcome.ExitCode,
                timedOut = outcome.TimedOut,
                truncated = outcome.Truncated,
            });
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }
}
