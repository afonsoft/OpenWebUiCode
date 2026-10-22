using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Application.Interfaces;

/// <summary>
/// Contexto do interceptor <c>pre-step</c>
/// (SPEC-20261021-harness-seams RF-A2): as mensagens "claimed" na fronteira
/// de rodada (steer/queue promovidos, notas de contexto) antes de entrarem
/// no histórico enviado ao provider.
/// </summary>
/// <param name="RunId">Run em execução.</param>
/// <param name="ChatId">Chat da run.</param>
/// <param name="UserId">Dono da run.</param>
/// <param name="Step">Step/round corrente (1-based; 0 = fronteira inicial).</param>
/// <param name="Claimed">Mensagens admitidas nesta fronteira (mutável pela waterfall).</param>
public sealed record PreStepContext(
    string RunId,
    string? ChatId,
    string? UserId,
    int Step,
    IReadOnlyList<ChatCompletionMessage> Claimed);

/// <summary>
/// Decisão do interceptor <c>pre-step</c>: passar inalterado, reescrever as
/// mensagens claimed ou rejeitar a admissão (as mensagens não entram no
/// histórico; a run segue).
/// </summary>
public sealed record PreStepDecision
{
    private PreStepDecision(
        PreStepAction action,
        IReadOnlyList<ChatCompletionMessage>? messages,
        string? reason)
    {
        Action = action;
        Messages = messages;
        Reason = reason;
    }

    /// <summary>Ação decidida.</summary>
    public PreStepAction Action { get; }

    /// <summary>Mensagens substitutas quando <see cref="PreStepAction.Rewrite"/>.</summary>
    public IReadOnlyList<ChatCompletionMessage>? Messages { get; }

    /// <summary>Motivo quando <see cref="PreStepAction.Reject"/> (vai ao log/evento).</summary>
    public string? Reason { get; }

    /// <summary>Passa inalterado.</summary>
    public static readonly PreStepDecision Pass = new(PreStepAction.Pass, null, null);

    /// <summary>Substitui as mensagens claimed.</summary>
    public static PreStepDecision Rewrite(IReadOnlyList<ChatCompletionMessage> messages) =>
        new(PreStepAction.Rewrite, messages, null);

    /// <summary>Recusa a admissão das mensagens nesta fronteira.</summary>
    public static PreStepDecision Reject(string reason) =>
        new(PreStepAction.Reject, null, reason);
}

/// <summary>Ação do <see cref="PreStepDecision"/>.</summary>
public enum PreStepAction
{
    /// <summary>Segue para o próximo interceptor/admissão.</summary>
    Pass,
    /// <summary>Mensagens claimed substituídas.</summary>
    Rewrite,
    /// <summary>Admissão recusada — mensagens descartadas.</summary>
    Reject,
}

/// <summary>
/// Contexto do interceptor <c>tool.call</c>: a chamada de tool antes do
/// gate de aprovação/execução. <see cref="ArgumentsJson"/> já passou pelo
/// <c>SecretScrubber</c> quando exibido — interceptors nunca veem secrets.
/// </summary>
/// <param name="RunId">Run em execução.</param>
/// <param name="ChatId">Chat da run.</param>
/// <param name="UserId">Dono da run.</param>
/// <param name="ToolName">Nome da função chamada (forma do spec).</param>
/// <param name="ArgumentsJson">Argumentos JSON pedidos pelo modelo.</param>
public sealed record ToolCallContext(
    string RunId,
    string? ChatId,
    string? UserId,
    string ToolName,
    string ArgumentsJson);

/// <summary>
/// Decisão do interceptor <c>tool.call</c>: passar, reescrever os
/// argumentos ou vetar a execução (o modelo recebe o motivo como resultado
/// de erro — análogo a uma negação do gate).
/// </summary>
public sealed record ToolCallDecision
{
    private ToolCallDecision(
        ToolCallAction action, string? argumentsJson, string? reason)
    {
        Action = action;
        ArgumentsJson = argumentsJson;
        Reason = reason;
    }

    /// <summary>Ação decidida.</summary>
    public ToolCallAction Action { get; }

    /// <summary>Args substitutos quando <see cref="ToolCallAction.Rewrite"/>.</summary>
    public string? ArgumentsJson { get; }

    /// <summary>Motivo quando <see cref="ToolCallAction.Veto"/>.</summary>
    public string? Reason { get; }

    /// <summary>Passa inalterado.</summary>
    public static readonly ToolCallDecision Pass = new(ToolCallAction.Pass, null, null);

    /// <summary>Substitui os argumentos da chamada.</summary>
    public static ToolCallDecision Rewrite(string argumentsJson) =>
        new(ToolCallAction.Rewrite, argumentsJson, null);

    /// <summary>Nega a execução — o modelo recebe <paramref name="reason"/> como erro.</summary>
    public static ToolCallDecision Veto(string reason) =>
        new(ToolCallAction.Veto, null, reason);
}

/// <summary>Ação do <see cref="ToolCallDecision"/>.</summary>
public enum ToolCallAction
{
    /// <summary>Segue para o próximo interceptor/gate.</summary>
    Pass,
    /// <summary>Argumentos substituídos para os próximos e para a execução.</summary>
    Rewrite,
    /// <summary>Execução negada (fail-closed).</summary>
    Veto,
}

/// <summary>
/// Interceptor mutável do ciclo de vida de uma run
/// (SPEC-20261021-harness-seams RF-A2): hooks waterfall executados em
/// ordem crescente de <see cref="Order"/> nos pontos <c>pre-step</c>
/// (mensagens claimed na fronteira de rodada) e <c>tool.call</c> (antes do
/// gate de aprovação). Fail-closed: timeout (<c>interceptors.timeout_ms</c>,
/// default 5s) ou exceção — em <c>pre-step</c> deixa passar inalterado e
/// loga; em <c>tool.call</c> nega a execução. Registrado via DI
/// (<see cref="System.Collections.Generic.IEnumerable{T}"/>); sem
/// interceptors o comportamento é o atual.
/// </summary>
public interface IRunInterceptor
{
    /// <summary>Ordem de execução na waterfall (menor primeiro).</summary>
    int Order { get; }

    /// <summary>
    /// Hook <c>pre-step</c> — decide sobre as mensagens claimed. Default
    /// passa inalterado (implementar só o que interessa).
    /// </summary>
    Task<PreStepDecision> OnPreStepAsync(PreStepContext ctx, CancellationToken ct) =>
        Task.FromResult(PreStepDecision.Pass);

    /// <summary>
    /// Hook <c>tool.call</c> — decide sobre a execução da tool. Default
    /// passa inalterado.
    /// </summary>
    Task<ToolCallDecision> OnToolCallAsync(ToolCallContext ctx, CancellationToken ct) =>
        Task.FromResult(ToolCallDecision.Pass);
}
