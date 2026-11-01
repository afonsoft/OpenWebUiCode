using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Executor de hooks <c>command</c> (SPEC-20261031-lifecycle-hooks
/// RF-001): o payload do evento vai no stdin (JSON), a decisão vem no
/// stdout/exit code — <c>0</c> = aplica decisão do JSON, <c>2</c> =
/// block com stderr como reason, demais códigos/JSON inválido/timeout =
/// erro não-bloqueante. Ambiente mínimo <c>OPENWEBUI_*</c>, sem secrets;
/// cwd = workspace da run.
/// </summary>
public sealed class CommandHookRunner(ILogger<CommandHookRunner>? log = null)
{
    /// <summary>Shell usado para o comando (testável).</summary>
    internal static Func<string, string, string, ProcessStartInfo> ShellOf =
        (cmd, cwd, args) =>
        {
            var psi = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = cwd,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(cmd);
            return psi;
        };

    /// <summary>Resultado bruto de um processo de hook.</summary>
    public sealed record RawResult(
        int ExitCode, string Stdout, string Stderr, bool TimedOut);

    /// <summary>
    /// Executa o hook: stdin = payload JSON; devolve o
    /// <see cref="HookOutcome"/> do processo (nunca lança — falha é
    /// não-bloqueante).
    /// </summary>
    public async Task<HookOutcome> RunAsync(
        HookDefinition hook, JsonElement payload, string cwd,
        string? sessionId, string? promptId, CancellationToken ct)
    {
        var raw = await ExecAsync(hook, payload, cwd, sessionId, promptId, ct);
        if (raw.TimedOut)
        {
            log?.LogWarning("Hook {Source} timeout após {S}s — ignorado.",
                hook.Source, hook.TimeoutSeconds);
            return HookOutcome.ApproveNone;
        }
        if (raw.ExitCode == 2)
        {
            return new HookOutcome(HookDecision.Block,
                string.IsNullOrWhiteSpace(raw.Stderr) ? "blocked by hook" : raw.Stderr.Trim(),
                hook.Source, null, []);
        }
        if (raw.ExitCode != 0)
        {
            log?.LogWarning("Hook {Source} exit {Code}: {Err} — não-bloqueante.",
                hook.Source, raw.ExitCode, raw.Stderr.Trim());
            return HookOutcome.ApproveNone;
        }
        return ParseStdout(raw.Stdout, hook, log);
    }

    /// <summary>Roda o processo coletando stdout/stderr com timeout.</summary>
    public async Task<RawResult> ExecAsync(
        HookDefinition hook, JsonElement payload, string cwd,
        string? sessionId, string? promptId, CancellationToken ct)
    {
        var psi = ShellOf(hook.Command, cwd, hook.Command);
        psi.Environment["OPENWEBUI_PROJECT_DIR"] = cwd;
        if (sessionId is not null)
        {
            psi.Environment["OPENWEBUI_SESSION_ID"] = sessionId;
        }
        if (promptId is not null)
        {
            psi.Environment["OPENWEBUI_PROMPT_ID"] = promptId;
        }
        try
        {
            using var proc = Process.Start(psi)!;
            try
            {
                await proc.StandardInput.WriteAsync(payload.GetRawText());
                proc.StandardInput.Close();
            }
            catch (IOException)
            {
                // comando que não lê stdin pode sair antes do write —
                // broken pipe não é falha do hook.
            }
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(hook.TimeoutSeconds));
            using var linked = CancellationTokenSource
                .CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                await proc.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return new RawResult(-1, "", "timeout", TimedOut: true);
            }
            return new RawResult(
                proc.ExitCode, await stdout, await stderr, TimedOut: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogWarning(ex, "Hook {Source} falhou ao iniciar.", hook.Source);
            return new RawResult(-1, "", ex.Message, TimedOut: false);
        }
    }

    /// <summary>Interpreta o stdout JSON do hook (exit 0).</summary>
    internal static HookOutcome ParseStdout(
        string stdout, HookDefinition hook, ILogger? log)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return HookOutcome.ApproveNone;
        }
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var decision = Str(root, "decision")?.ToLowerInvariant();
            var specific = root.TryGetProperty("hookSpecificOutput", out var hso)
                && hso.ValueKind == JsonValueKind.Object
                    ? hso
                    : (JsonElement?)null;
            var updated = specific is { } s
                && s.TryGetProperty("updatedInput", out var u)
                && u.ValueKind == JsonValueKind.Object
                    ? u.GetRawText()
                    : null;
            var contexts = new List<string>();
            if (specific is { } sp
                && sp.TryGetProperty("additionalContext", out var ac)
                && ac.ValueKind == JsonValueKind.String
                && ac.GetString() is { Length: > 0 } ctx)
            {
                contexts.Add(ctx);
            }
            var reason = Str(root, "reason");
            return decision switch
            {
                "block" => new HookOutcome(HookDecision.Block,
                    reason, hook.Source, updated, contexts),
                "ask" => new HookOutcome(HookDecision.Ask,
                    reason, hook.Source, updated, contexts),
                _ => new HookOutcome(HookDecision.Approve,
                    reason, null, updated, contexts),
            };
        }
        catch (JsonException)
        {
            log?.LogWarning("Hook {Source}: stdout não-JSON — não-bloqueante.",
                hook.Source);
            return HookOutcome.ApproveNone;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
