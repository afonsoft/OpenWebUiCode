using System.Text;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Bound genérico pós-execução de outputs de tool
/// (SPEC-20261016-tool-output-bounding, Epic #398, slice #402): outputs
/// acima de <c>builtinTools.outputMaxLines</c>/<c>builtinTools.outputMaxBytes</c>
/// (2000 linhas / 50 KiB por padrão) são gravados completos —
/// <see cref="SecretScrubber.Scrub"/> antes de tocar o disco — em
/// <c>{data}/tool-output/tool_*.txt</c> e o modelo recebe head+tail
/// delimitados com o path para leitura posterior via <c>file_read</c>
/// (allowlist estrita, <see cref="TryResolveManaged"/>). Cleanup de
/// arquivos &gt;7 dias roda no ciclo de manutenção.
/// </summary>
public class ToolOutputService(
    ConfigService config,
    Microsoft.Extensions.Hosting.IHostEnvironment env,
    ILogger<ToolOutputService> logger)
{
    /// <summary>Default de linhas por resultado de tool (RF-001).</summary>
    public const int DefaultMaxLines = 2000;

    /// <summary>Default de bytes por resultado de tool (50 KiB, RF-001).</summary>
    public const int DefaultMaxBytes = 50 * 1024;

    /// <summary>Retenção dos arquivos gerenciados (RF-004).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>Parte do head no limite de linhas (resto vai pro tail).</summary>
    private const double HeadShare = 0.7;

    private string OutputDir =>
        Path.Join(DataPaths.Root(env.ContentRootPath), "tool-output");

    /// <summary>Resultado do bound (texto final + arquivo gerenciado quando houve overflow).</summary>
    public sealed record BoundResult(
        string Text,
        bool Truncated,
        string? OutputPath = null,
        int TotalLines = 0,
        int TotalBytes = 0);

    /// <summary>
    /// Aplica o bound no <paramref name="text"/> já scrubbed da tool.
    /// Overflow → grava o output completo (re-scrub defensivo) e devolve
    /// head 70%/tail 30% com marcador de truncamento. Falha de escrita
    /// cai no fallback sem arquivo (workspace read-only).
    /// </summary>
    /// <param name="text">Output da tool (pós-execute, pós-scrub).</param>
    /// <param name="toolName">Nome da tool (legibilidade do arquivo).</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<BoundResult> BoundAsync(
        string text, string toolName, CancellationToken ct = default)
    {
        var maxLines = Math.Clamp(
            await config.GetAsync("builtinTools.outputMaxLines", DefaultMaxLines, ct),
            10, 100_000);
        var maxBytes = Math.Clamp(
            await config.GetAsync("builtinTools.outputMaxBytes", DefaultMaxBytes, ct),
            1024, 10 * 1024 * 1024);

        var lines = text.Split('\n');
        var totalBytes = Encoding.UTF8.GetByteCount(text);
        if (lines.Length <= maxLines && totalBytes <= maxBytes)
        {
            return new BoundResult(text, Truncated: false);
        }

        var headLines = (int)(maxLines * HeadShare);
        var tailLines = maxLines - headLines;
        var scrubbed = SecretScrubber.Scrub(text) ?? text;

        string? path = null;
        try
        {
            path = await WriteManagedAsync(toolName, scrubbed, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Data dir read-only — só trunca, sem arquivo (edge case do SPEC).
            logger.LogWarning(ex, "tool-output: escrita falhou — devolvendo só truncado.");
        }

        var sb = new StringBuilder(capacity: Math.Min(maxBytes + 1024, text.Length));
        AppendLines(sb, lines, 0, headLines, ref maxBytes);
        var marker = path is null
            ? $"[... {lines.Length} linhas omitidas — output excedeu o limite ...]"
            : $"[... {CountOmitted(lines, headLines, tailLines)} linhas omitidas — "
                + $"output completo: {path} ({lines.Length} linhas, "
                + $"{totalBytes} B) ...]";
        sb.Append(marker).Append('\n');
        var start = Math.Max(headLines, lines.Length - tailLines);
        AppendLines(sb, lines, start, lines.Length - start, ref maxBytes);
        return new BoundResult(sb.ToString(), Truncated: true, path,
            lines.Length, totalBytes);
    }

    /// <summary>
    /// Resolve um path absoluto sob <c>{data}/tool-output/</c> para
    /// <c>file_read</c> (RF-005): só nomes <c>tool_*.txt</c>, sem
    /// subdiretório nem traversal — null quando fora da allowlist.
    /// </summary>
    public string? TryResolveManaged(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return null;
        }

        var name = Path.GetFileName(path);
        if (name is null || !name.StartsWith("tool_", StringComparison.Ordinal)
            || !name.EndsWith(".txt", StringComparison.Ordinal))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }

        var dir = Path.GetDirectoryName(full);
        return dir is not null
            && CommandRiskClassifier.PathInside(OutputDir, full)
            && string.Equals(
                Path.GetFullPath(dir), Path.GetFullPath(OutputDir),
                StringComparison.Ordinal)
            ? full
            : null;
    }

    /// <summary>Remove <c>tool_*.txt</c> com mais de <paramref name="maxAge"/> (RF-004).</summary>
    public Task<int> CleanupAsync(TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? Retention);
        var removed = 0;
        try
        {
            if (!Directory.Exists(OutputDir))
            {
                return Task.FromResult(0);
            }

            foreach (var file in Directory.EnumerateFiles(OutputDir, "tool_*.txt"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "tool-output cleanup: não removeu {File}", file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "tool-output cleanup falhou.");
        }

        if (removed > 0)
        {
            logger.LogInformation("tool-output cleanup: {Removed} arquivos removidos.", removed);
        }

        return Task.FromResult(removed);
    }

    /// <summary>Escrita atômica (tmp + rename) do output completo scrubbed.</summary>
    private async Task<string> WriteManagedAsync(
        string toolName, string text, CancellationToken ct)
    {
        Directory.CreateDirectory(OutputDir);
        var safe = new string([.. toolName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);
        var file = Path.Join(OutputDir,
            $"tool_{safe}_{Guid.NewGuid():N}.txt");
        var tmp = file + ".tmp";
        await File.WriteAllTextAsync(tmp, text, ct);
        File.Move(tmp, file, overwrite: true);
        return file;
    }

    private static void AppendLines(
        StringBuilder sb, string[] lines, int start, int count, ref int budgetBytes)
    {
        var end = Math.Min(lines.Length, start + count);
        for (var i = start; i < end && budgetBytes > 0; i++)
        {
            var line = lines[i];
            var bytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (bytes > budgetBytes)
            {
                var room = Math.Max(0, budgetBytes - 16);
                sb.Append(Encoding.UTF8.GetString(
                    Encoding.UTF8.GetBytes(line).AsSpan(0, room)));
                sb.Append("\n[... linha truncada por bytes ...]\n");
                budgetBytes = 0;
                return;
            }

            sb.Append(line).Append('\n');
            budgetBytes -= bytes;
        }
    }

    private static int CountOmitted(string[] lines, int head, int tail) =>
        Math.Max(0, lines.Length - head - tail);
}
