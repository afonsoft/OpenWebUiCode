using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:mode_switch</c> — troca o modo do agente no meio da conversa
/// (SPEC-20261122-mode-transitions RF-003): <c>chat→plan</c> quando o pedido
/// pede planejamento/SPEC, <c>chat→build</c> quando pede implementação
/// direta, e a desescalação <c>build→chat</c>/<c>build→plan</c>. O caminho
/// <c>plan→build</c> é recusado — exige a aprovação humana do
/// <c>builtin_plan_exit</c>. Persiste <c>chat.Mode</c> + preset de entrada
/// (RF-005); o executor detecta a mudança na próxima call e publica o
/// evento <c>mode</c> (o chip do cliente atualiza ao vivo). Em plan a tool
/// não é anunciada — <c>plan_exit</c> é a saída correta.
/// </summary>
public sealed class ModeSwitchBuiltinTool(IServiceScopeFactory scopes)
    : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "mode_switch";

    /// <inheritdoc />
    public string Description =>
        "Switch the agent mode — 'plan' when the user wants planning/a spec "
            + "first, 'build' when they asked to implement/code directly, "
            + "'chat' to go back to plain conversation. The switch is "
            + "immediate — proceed in the new mode right away. From plan "
            + "mode, reaching build requires the plan_exit approval — this "
            + "tool refuses that path.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "mode": {
              "type": "string",
              "enum": ["plan", "build", "chat"],
              "description": "Target agent mode."
            },
            "reason": {
              "type": "string",
              "description": "Optional short reason logged with the switch."
            }
          },
          "required": ["mode"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var target = args.TryGetProperty("mode", out var m)
            && m.ValueKind == JsonValueKind.String
                ? m.GetString()?.Trim().ToLowerInvariant()
                : null;
        if (target is not ("plan" or "build" or "chat"))
        {
            return new BuiltinToolResult(
                "Parâmetro 'mode' inválido — use plan | build | chat.");
        }
        if (context.ChatId is null)
        {
            return new BuiltinToolResult(
                "mode_switch precisa de um chat — indisponível neste contexto.");
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
        var chat = await db.Chats
            .FirstOrDefaultAsync(
                c => c.Id == context.ChatId && c.UserId == context.UserId, ct);
        if (chat is null)
        {
            return new BuiltinToolResult("Chat não encontrado.");
        }

        var current = PermissionRuleset.Normalize(chat.Mode);
        if (current == target)
        {
            return new BuiltinToolResult(
                $"A conversa já está no modo {target} — prossiga.");
        }
        if (current == "plan" && target == "build")
        {
            return new BuiltinToolResult(
                "plan→build exige aprovação do plano — chame "
                    + "builtin_plan_exit (o dono decide se executa).");
        }

        chat.Mode = target;
        var presetBumped = await ChatModeTransitions.ApplyEntryPresetAsync(
            config, chat, target, ct);
        await db.SaveChangesAsync(ct);

        return new BuiltinToolResult(
            $"Modo alterado para '{target}' — prossiga já neste modo."
                + (presetBumped
                    ? " O preset de aprovação subiu para 'smart'."
                    : string.Empty),
            new { mode = target, previous = current, preset = chat.ApprovalPreset });
    }
}
