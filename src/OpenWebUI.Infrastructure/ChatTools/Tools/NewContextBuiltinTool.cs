using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:new_context</c> — pedido do agente por nova janela de
/// contexto (SPEC-20261013-agent-context-management RF-004): levanta uma
/// flag consumida na próxima fronteira de round, que força a compactação
/// já existente — o executor nunca chama o provider de dentro da tool.
/// Estado interno — sem aprovação.
/// </summary>
public sealed class NewContextBuiltinTool(ContextBoundaryService boundaries) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "new_context";

    /// <inheritdoc />
    public string Description =>
        "Request a fresh context window — schedule compaction for the next "
            + "round boundary. Call when the history grew stale/noisy; write "
            + "what must survive into context_notes first.";

    /// <inheritdoc />
    public string ParametersJson => """{ "type": "object", "properties": {} }""";

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (!await boundaries.EnabledAsync(ct))
        {
            return new BuiltinToolResult("Tools de contexto estão desabilitadas pelo administrador.");
        }
        if (string.IsNullOrEmpty(context.RunId) || string.IsNullOrEmpty(context.ChatId))
        {
            return new BuiltinToolResult("new_context requer run em chat persistido — indisponível no playground.");
        }

        await boundaries.RequestNewContextAsync(context.RunId, ct);
        return new BuiltinToolResult(
            "Nova janela de contexto agendada — a compactação roda na próxima fronteira de round.");
    }
}
