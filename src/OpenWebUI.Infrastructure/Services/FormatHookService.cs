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
    ILogger<FormatHookService> logger,
    Func<string, CancellationToken, Task<bool>>? binaryProbe = null)
{
    private const int MaxOutputChars = 4096;

    /// <summary>Timeout do hook em segundos (default 60, configurável para testes).</summary>
    private int TimeoutSeconds => Math.Max(1, configuration.GetValue("Format:TimeoutSeconds", 60));

    /// <summary>Probe de binário disponível (seam de teste; default `command -v`).</summary>
    private Func<string, CancellationToken, Task<bool>> BinaryProbe =>
        binaryProbe ?? DefaultBinaryProbe;

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

    /// <summary>Só os comandos de binding (chat → repo) — sem o global.</summary>
    private async Task<string?> ResolveBindingOnlyAsync(
        string userId, string? chatId, CancellationToken ct)
    {
        if (chatId is not null
            && await repos.GetChatBindingAsync(chatId, ct) is { } chatBinding
            && !string.IsNullOrWhiteSpace(chatBinding.FormatCommand))
        {
            return chatBinding.FormatCommand;
        }
        var binding = await repos.GetBindingAsync(userId, ct);
        return string.IsNullOrWhiteSpace(binding?.FormatCommand) ? null : binding.FormatCommand;
    }

    /// <summary>
    /// SPEC-20261018-formatters-catalog RF-002: agrupa os arquivos
    /// tocados por extensão e roda CADA formatter uma vez com a sua lista
    /// (binding custom → Format:Commands:{ext} → builtin detectado).
    /// Falhas nunca quebram a tool — viram warnings agregados no retorno.
    /// </summary>
    public async Task<string?> RunForUserAsync(
        string userId, string workdir, IReadOnlyList<string> jailedPaths,
        CancellationToken ct, string? chatId = null)
    {
        if (jailedPaths.Count == 0)
        {
            return null;
        }

        // Precedência por extensão: binding (chat→repo) → Format:Commands:{ext}
        // (específico) → Format:Command (catch-all) → builtin detectado.
        var bindingCommand = await ResolveBindingOnlyAsync(userId, chatId, ct);
        var disabled = DisabledSet();
        var warnings = new List<string>();
        foreach (var (ext, files) in FormatterCatalog.GroupByExtension(jailedPaths))
        {
            var resolved = await FormatterCatalog.ResolveAsync(
                ext, bindingCommand,
                configuration["Format:Commands:" + ext] ?? configuration["Format:Command"],
                BinaryProbe, ct);
            if (resolved.Entry is not { } entry || disabled.Contains(entry.Name))
            {
                continue;
            }
            var warning = await RunEntryAsync(entry, workdir, files, ct);
            if (warning is not null)
            {
                warnings.Add(warning);
            }
        }
        return warnings.Count == 0 ? null : string.Join('\n', warnings);
    }

    /// <summary>
    /// {files} → lista shell-quoted de uma vez; {file} → uma invocação
    /// por arquivo; sem placeholder → arquivos como argumentos finais.
    /// </summary>
    private static IReadOnlyList<string> Interpolate(
        string command, IReadOnlyList<string> files)
    {
        if (command.Contains("{files}", StringComparison.Ordinal))
        {
            var filesArg = string.Join(' ', files.Select(Quote));
            return [command.Replace("{files}", filesArg, StringComparison.Ordinal)];
        }
        if (command.Contains("{file}", StringComparison.Ordinal))
        {
            return [.. files.Select(f =>
                command.Replace("{file}", Quote(f), StringComparison.Ordinal))];
        }
        return [$"{command} {string.Join(' ', files.Select(Quote))}"];
    }

    /// <summary>Roda um formatter sobre sua lista de arquivos; devolve warning ou null.</summary>
    private async Task<string?> RunEntryAsync(
        FormatterEntry entry, string workdir, IReadOnlyList<string> files,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        foreach (var cmd in Interpolate(entry.Command, files))
        {
            var warning = await RunCommandAsync(entry, cmd, workdir, files.Count, ct);
            if (warning is not null)
            {
                warnings.Add(warning);
            }
        }
        return warnings.Count == 0 ? null : string.Join('\n', warnings);
    }

    /// <summary>Roda uma linha de comando já interpolada; devolve warning ou null.</summary>
    private async Task<string?> RunCommandAsync(
        FormatterEntry entry, string cmd, string workdir, int fileCount,
        CancellationToken ct)
    {
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
        if (entry.Env is not null)
        {
            foreach (var (key, value) in entry.Env)
            {
                psi.Environment[key] = value;
            }
        }

        var timeoutSeconds = Math.Max(1,
            entry.TimeoutSeconds ?? TimeoutSeconds);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return $"format hook não iniciou ({cmd})";
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
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

                return $"format hook {entry.Name} excedeu {timeoutSeconds}s — processo morto";
            }

            var (outText, errText) = (await stdout, await stderr);
            // RF-003: {formatter, files, exit, durationMs} no run log.
            logger.LogDebug(
                "format hook {Formatter} files={Files} exit={Exit} ms={Ms}",
                entry.Name, fileCount, process.ExitCode, sw.ElapsedMilliseconds);
            if (process.ExitCode == 0)
            {
                return null;
            }

            return $"format {entry.Name} falhou (exit {process.ExitCode}): "
                + Truncate(FirstNonEmpty(errText, outText));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "format hook falhou ao iniciar ({Command})", cmd);
            return $"format {entry.Name} falhou ao iniciar: {ex.Message}";
        }
    }

    /// <summary>Format:Disabled — lista de nomes de formatter a pular (csv).</summary>
    private HashSet<string> DisabledSet() =>
        new((configuration["Format:Disabled"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Detecção real de binário via `command -v` (POSIX).</summary>
    private static async Task<bool> DefaultBinaryProbe(
        string binary, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"command -v {binary} >/dev/null 2>&1");
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException
            or InvalidOperationException or OperationCanceledException)
        {
            return false;
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
