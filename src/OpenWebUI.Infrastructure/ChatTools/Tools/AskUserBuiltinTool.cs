using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:ask_user</c> — pergunta estruturada ao dono da conversa
/// (SPEC-20261007-chat-agent-ux RF-005, padrão do Question do opencode):
/// a run pausa num prompt com opções + resposta livre; a resposta volta
/// como resultado da tool. Implementado como <see cref="RequiresApproval"/>
/// — o gate do executor emite o evento <c>question_asked</c> e devolve a
/// resposta via <c>ToolGateDecision.Output</c>, sem executar
/// <see cref="ExecuteAsync"/> (que só roda se a run chegar aqui sem gate —
/// defesa).
/// </summary>
public sealed class AskUserBuiltinTool : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "ask_user";

    /// <inheritdoc />
    public string Description =>
        "Ask the user — call when you need a decision or detail.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "question": { "type": "string", "description": "The question to ask." },
            "options": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Clickable options (optional; omit = free-text answer)."
            },
            "multiple": {
              "type": "boolean",
              "description": "Allow picking more than one option (optional)."
            }
          },
          "required": ["question"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct) =>
        // O gate do executor intercepta ask_user e devolve a resposta via
        // ToolGateDecision.Output — chegar aqui significa execução sem o
        // fluxo de chat (ex.: tool isolada), onde não há quem responda.
        Task.FromResult(new BuiltinToolResult(
            "Erro: ask_user só pode responder dentro do fluxo de chat."));
}
