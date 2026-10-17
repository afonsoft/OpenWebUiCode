using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Motivo classificado de uma falha transitória de completion.</summary>
public enum ProviderRetryReason
{
    /// <summary>429 (ou 408/409): respeita <c>Retry-After</c> quando presente.</summary>
    RateLimit,
    /// <summary>5xx do provedor.</summary>
    ServerError,
    /// <summary>Timeout de transporte/conexão (não cancelamento do usuário).</summary>
    Timeout,
    /// <summary>Falha de transporte sem status (DNS, conexão recusada, stream morto).</summary>
    Transport,
    /// <summary>Provider rejeitou por janela de contexto — caminho compact→retry.</summary>
    ContextOverflow,
    /// <summary>Erro permanente — nunca retenta (4xx fora da lista).</summary>
    NonRetryable,
}

/// <summary>Decisão de uma tentativa: retentar, esperar quanto e por quê.</summary>
public sealed record RetryDecision(
    bool Retry, TimeSpan Delay, ProviderRetryReason Reason);

/// <summary>
/// Limites da política de retry — configuráveis via
/// <c>Providers:MaxAttempts</c> / <c>Providers:MaxConsecutiveTimeouts</c>
/// (<c>Providers__MaxAttempts</c> por env). Testes sem provider real usam
/// <c>MaxAttempts=1</c> para falhar rápido em vez de gastar o orçamento
/// de retries contra um endpoint inalcançável.
/// </summary>
public sealed record RetryPolicyOptions(int MaxAttempts, int MaxConsecutiveTimeouts)
{
    /// <summary>Padrão de produção: 6 tentativas, 3 timeouts seguidos.</summary>
    public static RetryPolicyOptions Default { get; } =
        new(ProviderRetryPolicy.MaxAttempts, ProviderRetryPolicy.MaxConsecutiveTimeouts);

    /// <summary>Lê os limites da configuração, caindo no padrão.</summary>
    public static RetryPolicyOptions FromConfig(IConfiguration cfg)
    {
        var attempts = cfg.GetValue("Providers:MaxAttempts", ProviderRetryPolicy.MaxAttempts);
        var timeouts = cfg.GetValue(
            "Providers:MaxConsecutiveTimeouts", ProviderRetryPolicy.MaxConsecutiveTimeouts);
        return new RetryPolicyOptions(
            Math.Max(1, attempts), Math.Max(1, timeouts));
    }
}

/// <summary>
/// Política de retry das chamadas de completion (SPEC-20261016-provider-
/// resilience RF-001, espelhando <c>session/runner/retry.ts</c> do opencode):
/// backoff exponencial 2s→cap 10s com jitter, orçamento de ~6 tentativas por
/// chamada, <c>Retry-After</c> honrado como piso até 15min e teto próprio de
/// 3 timeouts consecutivos (RF-005). Pura/estática para testes de schedule.
/// </summary>
public static class ProviderRetryPolicy
{
    /// <summary>Tentativas máximas de uma mesma chamada (~2min no total).</summary>
    public const int MaxAttempts = 6;

    /// <summary>Timeouts consecutivos que encerram a chamada (RF-005).</summary>
    public const int MaxConsecutiveTimeouts = 3;

    /// <summary>Retry-After de piso capado aqui (RF-001).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    /// <summary>Padrões de corpo que sinalizam rejeição por contexto cheio
    /// (OpenAI <c>context_length_exceeded</c>, Anthropic "prompt is too
    /// long", Google "exceeds the maximum number of tokens", gateways
    /// genéricos) — RF-004.</summary>
    private static readonly Regex OverflowPattern = new(
        "context_length|context window|too many tokens|prompt is too long"
        + "|maximum number of tokens|max(_| |-)?tokens|reduce the length"
        + "|request_too_large|input is too long|exceeds.*context",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(2));

    /// <summary>Status HTTP rejeitados onde o corpo pode sinalizar overflow.</summary>
    private static bool OverflowCandidate(HttpStatusCode? status) =>
        status is HttpStatusCode.BadRequest
            or HttpStatusCode.RequestEntityTooLarge
            or HttpStatusCode.TooManyRequests;

    /// <summary>
    /// A exceção é uma rejeição de context-overflow do provedor? Só conta
    /// quando veio status candidato (400/413/429) com a assinatura no corpo —
    /// um 400 genérico não compacta nem retenta.
    /// </summary>
    public static bool IsContextOverflow(Exception ex) =>
        ex is ProviderHttpException e
        && OverflowCandidate(e.StatusCode)
        && e.Body is { } body
        && OverflowPattern.IsMatch(body);

    /// <summary>Classifica a exceção de transporte/HTTP da chamada.</summary>
    public static ProviderRetryReason Classify(Exception ex)
    {
        if (IsContextOverflow(ex))
        {
            return ProviderRetryReason.ContextOverflow;
        }
        return ex switch
        {
            // TaskCanceledException com o ct da run íntegro = timeout do
            // HttpClient (cancelamento do usuário é filtrado no chamador).
            TaskCanceledException or TimeoutException => ProviderRetryReason.Timeout,
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
                => ProviderRetryReason.RateLimit,
            HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout
                or HttpStatusCode.Conflict } => ProviderRetryReason.RateLimit,
            HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError }
                => ProviderRetryReason.ServerError,
            HttpRequestException { StatusCode: null } => ProviderRetryReason.Transport,
            HttpRequestException => ProviderRetryReason.NonRetryable,
            _ => ProviderRetryReason.NonRetryable,
        };
    }

    /// <summary>
    /// Decide a próxima ação para a exceção na tentativa
    /// <paramref name="attempt"/> (0-based). <paramref name="jitter"/> liga o
    /// jitter ±25% — testes de schedule passam false para delays exatos.
    /// </summary>
    public static RetryDecision Decide(
        Exception ex, int attempt, int consecutiveTimeouts, bool jitter = true,
        RetryPolicyOptions? options = null)
    {
        var limits = options ?? RetryPolicyOptions.Default;
        var reason = Classify(ex);
        if (reason == ProviderRetryReason.ContextOverflow
            || reason == ProviderRetryReason.NonRetryable)
        {
            return new RetryDecision(false, TimeSpan.Zero, reason);
        }
        if (reason == ProviderRetryReason.Timeout
            && consecutiveTimeouts > limits.MaxConsecutiveTimeouts)
        {
            return new RetryDecision(false, TimeSpan.Zero, reason);
        }
        if (attempt + 1 >= limits.MaxAttempts)
        {
            return new RetryDecision(false, TimeSpan.Zero, reason);
        }

        var delay = BaseDelay * Math.Pow(2, attempt);
        if (delay > MaxDelay)
        {
            delay = MaxDelay;
        }
        // Retry-After como piso, capado a 15min (RF-001).
        if (ex is ProviderHttpException { RetryAfter: { } ra } && ra > delay)
        {
            delay = ra > MaxRetryAfter ? MaxRetryAfter : ra;
        }
        if (jitter)
        {
            var factor = 0.75 + (Random.Shared.NextDouble() * 0.5);
            delay = TimeSpan.FromTicks((long)(delay.Ticks * factor));
        }
        return new RetryDecision(true, delay, reason);
    }

    /// <summary>O provider sinalizou resposta truncada por teto de output
    /// (finish_reason=length / stop_reason=max_tokens / MAX_TOKENS)?</summary>
    public static bool IsLengthFinish(string? finishReason) =>
        finishReason is "length" or "max_tokens" or "MAX_TOKENS";
}
