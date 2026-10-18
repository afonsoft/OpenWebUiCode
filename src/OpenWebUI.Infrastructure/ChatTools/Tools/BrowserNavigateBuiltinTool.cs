using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using OpenWebUI.Infrastructure.ChatTools.Browser;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:browser_navigate</c> — abre uma URL na sessão de browser do
/// chat e devolve a observação pós-navegação (screenshot + elementos
/// indexados). SPEC-20261017-browser-visual-reasoning RF-003.
/// </summary>
public sealed class BrowserNavigateBuiltinTool(
    IBrowserSessions sessions,
    TesseractOcr ocr,
    ConfigService config,
    IConfiguration configuration,
    AppDbContext db)
    : BrowserSessionToolBase(sessions, ocr, config, configuration, db)
{
    /// <inheritdoc />
    public override string Name => "browser_navigate";

    /// <inheritdoc />
    public override string Description =>
        "Navigate the chat's browser session to a URL and observe the page "
        + "(screenshot + indexed interactive elements).";

    /// <inheritdoc />
    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "http(s) URL to open (localhost allowed)." }
          },
          "required": ["url"]
        }
        """;

    /// <inheritdoc />
    public override ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public override async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (await DisabledResultAsync(ct) is { } disabled)
        {
            return disabled;
        }
        if (BudgetExceeded(context) is { } budget)
        {
            return budget;
        }
        if (await ValidateUrlAsync(Str(args, "url"), ct) is { } invalid)
        {
            return invalid;
        }

        var url = Str(args, "url")!;
        var chatKey = ChatKey(context);
        try
        {
            var page = await Sessions.GetPageAsync(chatKey, ct);
            await page.GotoAsync(url,
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            var (text, payload) = await ObserveAsync(page, context, $"Navigated to {url}", ct);
            return new BuiltinToolResult(text, payload);
        }
        catch (PlaywrightException ex)
        {
            await Sessions.ResetChatAsync(chatKey);
            return new BuiltinToolResult(
                $"browser_navigate falhou ({Short(ex)}). A sessão foi reiniciada — tente de novo.");
        }
        catch (InvalidOperationException ex)
        {
            return new BuiltinToolResult($"browser_navigate falhou: {ex.Message}");
        }
    }

    private static string Short(PlaywrightException ex) =>
        ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
}
