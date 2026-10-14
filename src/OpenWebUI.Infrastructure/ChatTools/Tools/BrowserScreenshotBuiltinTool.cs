using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:browser_screenshot</c> — captura um screenshot PNG de uma
/// URL com browser headless e renderiza inline no transcript
/// (SPEC-20261007-chat-agent-parity RF-017, o "browser_use" de fase 1:
/// o agente testa a UI que ele mesmo subiu). Guard SSRF com
/// <b>loopback liberado</b> — o caso de uso é justamente o preview
/// local — mas LAN/metadata de cloud seguem bloqueados.
/// Feature flag: <c>BrowserTools:Enabled</c>/env ou o kv
/// <c>browser.enabled</c> ligado pelo admin (off por padrão).
/// </summary>
public sealed class BrowserScreenshotBuiltinTool(
    BrowserScreenshotService screenshots,
    ConfigService config,
    IConfiguration configuration,
    AppDbContext db) : IBuiltinChatTool
{
    /// <summary>Chave kv da flag administrável.</summary>
    public const string EnabledKey = "browser.enabled";

    /// <inheritdoc />
    public string Name => "browser_screenshot";

    /// <inheritdoc />
    public string Description =>
        "Screenshot a web page — call for visual checks or URLs.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "http(s) URL to capture (localhost allowed)." },
            "width": { "type": "integer", "description": "Viewport width in px.", "default": 1280 },
            "height": { "type": "integer", "description": "Viewport height in px.", "default": 800 }
          },
          "required": ["url"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (!await IsEnabledAsync(ct))
        {
            return new BuiltinToolResult(
                "Erro: browser_screenshot está desabilitada — o administrador liga em "
                + "PUT /api/v1/browser/config ou env BrowserTools__Enabled=true.");
        }

        var raw = args.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return new BuiltinToolResult("URL inválida — use http(s) absoluta.");
        }

        // Loopback liberado: o agente já executa shell no próprio host —
        // LAN/metadata de cloud continuam bloqueados.
        if (await SsrfGuard.IsBlockedAsync(uri.Host, allowLoopback: true, ct))
        {
            return new BuiltinToolResult(
                $"Erro: host '{uri.Host}' resolve para endereço privado de rede — "
                + "bloqueado (apenas loopback/localhost é permitido).");
        }

        var width = args.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv) ? wv : 1280;
        var height = args.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv) ? hv : 800;

        byte[] png;
        try
        {
            png = await screenshots.CaptureAsync(uri, width, height, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new BuiltinToolResult($"Screenshot falhou: {ex.Message}");
        }

        var fileId = Guid.NewGuid().ToString();
        var filename = $"screenshot-{fileId[..8]}.png";
        Directory.CreateDirectory(context.UploadDir);
        var storagePath = Path.Join(context.UploadDir, $"{fileId}_{filename}");
        await File.WriteAllBytesAsync(storagePath, png, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        db.Files.Add(new FileEntry
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
        await db.SaveChangesAsync(ct);

        var imagePath = $"/api/v1/files/{fileId}/content";
        return new BuiltinToolResult(
            $"Screenshot de {uri} ({width}x{height}): {imagePath}",
            new
            {
                fileId,
                imagePath,
                url = uri.ToString(),
                width,
                height,
            });
    }

    /// <summary>Flag: <c>BrowserTools:Enabled</c>/env ou kv persistido.</summary>
    internal async Task<bool> IsEnabledAsync(CancellationToken ct) =>
        string.Equals(configuration["BrowserTools:Enabled"], "true",
            StringComparison.OrdinalIgnoreCase)
        || await config.GetAsync(EnabledKey, false, ct);
}
