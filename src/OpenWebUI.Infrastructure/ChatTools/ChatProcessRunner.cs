using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>Resultado de um processo disparado pelo chat.</summary>
/// <param name="ExitCode">Código de saída (−1 se não iniciou/morreu).</param>
/// <param name="Output">stdout+stderr combinados, truncados e limpos.</param>
/// <param name="Truncated">Se a saída foi cortada no limite.</param>
/// <param name="TimedOut">Se morreu por timeout.</param>
public sealed record ProcessOutcome(
    int ExitCode, string Output, bool Truncated, bool TimedOut);

/// <summary>
/// Runner compartilhado das tools de execução do chat
/// (SPEC-20261007-chat-agent-tools): spawna <c>/bin/sh -c</c> com stdout e
/// stderr redirecionados, aplica timeout, mata a árvore de processo ao
/// cancelar e mascara segredos na saída antes de devolver.
/// </summary>
public static class ChatProcessRunner
{
    private const int DefaultMaxOutputChars = 16_000;

    /// <summary>
    /// Executa <paramref name="command"/> num shell no diretório de trabalho
    /// dado; retorna saída combinada limitada a
    /// <paramref name="maxOutputChars"/> e segredos mascarados.
    /// <paramref name="env"/> injeta variáveis resolvidas (secrets do
    /// usuário ∪ global ∪ managed GitHub — SPEC-20261017-agent-secrets-env);
    /// <paramref name="secretsToScrub"/> mascara os valores literais na saída.
    /// </summary>
    public static async Task<ProcessOutcome> RunAsync(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        int maxOutputChars = DefaultMaxOutputChars,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyCollection<string>? secretsToScrub = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            process.Start();
        }
        catch (Win32Exception ex) { return new ProcessOutcome(-1, $"Falha ao iniciar processo: {ex.Message}", false, false); }
        catch (ObjectDisposedException ex) { return new ProcessOutcome(-1, $"Falha ao iniciar processo: {ex.Message}", false, false); }
        catch (InvalidOperationException ex) { return new ProcessOutcome(-1, $"Falha ao iniciar processo: {ex.Message}", false, false); }
        catch (PlatformNotSupportedException ex) { return new ProcessOutcome(-1, $"Falha ao iniciar processo: {ex.Message}", false, false); }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            process.StandardInput.Close();
        }

        var timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
        }

        if (!process.HasExited || timedOut || ct.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { /* Já morreu. */ }
            catch (Win32Exception) { /* Já morreu. */ }
            catch (NotSupportedException) { /* Já morreu. */ }
        }

        try
        {
            stdout.Append(await stdoutTask);
            stderr.Append(await stderrTask);
        }
        catch (OperationCanceledException) { /* leituras canceladas — usar o que sobrou */ }
        catch (IOException) { /* pipe fechado — usar o que sobrou */ }

        var combined = stdout.ToString();
        if (stderr.Length > 0)
        {
            combined += (combined.Length > 0 ? "\n" : string.Empty) + stderr;
        }

        var truncated = false;
        if (combined.Length > maxOutputChars)
        {
            combined = combined[..maxOutputChars];
            truncated = true;
        }

        var scrubbed = SecretScrubber.Scrub(combined, secretsToScrub) ?? string.Empty;
        var exitCode = timedOut ? -1 : process.HasExited ? process.ExitCode : -1;
        return new ProcessOutcome(exitCode, scrubbed, truncated, timedOut);
    }
}
