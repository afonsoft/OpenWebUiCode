using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:context_notes</c> — notebook persistente do chat
/// (SPEC-20261013-agent-context-management RF-003): sem <c>text</c>
/// devolve o conteúdo; com <c>text</c> sobrescreve (replace, não append —
/// o modelo reescreve o estado). Cap de 8k chars; injetado no contexto de
/// toda run e sobrevive a compactação e reload. Estado interno — sem
/// aprovação.
/// </summary>
public sealed class ContextNotesBuiltinTool(ContextBoundaryService boundaries) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "context_notes";

    /// <inheritdoc />
    public string Description =>
        "Persistent context notebook for this chat — survives compaction "
            + "and reload. Call without 'text' to read it; with 'text' to "
            + "REWRITE it entirely (keep it dense: goals, decisions, "
            + "constraints worth carrying across context windows).";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "text": {
              "type": "string",
              "description": "New full notebook content (replace). Omit to read."
            }
          }
        }
        """;

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
        if (string.IsNullOrEmpty(context.ChatId))
        {
            return new BuiltinToolResult("context_notes requer chat persistido — indisponível no playground.");
        }

        if (!args.TryGetProperty("text", out var textEl)
            || textEl.ValueKind != JsonValueKind.String)
        {
            var notes = await boundaries.GetNotesAsync(context.ChatId, ct);
            return new BuiltinToolResult(string.IsNullOrEmpty(notes) ? "(notebook vazio)" : notes);
        }

        var text = textEl.GetString() ?? "";
        return await boundaries.SetNotesAsync(context.ChatId, text, ct)
            ? new BuiltinToolResult($"Notebook atualizado ({text.Length} chars) — injetado na próxima janela.")
            : new BuiltinToolResult(
                $"Notebook excede o cap de {ContextBoundaryService.MaxNoteChars} caracteres — resuma e tente de novo.");
    }
}
