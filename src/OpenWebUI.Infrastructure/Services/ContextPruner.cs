using System.Text.Json;
using System.Text.RegularExpressions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Poda mecânica de contexto (SPEC-20261014-context-maintenance-v2 RF-001):
/// reescreve a <b>projeção</b> da janela enviada ao modelo — as mensagens
/// persistidas nunca são tocadas. Três regras, nenhuma chamada de LLM:
/// (a) tool result de escrita cujo <c>path</c> foi reescrito por chamada
/// posterior → elidido; (b) resultado <c>ok</c> sem payload → marcador
/// curto; (c) resultado grande (&gt;4KB) → marcador apontando para
/// <c>history://</c> (RF-003) para recuperação sob demanda.
/// </summary>
public static class ContextPruner
{
    /// <summary>Tamanho máximo de um tool result antes de virar marcador (RF-001c).</summary>
    public const int ElideBytes = 4096;

    /// <summary>Marcador estável para resultado "ok" sem payload (RF-001b).</summary>
    public const string OkMarker = "[ok — resultado sem conteúdo elidido]";

    /// <summary>Tools cujo argumento <c>path</c> reescreve um alvo (supersession, RF-001a).</summary>
    private static readonly HashSet<string> s_writeTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_write", "apply_patch", "file_edit", "file_append",
    };

    /// <summary>Acks curtos sem payload útil (cobertos por RF-001b).</summary>
    private static readonly Regex s_ack = new(
        "^(ok|done|success|sucesso|conclu[ií]do)[.!]?$|^\\{\\s*\"ok\"\\s*:\\s*true\\s*\\}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexDefaults.MatchTimeout);

    /// <summary>
    /// Devolve a janela podada (nova lista — nunca muta a entrada): aplica
    /// as três regras de RF-001 sobre mensagens <c>tool</c> apenas.
    /// </summary>
    public static List<ChatCompletionMessage> Prune(
        IReadOnlyList<ChatCompletionMessage> messages, string chatId)
    {
        // Mapeia tool_call_id → path reescrito, para parear resultados às
        // chamadas mesmo quando o cliente não envia ToolCallId.
        var calls = CollectCalls(messages);
        var lastWrite = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < calls.Count; i++)
        {
            if (calls[i].Path is { } p)
            {
                lastWrite[p] = i;
            }
        }

        var result = new List<ChatCompletionMessage>(messages.Count);
        var resultIndex = 0;
        foreach (var m in messages)
        {
            if (m.Role == "tool")
            {
                var callIdx = ResolveCallIndex(m, calls, resultIndex++);
                var call = callIdx >= 0 ? calls[callIdx] : null;
                var content = m.Content ?? string.Empty;

                if (call?.Path is { } path && callIdx < lastWrite.GetValueOrDefault(path, -1))
                {
                    result.Add(m with
                    {
                        Content = $"[resultado elidido — '{path}' reescrito por chamada posterior]",
                    });
                    continue;
                }
                if (content.Length <= 120 && s_ack.IsMatch(content.Trim()))
                {
                    result.Add(m with { Content = OkMarker });
                    continue;
                }
                if (content.Length > ElideBytes)
                {
                    var pointer = call?.Id is { } id
                        ? $"history://{chatId}/call:{id}"
                        : $"history://{chatId}";
                    result.Add(m with
                    {
                        Content = $"[resultado de {content.Length} bytes elidido — "
                            + $"recupere com file_read {pointer}]",
                    });
                    continue;
                }
            }
            result.Add(m);
        }
        return result;
    }

    private sealed record CallRef(string? Id, string? Path);

    /// <summary>Coleta chamadas em ordem (id + alvo de escrita quando houver).</summary>
    private static List<CallRef> CollectCalls(IReadOnlyList<ChatCompletionMessage> messages)
    {
        var calls = new List<CallRef>();
        foreach (var m in messages.Where(m => m.ToolCallsJson is not null))
        {
            try
            {
                foreach (var el in JsonDocument.Parse(m.ToolCallsJson).RootElement.EnumerateArray())
                {
                    var id = el.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var fn = el.TryGetProperty("function", out var f) ? f : default;
                    var name = fn.ValueKind == JsonValueKind.Object
                        && fn.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? path = null;
                    if (name is not null && s_writeTools.Contains(name)
                        && fn.TryGetProperty("arguments", out var a))
                    {
                        // arguments pode vir como string (padrão), objeto
                        // embutido ou string "double-encoded" (Gemini) —
                        // GetString()/TryGetProperty no kind errado lançava
                        // InvalidOperationException e derrubava a run.
                        var argsJson = a.ValueKind switch
                        {
                            JsonValueKind.String => a.GetString(),
                            JsonValueKind.Object => a.GetRawText(),
                            _ => null,
                        };
                        if (argsJson is not null)
                        {
                            try
                            {
                                var args = BuiltinToolRegistry.NormalizeArgs(argsJson);
                                path = args.ValueKind == JsonValueKind.Object
                                    && args.TryGetProperty("path", out var p)
                                        ? p.GetString() : null;
                            }
                            catch (JsonException)
                            {
                                // arguments malformado — ignora o alvo.
                            }
                        }
                    }
                    calls.Add(new CallRef(id, path));
                }
            }
            catch (JsonException)
            {
                // tool_calls malformado — ignora o bloco inteiro.
            }
        }
        return calls;
    }

    /// <summary>
    /// Índice da chamada que gerou o tool result: por <c>ToolCallId</c>
    /// quando presente; senão posicional — o n-ésimo result casa com a
    /// n-ésima chamada (results seguem a ordem dos calls do assistant).
    /// </summary>
    private static int ResolveCallIndex(
        ChatCompletionMessage msg, List<CallRef> calls, int resultIdx)
    {
        if (msg.ToolCallId is { } id)
        {
            return calls.FindIndex(c => c.Id == id);
        }
        return resultIdx < calls.Count ? resultIdx : -1;
    }
}
