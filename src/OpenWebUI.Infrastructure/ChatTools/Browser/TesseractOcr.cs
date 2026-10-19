using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OpenWebUI.Infrastructure.ChatTools.Browser;

/// <summary>
/// OCR local via binário <c>tesseract</c> do host
/// (SPEC-20261017-browser-visual-reasoning RF-005): resolvido no PATH uma
/// vez; ausente → <see cref="IsAvailable"/> false e os callers omitem o bloco
/// OCR sem falhar. <see cref="ReadTextAsync"/> devolve texto corrido para
/// <c>browser_observe</c>; <see cref="FindTextBoxAsync"/> usa a saída TSV
/// (left/top/width/height por palavra) para o self-healing de
/// <c>browser_act</c> — localiza o label na screenshot e retorna o centro
/// do bounding box para clique por coordenada.
/// </summary>
public sealed class TesseractOcr(ILogger<TesseractOcr> logger)
{
    private string? _path;
    private bool _resolved;

    /// <summary>True quando <c>tesseract</c> existe no PATH.</summary>
    public bool IsAvailable
    {
        get
        {
            Resolve();
            return _path is not null;
        }
    }

    /// <summary>Texto OCR da imagem PNG (null quando indisponível/falha).</summary>
    public async Task<string?> ReadTextAsync(string pngPath, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            return null;
        }
        var output = await RunAsync(pngPath, ["stdout"], ct);
        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    /// <summary>
    /// Localiza <paramref name="query"/> na imagem via TSV do tesseract e
    /// retorna o centro (x,y) do bounding box das palavras casadas — para
    /// clique por coordenada no self-healing.
    /// </summary>
    public async Task<(int X, int Y)?> FindTextBoxAsync(
        string pngPath, string query, CancellationToken ct)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(query))
        {
            return null;
        }
        var tsv = await RunAsync(pngPath, ["stdout", "tsv"], ct);
        return tsv is null ? null : FindTextBox(tsv, query);
    }

    /// <summary>
    /// Parse do TSV: palavras (level 5) casadas com os tokens da query —
    /// união dos bounding boxes → centro. Estático para teste direto.
    /// </summary>
    public static (int X, int Y)? FindTextBox(string tsv, string query)
    {
        var tokens = query.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        int? left = null, top = null, right = null, bottom = null;
        foreach (var cols in tsv.Split('\n').Select(line => line.Split('\t')))
        {
            if (cols.Length < 12 || cols[0] != "5" || string.IsNullOrWhiteSpace(cols[11]))
            {
                continue;
            }
            if (!int.TryParse(cols[6], out var l) || !int.TryParse(cols[7], out var t)
                || !int.TryParse(cols[8], out var w) || !int.TryParse(cols[9], out var h))
            {
                continue;
            }
            var word = cols[11].Trim().ToLowerInvariant()
                .Trim('.', ',', ':', ';', '!', '?', '"', '\'');
            if (!tokens.Any(tok => word.Contains(tok, StringComparison.Ordinal)))
            {
                continue;
            }
            left = Math.Min(left ?? l, l);
            top = Math.Min(top ?? t, t);
            right = Math.Max(right ?? l + w, l + w);
            bottom = Math.Max(bottom ?? t + h, t + h);
        }

        return left is null
            ? null
            : ((left.Value + right!.Value) / 2, (top!.Value + bottom!.Value) / 2);
    }

    private void Resolve()
    {
        if (_resolved)
        {
            return;
        }
        _resolved = true;
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }
                var candidate = Path.Join(dir, "tesseract");
                if (File.Exists(candidate))
                {
                    _path = candidate;
                    logger.LogInformation("browser OCR: tesseract em {Path}", candidate);
                    return;
                }
            }
        }
    }

    // Spawn do binário tesseract — depende do host; CI pode não ter.
    [ExcludeFromCodeCoverage]
    private async Task<string?> RunAsync(string pngPath, string[] extra, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _path!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(pngPath);
            foreach (var arg in extra)
            {
                psi.ArgumentList.Add(arg);
            }
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0
                ? await process.StandardOutput.ReadToEndAsync(ct)
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException
            or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
