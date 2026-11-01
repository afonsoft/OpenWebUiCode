using System.Text.Json;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Executor de hooks <c>prompt</c> (SPEC-20261031-lifecycle-hooks
/// RF-002): envia o evento a um modelo rápido (papel <c>advisor</c>,
/// configurável via <c>hooks.promptModel</c>) com instrução fixa e
/// interpreta <c>approve|block|ask</c>, <c>true/false</c> ou
/// <c>{"pass":bool}</c>. Resposta não-parseável = não-bloqueante; o
/// prompt do hook nunca chega ao modelo principal.
/// </summary>
public sealed class PromptHookEvaluator(
    ModelRoleResolver roles,
    ConfigService config,
    Func<ChatCompletionRequest, string?, CancellationToken, Task<ProviderCompletion>> complete)
{
    private const string Instruction = """
        Você é um hook de política de um agente. Avalie o evento JSON e
        responda SOMENTE uma palavra: "approve" (segue), "block" (bloqueia
        com razão na segunda linha) ou "ask" (pede aprovação humana).
        Regra do hook: {rule}
        """;

    /// <summary>Avalia o hook prompt; falha/modelo ausente = approve.</summary>
    public async Task<HookOutcome> EvaluateAsync(
        HookDefinition hook, JsonElement payload, ChatRun run,
        CancellationToken ct)
    {
        try
        {
            var model = await config.GetAsync("hooks.promptModel", (string?)null, ct)
                ?? await roles.ResolveAsync("advisor", run.UserId, run.Model, ct);
            var messages = new List<ChatCompletionMessage>
            {
                new("system", Instruction.Replace("{rule}", hook.Command)),
                new("user", payload.GetRawText()),
            };
            var step = await complete(
                new ChatCompletionRequest(model, messages, Stream: false),
                run.UserId, ct);
            return Parse(step.Content ?? string.Empty, hook);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return HookOutcome.ApproveNone;
        }
    }

    /// <summary>Parse tolerante da resposta do modelo.</summary>
    internal static HookOutcome Parse(string content, HookDefinition hook)
    {
        var text = content.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("pass", out var p)
                    && p.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return new HookOutcome(
                        p.GetBoolean() ? HookDecision.Approve : HookDecision.Block,
                        null, p.GetBoolean() ? null : hook.Source, null, []);
                }
            }
            catch (JsonException)
            {
                // cai no parse textual abaixo
            }
        }
        var first = text.Split('\n', 2);
        var decision = first[0].Trim().ToLowerInvariant();
        var reason = first.Length > 1 ? first[1].Trim() : null;
        return decision switch
        {
            "block" or "false" => new HookOutcome(
                HookDecision.Block, reason, hook.Source, null, []),
            "ask" => new HookOutcome(HookDecision.Ask, reason, hook.Source, null, []),
            "approve" or "true" => HookOutcome.ApproveNone,
            _ => HookOutcome.ApproveNone,
        };
    }
}
