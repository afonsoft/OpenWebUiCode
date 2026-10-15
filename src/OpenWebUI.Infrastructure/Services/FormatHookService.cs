using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Format hook (SPEC-20261009-worktree-format-hooks, RF-004): roda um
/// comando formatador depois de file_write/file_edit/apply_patch. O
/// comando vem do binding do repo (<c>WorkspaceRepoBinding.FormatCommand</c>
/// — "per-repo binding") e cai para o global <c>Format:Command</c> quando
/// vazio. <c>{files}</c> é interpolado com os paths relativos já jailed
/// (entre aspas simples); sem placeholder, os arquivos vão como argumentos
/// finais. Timeout <c>Format:TimeoutSeconds</c> (default 60s). Falha
/// (exit≠0, timeout, spawn) NUNCA quebra a tool — vira texto de warning
/// que o chamador anexa ao tool_result.
/// </summary>
public sealed class FormatHookService(
    WorkspaceRepoService repos,
    IConfiguration configuration,
    ILogger<FormatHookService> logger)
{
    private const int MaxOutputChars = 400;

    /// <summary>Timeout do hook em segundos (default 60, configurável para testes).</summary>
    private int TimeoutSeconds => Math.Max(1, configuration.GetValue("Format:TimeoutSeconds", 60));

    /// <summary>
    /// Comando configurado (chat binding → global binding → Format:Command),
    /// ou null. SPEC-15: o binding por chat tem prioridade.
    /// </summary>
    public async Task<string?> ResolveCommandAsync(
        string userId, string? chatId = null, CancellationToken ct = default)
    {
        if (chatId is not null
            && await repos.GetChatBindingAsync(chatId, ct) is { } chatBinding
            && !string.IsNullOrWhiteSpace(chatBinding.FormatCommand))
        {
            return chatBinding.FormatCommand;
        }

        var binding = await repos.GetBindingAsync(userId, ct);
        if (!string.IsNullOrWhiteSpace(binding?.FormatCommand))
        {
            return binding.FormatCommand;
        }

        var global = configuration["Format:Command"];
        return string.IsNullOrWhiteSpace(global) ? null : global;
    }

    /// <summary>
    /// Roda o formatador sobre os arquivos tocados (já resolvidos dentro do
    /// jail pelo chamador). Devolve o texto de warning em falha, ou null em
    /// sucesso/ausência de comando.
    /// </summary>
    public async Task<string?> RunForUserAsync(
        string userId, string workdir, IReadOnlyList<string> jailedPaths,
        CancellationToken ct, string? chatId = null)
    {
        if (jailedPaths.Count == 0)
        {
            return null;
        }

        var command = await ResolveCommandAsync(userId, chatId, ct);
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var files = string.Join(' ', jailedPaths.Select(Quote));
        var cmd = command.Contains("{files}", StringComparison.Ordinal)
            ? command.Replace("{files}", files, StringComparison.Ordinal)
            : $"{command} {files}";

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(cmd);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return $"format hook não iniciou ({command})";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception killEx) when (killEx is InvalidOperationException or Win32Exception)
                {
                    // Já saiu no intervalo.
                }

                return $"format hook excedeu {TimeoutSeconds}s — processo morto";
            }

            var (outText, errText) = (await stdout, await stderr);
            if (process.ExitCode == 0)
            {
                logger.LogDebug("format hook ok ({Command}): {Output}", cmd, outText.Trim());
                return null;
            }

            return $"format hook falhou (exit {process.ExitCode}): "
                + Truncate(FirstNonEmpty(errText, outText));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "format hook falhou ao iniciar ({Command})", cmd);
            return $"format hook falhou ao iniciar: {ex.Message}";
        }
    }

    /// <summary>Aspas simples POSIX — paths vêm do jail, sem newline.</summary>
    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    private static string FirstNonEmpty(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) ? a : b;

    private static string Truncate(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length > MaxOutputChars ? trimmed[..MaxOutputChars] : trimmed;
    }
}
