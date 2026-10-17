using System.Net;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Erro HTTP de uma chamada de completion ao provedor (SPEC-20261016-
/// provider-resilience): além do status carrega <see cref="RetryAfter"/> e
/// um recorte do corpo da resposta — o motor de retry decide a partir do
/// status e o detector de context-overflow procura os padrões no corpo.
/// O corpo é truncado a <see cref="MaxBodyChars"/> porque pode conter
/// trechos do prompt enviado.
/// </summary>
public sealed class ProviderHttpException : HttpRequestException
{
    /// <summary>Teto do corpo de erro capturado para classificação.</summary>
    public const int MaxBodyChars = 1200;

    public ProviderHttpException(
        HttpStatusCode statusCode, TimeSpan? retryAfter, string? body)
        : base($"O provedor respondeu {(int)statusCode} {statusCode}.", null, statusCode)
    {
        RetryAfter = retryAfter;
        Body = body;
    }

    /// <summary>Delay pedido pelo header <c>Retry-After</c>, quando presente.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Recorte do corpo da resposta (truncado) para detecção de
    /// context-overflow — nunca logado integralmente.</summary>
    public string? Body { get; }
}
