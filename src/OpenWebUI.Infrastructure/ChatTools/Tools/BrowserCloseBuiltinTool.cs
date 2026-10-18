using System.Text.Json;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Infrastructure.ChatTools.Browser;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:browser_close</c> — fecha o contexto de browser do chat
/// (libera cookies/storage da sessão). SPEC-20261017-browser-visual-reasoning.
/// </summary>
public sealed class BrowserCloseBuiltinTool(
    IBrowserSessions sessions,
    TesseractOcr ocr,
    ConfigService config,
    IConfiguration configuration,
    AppDbContext db)
    : BrowserSessionToolBase(sessions, ocr, config, configuration, db)
{
    /// <inheritdoc />
    public override string Name => "browser_close";

    /// <inheritdoc />
    public override string Description =>
        "Close the chat's browser session (drops the isolated context).";

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
        await Sessions.CloseChatAsync(ChatKey(context));
        return new BuiltinToolResult(
            "Sessão de browser do chat encerrada.", new { closed = true });
    }
}
