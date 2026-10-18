using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools.Browser;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// Base das tools de sessão de browser (SPEC-20261017-browser-visual-reasoning):
/// flag <c>browser.enabled</c> (a mesma do <c>browser_screenshot</c>), SSRF
/// com loopback liberado, observação pós-ação (screenshot → FileEntry +
/// elementos indexados + OCR opcional) e budget de ações por run.
/// </summary>
public abstract class BrowserSessionToolBase : IBuiltinChatTool
{
    /// <summary>Serviço de sessões de browser por chat.</summary>
    protected readonly BrowserSessionService Sessions;
    /// <summary>OCR local opcional (tesseract do host).</summary>
    protected readonly TesseractOcr Ocr;
    private readonly ConfigService _config;
    private readonly IConfiguration _configuration;
    private readonly AppDbContext _db;

    /// <summary>Injeta as dependências compartilhadas das tools de browser.</summary>
    protected BrowserSessionToolBase(
        BrowserSessionService sessions,
        TesseractOcr ocr,
        ConfigService config,
        IConfiguration configuration,
        AppDbContext db)
    {
        Sessions = sessions;
        Ocr = ocr;
        _config = config;
        _configuration = configuration;
        _db = db;
    }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public abstract string ParametersJson { get; }

    /// <inheritdoc />
    public abstract ApprovalTier Tier { get; }

    /// <inheritdoc />
    public abstract Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct);

    /// <summary>Chave de sessão do chat (isolamento entre conversas).</summary>
    protected static string ChatKey(BuiltinToolContext context) =>
        context.ChatId ?? context.UserId;

    /// <summary>Chave do budget de ações (por run; cai pro chat fora de run).</summary>
    protected static string RunKey(BuiltinToolContext context) =>
        context.RunId ?? context.ChatId ?? context.UserId;

    /// <summary>Flag <c>browser.enabled</c> — mesma do screenshot.</summary>
    protected async Task<BuiltinToolResult?> DisabledResultAsync(CancellationToken ct)
    {
        var enabled =
            string.Equals(_configuration["BrowserTools:Enabled"], "true",
                StringComparison.OrdinalIgnoreCase)
            || await _config.GetAsync(BrowserScreenshotBuiltinTool.EnabledKey, false, ct);
        return enabled
            ? null
            : new BuiltinToolResult(
                $"Erro: {Name} está desabilitada — o administrador liga em "
                + "PUT /api/v1/browser/config ou env BrowserTools__Enabled=true.");
    }

    /// <summary>Consome o budget de ação; false → mensagem amigável.</summary>
    protected BuiltinToolResult? BudgetExceeded(BuiltinToolContext context) =>
        Sessions.TryConsumeAction(RunKey(context))
            ? null
            : new BuiltinToolResult(
                $"Budget de ações de browser esgotado ({Sessions.MaxActionsPerRun}/run) — "
                + "feche a sessão com browser_close ou peça uma nova run.");

    /// <summary>
    /// Valida a URL http(s) e aplica o SSRF guard (loopback liberado; LAN e
    /// metadata de cloud bloqueados — RF-006).
    /// </summary>
    protected static async Task<BuiltinToolResult?> ValidateUrlAsync(
        string? raw, CancellationToken ct)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return new BuiltinToolResult("URL inválida — use http(s) absoluta.");
        }
        if (await SsrfGuard.IsBlockedAsync(uri.Host, allowLoopback: true, ct))
        {
            return new BuiltinToolResult(
                $"Erro: host '{uri.Host}' resolve para endereço privado de rede — "
                + "bloqueado (apenas loopback/localhost é permitido).");
        }
        return null;
    }

    /// <summary>
    /// Extrai a observação padrão pós-ação (RF-003): elementos indexados +
    /// screenshot salvo como FileEntry + OCR quando habilitado.
    /// </summary>
    protected async Task<(string Text, object Payload)> ObserveAsync(
        IPage page, BuiltinToolContext context, string? prefix, CancellationToken ct)
    {
        List<BrowserElement> elements;
        try
        {
            var json = await page.EvaluateAsync<JsonElement>(
                BrowserElements.ExtractScript, BrowserElements.MaxElements);
            elements = BrowserElements.Parse(json);
        }
        catch (PlaywrightException)
        {
            elements = [];
        }

        byte[]? png = null;
        try
        {
            png = await page.ScreenshotAsync(
                new PageScreenshotOptions { Type = ScreenshotType.Png });
        }
        catch (PlaywrightException)
        {
            // Página recém-aberta pode falhar o print — a observação textual
            // ainda vale; o screenshot volta no próximo passo.
        }

        string? imagePath = null;
        string? fileId = null;
        string? ocrText = null;
        if (png is { Length: > 0 })
        {
            (fileId, imagePath, ocrText) =
                await SaveScreenshotAsync(png, context, Sessions.OcrEnabled, ct);
        }

        string title, url;
        try
        {
            title = await page.TitleAsync();
            url = page.Url;
        }
        catch (PlaywrightException)
        {
            title = "";
            url = page.Url ?? "";
        }

        var text = $"{(prefix is null ? "" : prefix + " — ")}title: {title} | url: {url} | "
            + $"elements: {BrowserElements.Format(elements)}"
            + (ocrText is not null ? $" | ocr_text: {ocrText[..Math.Min(ocrText.Length, 2000)]}" : "");
        var payload = new
        {
            fileId,
            imagePath,
            url,
            title,
            elements = elements.Select(e => new
            {
                index = e.Index, tag = e.Tag, role = e.Role,
                name = e.Name, href = e.Href, type = e.Type,
            }).ToList(),
            ocr_text = ocrText,
        };
        return (text, payload);
    }

    /// <summary>
    /// Salva o PNG como FileEntry do usuário e roda OCR quando habilitado.
    /// </summary>
    private async Task<(string FileId, string ImagePath, string? OcrText)> SaveScreenshotAsync(
        byte[] png, BuiltinToolContext context, bool withOcr, CancellationToken ct)
    {
        var fileId = Guid.NewGuid().ToString();
        var filename = $"browser-{fileId[..8]}.png";
        Directory.CreateDirectory(context.UploadDir);
        var storagePath = Path.Join(context.UploadDir, $"{fileId}_{filename}");
        await File.WriteAllBytesAsync(storagePath, png, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _db.Files.Add(new FileEntry
        {
            Id = fileId,
            UserId = context.UserId,
            Filename = filename,
            ContentType = "image/png",
            StoragePath = storagePath,
            Size = png.Length,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(ct);

        string? ocrText = null;
        if (withOcr && Ocr.IsAvailable)
        {
            ocrText = await Ocr.ReadTextAsync(storagePath, ct);
        }
        return (fileId, $"/api/v1/files/{fileId}/content", ocrText);
    }

    /// <summary>Campos auxiliares dos args JSON.</summary>
    protected static string? Str(JsonElement args, string name) =>
        args.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    /// <summary>Campo inteiro opcional.</summary>
    protected static int? Num(JsonElement args, string name) =>
        args.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : null;
}
