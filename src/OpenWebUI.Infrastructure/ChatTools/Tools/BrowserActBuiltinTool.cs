using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Playwright;
using OpenWebUI.Infrastructure.ChatTools.Browser;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:browser_act</c> — executa click/fill/press/select/scroll na
/// sessão do chat. Alvo preferido: <c>index</c> do elemento observado
/// (carimbo <c>data-owui-el</c>); alternativa <c>selector</c> CSS ou clique
/// por coordenada <c>x,y</c>. Seletor quebrado aciona o self-healing
/// (SPEC-20261017-browser-visual-reasoning RF-004): texto visível → OCR no
/// último screenshot → lista rankeada de candidatos — nunca erro seco.
/// Tier <see cref="ApprovalTier.Exec"/> (efeito colateral real na página).
/// </summary>
public sealed class BrowserActBuiltinTool(
    IBrowserSessions sessions,
    TesseractOcr ocr,
    ConfigService config,
    IConfiguration configuration,
    AppDbContext db)
    : BrowserSessionToolBase(sessions, ocr, config, configuration, db)
{
    private const string ClickAction = "click";

    /// <inheritdoc />
    public override string Name => "browser_act";

    /// <inheritdoc />
    public override string Description =>
        "Act on the chat's browser page: click/fill/press/select/scroll by "
        + "element index (from observe), CSS selector or x/y coordinates.";

    /// <inheritdoc />
    public override string ParametersJson => $$"""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["{{ClickAction}}", "fill", "press", "select", "scroll"],
                        "description": "Action to perform." },
            "index":  { "type": "integer", "description": "Element index from browser_observe." },
            "selector": { "type": "string", "description": "CSS selector fallback." },
            "text":   { "type": "string", "description": "Visible text fallback / fill source." },
            "value":  { "type": "string", "description": "Value for fill/select." },
            "key":    { "type": "string", "description": "Key for press (e.g. Enter)." },
            "x":      { "type": "integer", "description": "Click X (coordinate fallback)." },
            "y":      { "type": "integer", "description": "Click Y (coordinate fallback)." },
            "deltaY": { "type": "integer", "description": "Scroll delta in px.", "default": 600 }
          },
          "required": ["action"]
        }
        """;

    /// <inheritdoc />
    public override ApprovalTier Tier => ApprovalTier.Exec;

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

        var action = Str(args, "action")?.ToLowerInvariant();
        if (action is not (ClickAction or "fill" or "press" or "select" or "scroll"))
        {
            return new BuiltinToolResult(
                "Ação inválida — use click|fill|press|select|scroll.");
        }

        var chatKey = ChatKey(context);
        try
        {
            var page = await Sessions.GetPageAsync(chatKey, ct);
            var acted = await TryActAsync(page, args, action, ct);
            if (acted is not null)
            {
                return acted;
            }

            var (text, payload) = await ObserveAsync(page, context, $"Action {action}", ct);
            return new BuiltinToolResult(text, payload);
        }
        catch (PlaywrightException ex)
        {
            await Sessions.ResetChatAsync(chatKey);
            return new BuiltinToolResult(
                $"browser_act falhou ({(ex.Message.Length > 200 ? ex.Message[..200] : ex.Message)}). "
                + "A sessão foi reiniciada — observe a página e tente de novo.");
        }
        catch (InvalidOperationException ex)
        {
            return new BuiltinToolResult($"browser_act falhou: {ex.Message}");
        }
    }

    /// <summary>
    /// Tenta a ação nos alvos em ordem: índice observado → seletor CSS →
    /// coordenada → texto visível → OCR. Devolve resultado de erro
    /// (candidatos rankeados) ou null quando executou com sucesso.
    /// </summary>
    private async Task<BuiltinToolResult?> TryActAsync(
        IPage page, JsonElement args, string action, CancellationToken ct)
    {
        var index = Num(args, "index");
        var selector = Str(args, "selector");
        var text = Str(args, "text");
        var x = Num(args, "x");
        var y = Num(args, "y");

        // Ações sem alvo de elemento.
        if (action == "scroll")
        {
            await page.Mouse.WheelAsync(0, Num(args, "deltaY") ?? 600);
            return null;
        }
        if (action == "press" && index is null && selector is null && x is null)
        {
            var key = Str(args, "key") ?? "Enter";
            await page.Keyboard.PressAsync(key);
            return null;
        }
        if (x is not null && y is not null && action == ClickAction)
        {
            await page.Mouse.ClickAsync(x.Value, y.Value);
            return null;
        }

        // Locator por índice observado ou seletor CSS.
        ILocator? locator = null;
        var targetDesc = "";
        if (index is not null)
        {
            locator = page.Locator(BrowserElements.SelectorForIndex(index.Value)).First;
            targetDesc = $"[{index}]";
        }
        else if (!string.IsNullOrWhiteSpace(selector))
        {
            locator = page.Locator(selector).First;
            targetDesc = selector;
        }

        if (locator is not null && await RunOnLocatorAsync(locator, action, args))
        {
            return null; // sucesso
        }

        // Healing (a): texto visível fornecido pelo modelo.
        if (!string.IsNullOrWhiteSpace(text))
        {
            var byText = page.GetByText(text, new PageGetByTextOptions { Exact = false }).First;
            if (await RunOnLocatorAsync(byText, action, args))
            {
                return null;
            }
        }

        // Healing (b): OCR do último screenshot → clique por coordenada.
        if (action == ClickAction && !string.IsNullOrWhiteSpace(text)
            && Sessions.OcrEnabled && Ocr.IsAvailable
            && await OcrClickAsync(page, text, ct))
        {
            return null;
        }

        // Healing (c): candidatos rankeados — o modelo escolhe o índice.
        var json = await page.EvaluateAsync<JsonElement>(
            BrowserElements.ExtractScript, BrowserElements.MaxElements);
        var elements = BrowserElements.Parse(json);
        var candidates = BrowserElements.Rank(elements, text ?? selector ?? "");
        var hint = candidates.Count > 0
            ? "elements like: " + BrowserElements.Format(candidates)
            : "nenhum candidato visível — observe a página com browser_observe.";
        return new BuiltinToolResult(
            $"Não achei o alvo {targetDesc} para '{action}'. {hint}");
    }

    /// <summary>
    /// Healing (b) por OCR: print temporário → tesseract TSV → clique no centro
    /// do bounding box do texto. Depende do binário tesseract do host —
    /// indisponível em CI (coberto localmente quando tesseract existe).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private async Task<bool> OcrClickAsync(IPage page, string text, CancellationToken ct)
    {
        var tmp = Path.Join(Path.GetTempPath(), $"owui-ocr-{Guid.NewGuid():N}.png");
        try
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Png, Path = tmp,
            });
            var box = await Ocr.FindTextBoxAsync(tmp, text, ct);
            if (box is { } point)
            {
                await page.Mouse.ClickAsync(point.X, point.Y);
                return true;
            }
            return false;
        }
        finally
        {
            try { File.Delete(tmp); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <summary>Executa a ação no locator; false quando o alvo não existe.</summary>
    private static async Task<bool> RunOnLocatorAsync(
        ILocator locator, string action, JsonElement args)
    {
        try
        {
            switch (action)
            {
                case ClickAction:
                    await locator.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
                    break;
                case "fill":
                    await locator.FillAsync(Str(args, "value") ?? Str(args, "text") ?? "",
                        new LocatorFillOptions { Timeout = 8000 });
                    break;
                case "press":
                    await locator.PressAsync(Str(args, "key") ?? "Enter",
                        new LocatorPressOptions { Timeout = 8000 });
                    break;
                case "select":
                    await locator.SelectOptionAsync(Str(args, "value") ?? "",
                        new LocatorSelectOptionOptions { Timeout = 8000 });
                    break;
                default:
                    return false;
            }
            return true;
        }
        catch (PlaywrightException)
        {
            return false; // alvo não encontrado → self-healing
        }
    }
}
