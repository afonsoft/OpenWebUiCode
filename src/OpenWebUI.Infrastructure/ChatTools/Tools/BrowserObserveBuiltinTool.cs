using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using OpenWebUI.Infrastructure.ChatTools.Browser;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:browser_observe</c> — screenshot + snapshot de elementos
/// indexados (+ OCR quando habilitado) da página atual da sessão.
/// SPEC-20261017-browser-visual-reasoning RF-003/RF-005.
/// </summary>
public sealed class BrowserObserveBuiltinTool(
    IBrowserSessions sessions,
    TesseractOcr ocr,
    ConfigService config,
    IConfiguration configuration,
    AppDbContext db)
    : BrowserSessionToolBase(sessions, ocr, config, configuration, db)
{
    /// <inheritdoc />
    public override string Name => "browser_observe";

    /// <inheritdoc />
    public override string Description =>
        "Observe the current page of the chat's browser session: screenshot, "
        + "title/url and indexed interactive elements.";

    /// <inheritdoc />
    public override string ParametersJson => """
        { "type": "object", "properties": {} }
        """;

    /// <inheritdoc />
    public override ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;

    /// <inheritdoc />
    public override async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (await DisabledResultAsync(ct) is { } disabled)
        {
            return disabled;
        }

        var chatKey = ChatKey(context);
        try
        {
            var page = await Sessions.GetPageAsync(chatKey, ct);
            var (text, payload) = await ObserveAsync(page, context, null, ct);
            return new BuiltinToolResult(text, payload);
        }
        catch (PlaywrightException ex)
        {
            await Sessions.ResetChatAsync(chatKey);
            return new BuiltinToolResult(
                $"browser_observe falhou ({(ex.Message.Length > 200 ? ex.Message[..200] : ex.Message)}). "
                + "A sessão foi reiniciada — navegue novamente.");
        }
        catch (InvalidOperationException ex)
        {
            return new BuiltinToolResult($"browser_observe falhou: {ex.Message}");
        }
    }
}
