using System.Text.Json;
using System.Text.RegularExpressions;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Estado vivo de uma tool_call + seu tool_result (SPEC chat-activity-feed):
/// compartilhado entre ChatView (eventos SSE e mensagens persistidas) e
/// RunActivityFeed (linhas amigáveis + detalhes via ToolCallCard).
/// </summary>
public sealed partial class ToolCallView
{
    /// <summary>Evento tool_call (id, nome, preview de args).</summary>
    public required RunToolCallEvent Call { get; init; }

    /// <summary>tool_result correspondente quando chega; null = ainda rodando.</summary>
    public RunToolResultEvent? Result { get; set; }

    /// <summary>
    /// Converte uma mensagem persistida role=tool em ToolCallView para o
    /// work log colapsado do histórico. O nome real vem do tool_calls da
    /// mensagem assistant quando resolvido pelo chamador; sem ele, preserva
    /// o fallback antigo para o ToolCallId. Para delegate_task, reidrata o
    /// <c>childChatId</c> do link "(chat filho: /c/{id})" gravado no texto
    /// — é o que permite o card embutir a conversa da subtarefa (padrão
    /// Devin web) também no histórico, onde o Result estruturado do SSE
    /// não foi persistido.
    /// </summary>
    public static ToolCallView FromPersisted(ChatMessageModel m, string? toolName = null)
    {
        var id = m.ToolCallId ?? "tool";
        var name = string.IsNullOrWhiteSpace(toolName) ? id : toolName;
        return new ToolCallView
        {
            Call = new RunToolCallEvent(id, name, null),
            Result = new RunToolResultEvent(
                id, name, true, m.Content,
                Result: DelegateResult(m, name)),
        };
    }

    /// <summary>
    /// Extrai o mapa id → nome das tool_calls persistidas nas mensagens
    /// assistant. JSON malformado ou fora do formato esperado é ignorado:
    /// o histórico antigo pode ter vindo de providers heterogêneos.
    /// </summary>
    public static IReadOnlyDictionary<string, string> PersistedToolNames(
        IEnumerable<ChatMessageModel> messages)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (string.IsNullOrWhiteSpace(message.ToolCallsJson))
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(message.ToolCallsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var call in doc.RootElement.EnumerateArray())
                {
                    if (call.ValueKind != JsonValueKind.Object
                        || !call.TryGetProperty("id", out var idEl)
                        || idEl.ValueKind != JsonValueKind.String
                        || !call.TryGetProperty("function", out var fn)
                        || fn.ValueKind != JsonValueKind.Object
                        || !fn.TryGetProperty("name", out var nameEl)
                        || nameEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }
                    var id = idEl.GetString();
                    var name = nameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    {
                        names[id] = name;
                    }
                }
            }
            catch (JsonException)
            {
                // tool_calls malformado — ignora apenas essa mensagem.
            }
        }
        return names;
    }

    private static JsonElement? DelegateResult(ChatMessageModel m, string name)
    {
        if (!name.EndsWith("delegate_task", StringComparison.Ordinal)
            || m.Content is null)
        {
            return null;
        }
        var match = ChildChatLink().Match(m.Content);
        return match.Success
            ? JsonSerializer.SerializeToElement(new { childChatId = match.Groups[1].Value })
            : null;
    }

    [GeneratedRegex(@"/c/([A-Za-z0-9_-]+)")]
    private static partial Regex ChildChatLink();
}
