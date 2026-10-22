using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Infrastructure.Runs;

/// <summary>
/// Resultado da waterfall <c>pre-step</c>: mensagens finais admitidas +
/// decisões tomadas (para o emissor registrar <c>interceptor.veto</c>).
/// </summary>
/// <param name="Messages">Mensagens que entram no histórico (vazio quando rejeitado).</param>
/// <param name="Rejected">Se algum interceptor rejeitou a admissão.</param>
/// <param name="VetoedBy">Nome do interceptor que decidiu (reject/veto).</param>
/// <param name="Reason">Motivo declarado.</param>
public sealed record PreStepOutcome(
    IReadOnlyList<ChatCompletionMessage> Messages,
    bool Rejected,
    string? VetoedBy,
    string? Reason);

/// <summary>
/// Resultado da waterfall <c>tool.call</c>: passar, args reescritos ou veto.
/// </summary>
/// <param name="Vetoed">Execução negada.</param>
/// <param name="ArgumentsJson">Args finais (rewrite) — null = originais.</param>
/// <param name="VetoedBy">Interceptor que vetou/rewrite final.</param>
/// <param name="Reason">Motivo do veto (vai ao modelo como erro).</param>
public sealed record ToolCallOutcome(
    bool Vetoed,
    string? ArgumentsJson,
    string? VetoedBy,
    string? Reason);

/// <summary>
/// Executa os <see cref="IRunInterceptor"/> em waterfall ordenada
/// (SPEC-20261021-harness-seams RF-A2). Fail-closed por ponto: em
/// <c>pre-step</c> timeout/exceção deixa as mensagens atuais passarem e
/// loga; em <c>tool.call</c> timeout/exceção veta a execução. Sem
/// interceptors registrados as chamadas são no-op baratas.
/// </summary>
public sealed class RunInterceptorRunner(
    IEnumerable<IRunInterceptor> interceptors,
    IConfiguration configuration,
    ILogger<RunInterceptorRunner> logger)
{
    private readonly List<IRunInterceptor> _interceptors =
        [.. interceptors.OrderBy(i => i.Order)];

    /// <summary>Timeout por interceptor (<c>interceptors.timeout_ms</c>, default 5s).</summary>
    public int TimeoutMs { get; } =
        int.TryParse(configuration["interceptors.timeout_ms"], out var ms)
            ? Math.Max(100, ms) : 5000;

    /// <summary>Se há interceptors — permite pular o ponto inteiro.</summary>
    public bool HasInterceptors => _interceptors.Count > 0;

    /// <summary>
    /// Waterfall <c>pre-step</c>: cada interceptor vê as mensagens já
    /// reescritas pelos anteriores; o primeiro <c>Reject</c> descarta a
    /// admissão e encerra a cadeia.
    /// </summary>
    public async Task<PreStepOutcome> RunPreStepAsync(
        PreStepContext ctx, CancellationToken ct)
    {
        var current = ctx.Claimed;
        foreach (var interceptor in _interceptors)
        {
            PreStepDecision decision;
            try
            {
                using var timeout = CancellationTokenSource
                    .CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeoutMs);
                decision = await interceptor.OnPreStepAsync(
                    ctx with { Claimed = current }, timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // fail-open no pre-step: passa o estado atual e loga.
                logger.LogWarning(ex,
                    "Interceptor {Type} falhou em pre-step da run {RunId} — passando inalterado.",
                    interceptor.GetType().Name, ctx.RunId);
                continue;
            }

            switch (decision.Action)
            {
                case PreStepAction.Reject:
                    return new PreStepOutcome(
                        [], true, interceptor.GetType().Name, decision.Reason);
                case PreStepAction.Rewrite:
                    current = decision.Messages ?? current;
                    break;
            }
        }
        return new PreStepOutcome(current, false, null, null);
    }

    /// <summary>
    /// Waterfall <c>tool.call</c>: rewrite encadeia o args para o próximo;
    /// o primeiro <c>Veto</c> encerra negando. Timeout/exceção = veto
    /// conservador (fail-closed).
    /// </summary>
    public async Task<ToolCallOutcome> RunToolCallAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var args = ctx.ArgumentsJson;
        foreach (var interceptor in _interceptors)
        {
            ToolCallDecision decision;
            try
            {
                using var timeout = CancellationTokenSource
                    .CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeoutMs);
                decision = await interceptor.OnToolCallAsync(
                    ctx with { ArgumentsJson = args }, timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Interceptor {Type} falhou em tool.call {Tool} da run {RunId} — veto conservador.",
                    interceptor.GetType().Name, ctx.ToolName, ctx.RunId);
                return new ToolCallOutcome(
                    true, null, interceptor.GetType().Name,
                    "Execução negada: interceptor interno falhou.");
            }

            switch (decision.Action)
            {
                case ToolCallAction.Veto:
                    return new ToolCallOutcome(
                        true, null, interceptor.GetType().Name, decision.Reason);
                case ToolCallAction.Rewrite when decision.ArgumentsJson is not null:
                    args = decision.ArgumentsJson;
                    break;
            }
        }
        return new ToolCallOutcome(
            false, args == ctx.ArgumentsJson ? null : args, null, null);
    }
}
