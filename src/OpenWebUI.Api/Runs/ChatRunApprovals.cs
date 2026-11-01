using System.Collections.Concurrent;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Api.Runs;

/// <summary>
/// Gate de aprovação de tools (SPEC-20261007-chat-tool-streaming RF-003):
/// a run pausa num <see cref="TaskCompletionSource{TResult}"/> por
/// <c>runId:callId</c> até o dono decidir via endpoint
/// (<see cref="ResolveAsync"/>), o timeout expirar (deny) ou a run ser
/// interrompida (<see cref="Cancel"/>). <c>remember</c> vale só para a
/// conversa e só em memória — um restart devolve a run a interrupted de
/// qualquer forma. Desde a SPEC-20261009-agent-modes-plan-build RF-005 a
/// allowlist guarda por (chatId, toolName) o padrão de argumentos
/// aprovado (<see cref="PermissionRuleset.ArgPattern"/>), não só a tool
/// — "aprova sempre" cobre a mesma classe de ação, não qualquer call.
/// </summary>
public sealed class ChatRunApprovals
{
    /// <summary>Tempo máximo esperando uma decisão antes de negar.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>Resultado da espera: aprovado, ou negado com mensagem.</summary>
    public sealed record ApprovalResult(bool Approved, string? Message);

    private sealed record Pending(
        string ChatId,
        string ToolName,
        string ArgPattern,
        TaskCompletionSource<ApprovalResult> Completion,
        object? AskedPayload);

    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    // chatId → toolName → padrões de args aprovados "sempre nesta sessão".
    private readonly ConcurrentDictionary<
        string, ConcurrentDictionary<string, ConcurrentDictionary<string, bool>>> _remembered =
        new(StringComparer.Ordinal);

    private static string Key(string runId, string callId) => $"{runId}:{callId}";

    /// <summary>
    /// Se o dono já aprovou <paramref name="toolName"/> com remember neste
    /// chat cobrindo <paramref name="signature"/> — qualquer padrão
    /// guardado que casa por glob; sem assinatura, basta a tool lembrada.
    /// </summary>
    public bool IsRemembered(string chatId, string toolName, string signature = "*") =>
        _remembered.TryGetValue(chatId, out var tools)
        && tools.TryGetValue(toolName, out var patterns)
        && (patterns.ContainsKey("*")
            || patterns.Keys.Any(p => WorkspaceFiles.GlobToRegex(p).IsMatch(signature)));

    /// <summary>Se há uma aprovação pendente nesta run/call.</summary>
    public bool IsPending(string runId, string callId) =>
        _pending.ContainsKey(Key(runId, callId));

    /// <summary>
    /// Nome da tool da call pendente (para persistir override no
    /// "aprova sempre"), null quando não há pendência.
    /// </summary>
    public Task<string?> PendingToolNameAsync(string runId, string callId) =>
        Task.FromResult(_pending.TryGetValue(Key(runId, callId), out var p)
            ? p.ToolName
            : null);

    /// <summary>
    /// Tool + padrão de argumentos da call pendente — usado para gravar a
    /// <c>SavedApproval</c> durável quando o dono decide com "sempre"
    /// (SPEC-20261018-permissions-v2 RF-002).
    /// </summary>
    public Task<(string ToolName, string ArgPattern)?> PendingInfoAsync(
        string runId, string callId) =>
        Task.FromResult(_pending.TryGetValue(Key(runId, callId), out var p)
            ? (p.ToolName, p.ArgPattern)
            : ((string, string)?)null);

    /// <summary>
    /// Payload do evento <c>approval_asked</c>/<c>question_asked</c> da call
    /// pendente nesta run — permite ao GET da run devolver a pendência a
    /// clientes cujo attach SSE morreu antes do evento (proxies que
    /// derrubam/bufferizam o stream), para o card de aprovação aparecer
    /// via polling em vez de exigir F5.
    /// </summary>
    public object? PendingAsked(string runId) =>
        _pending.Where(kv => kv.Key.StartsWith(runId + ":", StringComparison.Ordinal))
            .Select(kv => kv.Value.AskedPayload)
            .FirstOrDefault(p => p is not null);

    /// <summary>
    /// Chats com ao menos uma call bloqueada esperando decisão do dono
    /// (aprovação, pergunta ask_user ou plan_exit) — snapshot usado pelo
    /// filtro <c>attention</c> da lista de chats (SPEC-20261009-attention-inbox).
    /// </summary>
    public HashSet<string> PendingChatIds() =>
        _pending.Values.Select(p => p.ChatId).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Registra a pendência e espera a decisão. Resolve aprovado, ou negado
    /// (decisão — com a mensagem de instrução opcional —, timeout ou
    /// cancelamento da run).
    /// </summary>
    /// <param name="argPattern">
    /// Padrão de argumentos lembrado quando o dono aprova com remember
    /// (SPEC RF-005) — <c>*</c> cobre a tool inteira.
    /// </param>
    public async Task<ApprovalResult> WaitAsync(
        string runId, string chatId, string callId, string toolName,
        CancellationToken ct, string argPattern = "*",
        object? askedPayload = null)
    {
        var pending = new Pending(chatId, toolName, argPattern,
            new TaskCompletionSource<ApprovalResult>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            askedPayload);
        if (!_pending.TryAdd(Key(runId, callId), pending))
        {
            return new ApprovalResult(false, null);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await Task.WhenAny(pending.Completion.Task,
                Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, timeout.Token));
            return pending.Completion.Task is { IsCompletedSuccessfully: true } task
                ? task.Result
                : new ApprovalResult(false, null);
        }
        catch (OperationCanceledException)
        {
            return new ApprovalResult(false, null);
        }
        finally
        {
            _pending.TryRemove(Key(runId, callId), out _);
        }
    }

    /// <summary>
    /// Aplica a decisão do dono. Retorna false quando a call não está
    /// pendente (já decidida, expirada ou inexistente → endpoint 404).
    /// </summary>
    public bool Resolve(
        string runId, string chatId, string callId,
        bool approved, bool remember, string? message = null)
    {
        if (!_pending.TryGetValue(Key(runId, callId), out var pending)
            || pending.ChatId != chatId)
        {
            return false;
        }

        if (remember && approved)
        {
            _remembered.GetOrAdd(chatId, _ => new ConcurrentDictionary<
                    string, ConcurrentDictionary<string, bool>>(StringComparer.Ordinal))
                .GetOrAdd(pending.ToolName, _ => new ConcurrentDictionary<string, bool>(
                    StringComparer.Ordinal))
                [pending.ArgPattern] = true;
        }

        return pending.Completion.TrySetResult(new ApprovalResult(approved, message));
    }

    /// <summary>Nega todas as aprovações pendentes da run (stop/shutdown).</summary>
    public void Cancel(string runId)
    {
        foreach (var (key, pending) in _pending)
        {
            if (key.StartsWith($"{runId}:", StringComparison.Ordinal))
            {
                pending.Completion.TrySetResult(new ApprovalResult(false, null));
            }
        }
    }
}
