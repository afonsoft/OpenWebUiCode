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
        HttpStatusCode statusCode, TimeSpan? retryAfter, string? body,
        bool midStream = false)
        : base($"O provedor respondeu {(int)statusCode} {statusCode}.", null, statusCode)
    {
        RetryAfter = retryAfter;
        Body = body;
        MidStream = midStream;
    }

    /// <summary>True quando o erro veio de dentro do corpo SSE já em
    /// streaming (<c>{"error":...}</c> mid-stream) — não há resposta
    /// buffered equivalente que a reposição possa classificar, então a
    /// exceção precisa subir até a retry policy em vez de virar null.</summary>
    public bool MidStream { get; }

    /// <summary>Delay pedido pelo header <c>Retry-After</c>, quando presente.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Recorte do corpo da resposta (truncado) para detecção de
    /// context-overflow — nunca logado integralmente.</summary>
    public string? Body { get; }
}
