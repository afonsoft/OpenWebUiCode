using Microsoft.Extensions.Caching.Hybrid;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Routers gerenciados `/ollama/*` e `/openai/*`: proxy autenticado aos
/// provedores configurados, como no upstream (SDKs usam o Open WebUI
/// como gateway com auth JWT/sk-*).
/// </summary>
public static class PassthroughEndpoints
{
    public static void MapPassthroughEndpoints(this IEndpointRouteBuilder app)
    {
        var ollama = app.MapGroup("/ollama").RequireAuthorization();
        ollama.MapGet("/api/tags",
            (HttpContext http, ProviderProxyService proxy, HybridCache cache) =>
                Proxy(http, proxy, cache, ProviderTypes.Ollama, "api/tags", null));
        ollama.MapGet("/api/version",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.Ollama, "api/version", null));
        ollama.MapPost("/api/show",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.Ollama, "api/show", null));
        ollama.MapPost("/api/chat",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.Ollama, "api/chat", null));
        ollama.MapPost("/api/generate",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.Ollama, "api/generate", null));
        ollama.MapPost("/api/embed",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.Ollama, "api/embed", null));
        ollama.MapPost("/api/pull",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.Ollama, "api/pull", null));
        ollama.MapPost("/api/create",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.Ollama, "api/create", null));
        ollama.MapDelete("/api/delete",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.Ollama, "api/delete", null));
        ollama.MapPost("/api/copy",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.Ollama, "api/copy", null));
        ollama.MapMethods("/api/blobs/{digest}", ["GET", "HEAD"],
            (HttpContext http, string digest, ProviderProxyService proxy) =>
                ProxyBlob(http, proxy, digest));
        ollama.MapPost("/api/blobs/{digest}",
            (HttpContext http, string digest, ProviderProxyService proxy) =>
                ProxyBlob(http, proxy, digest, maxUpload: MaxBlobUpload));

        // Variantes indexadas: /ollama/{idx}/api/* usa a conexão de índice idx.
        var ollamaIndexed = app.MapGroup("/ollama/{idx:int}").RequireAuthorization();
        ollamaIndexed.MapGet("/api/tags",
            (HttpContext http, int idx, ProviderProxyService proxy, HybridCache cache) =>
                Proxy(http, proxy, cache, ProviderTypes.Ollama, "api/tags", idx));
        ollamaIndexed.MapPost("/api/chat",
            (HttpContext http, int idx, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.Ollama, "api/chat", idx));

        var openai = app.MapGroup("/openai").RequireAuthorization();
        openai.MapGet("/models",
            (HttpContext http, ProviderProxyService proxy, HybridCache cache) =>
                Proxy(http, proxy, cache, ProviderTypes.OpenAi, "models", null));
        openai.MapPost("/chat/completions",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.OpenAi, "chat/completions", null));
        openai.MapPost("/embeddings",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.OpenAi, "embeddings", null));
        openai.MapPost("/audio/speech",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.OpenAi, "audio/speech", null));
        openai.MapPost("/audio/transcriptions",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.OpenAi, "audio/transcriptions", null));
        openai.MapPost("/images/generations",
            (HttpContext http, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.OpenAi, "images/generations", null));

        var openaiIndexed = app.MapGroup("/openai/{idx:int}").RequireAuthorization();
        openaiIndexed.MapPost("/audio/speech",
            (HttpContext http, int idx, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.OpenAi, "audio/speech", idx));
        openaiIndexed.MapPost("/images/generations",
            (HttpContext http, int idx, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.OpenAi, "images/generations", idx));
        openaiIndexed.MapGet("/models",
            (HttpContext http, int idx, ProviderProxyService proxy, HybridCache cache) =>
                Proxy(http, proxy, cache, ProviderTypes.OpenAi, "models", idx));
        openaiIndexed.MapPost("/chat/completions",
            (HttpContext http, int idx, ProviderProxyService proxy) => Proxy(http, proxy, ProviderTypes.OpenAi, "chat/completions", idx));
    }

    /// <summary>Limite do upload de blob (512 MB, alinhado ao upstream).</summary>
    private const long MaxBlobUpload = 512L * 1024 * 1024;

    /// <summary>Regex do digest de blob aceito pelo Ollama (sha256 + 64 hex).</summary>
    private static readonly System.Text.RegularExpressions.Regex BlobDigest =
        new(@"^sha256:[0-9a-fA-F]{64}$", System.Text.RegularExpressions.RegexOptions.Compiled, RegexDefaults.MatchTimeout);

    /// <summary>Valida o digest e o limite de upload antes de proxear blobs.</summary>
    private static Task<IResult> ProxyBlob(
        HttpContext http, ProviderProxyService proxy, string digest, long? maxUpload = null)
    {
        if (!BlobDigest.IsMatch(digest))
        {
            return Task.FromResult<IResult>(
                Results.BadRequest(new { detail = "Digest inválido (sha256:<hex64>)." }));
        }

        if (maxUpload is not null)
        {
            http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>()
                ?.MaxRequestBodySize = maxUpload.Value;
        }

        return Proxy(http, proxy, ProviderTypes.Ollama, $"api/blobs/{digest}", null);
    }

    /// <summary>TTL do cache de respostas de leitura do proxy (tags/models).</summary>
    private static readonly TimeSpan ProxyCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Paths GET cujo conteúdo pode ser cacheado por conexão.</summary>
    private static readonly HashSet<string> CacheablePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "api/tags", "models",
    };

    private static async Task<IResult> Proxy(
        HttpContext http, ProviderProxyService proxy, string provider, string path, int? index) =>
        await Proxy(http, proxy, null, provider, path, index);

    /// <summary>Lê o body inbound, delega ao serviço e converte o resultado em IResult.</summary>
    private static async Task<IResult> Proxy(
        HttpContext http, ProviderProxyService proxy, HybridCache? cache,
        string provider, string path, int? index)
    {
        var cacheable = cache is not null
            && HttpMethods.IsGet(http.Request.Method)
            && CacheablePaths.Contains(path);
        if (cacheable)
        {
            try
            {
                var cached = await cache!.GetOrCreateAsync(
                    $"proxy:{provider}:{path}:{index?.ToString() ?? ""}",
                    async _ => await ForwardBufferedAsync(http, proxy, provider, path, index),
                    new HybridCacheEntryOptions
                    {
                        Expiration = ProxyCacheTtl,
                        LocalCacheExpiration = ProxyCacheTtl,
                    },
                    tags: ["providers"],
                    cancellationToken: http.RequestAborted);
                return new CachedResult(cached);
            }
            catch (ProxyFailureException ex)
            {
                // Falhas nunca entram no cache: um provider fora do ar não
                // fica mascarado pelos 60s do TTL.
                return ex.Response is not null
                    ? new ProxiedResult(ex.Response)
                    : Results.Problem(ex.Error, statusCode: ex.StatusCode);
            }
        }

        byte[]? body = null;
        if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
            body = buffer.ToArray();
        }

        var result = await proxy.ForwardAsync(
            new HttpMethod(http.Request.Method), provider, path, index,
            body, http.Request.ContentType, http.Request.QueryString.Value, http.RequestAborted);
        return result.Response is null
            ? Results.Problem(result.Error, statusCode: result.StatusCode)
            : new ProxiedResult(result.Response);
    }

    /// <summary>Executa o forward e bufferiza a resposta; falhas viram exceção (não cacheadas).</summary>
    private static async Task<CachedProxyResponse> ForwardBufferedAsync(
        HttpContext http, ProviderProxyService proxy,
        string provider, string path, int? index)
    {
        var result = await proxy.ForwardAsync(
            HttpMethod.Get, provider, path, index,
            null, null, http.Request.QueryString.Value, http.RequestAborted);
        if (result.Response is null)
        {
            throw new ProxyFailureException(result.StatusCode, result.Error);
        }
        if ((int)result.Response.StatusCode >= 400)
        {
            throw new ProxyFailureException((int)result.Response.StatusCode, null, result.Response);
        }

        using var response = result.Response;
        return new CachedProxyResponse(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            await response.Content.ReadAsByteArrayAsync(http.RequestAborted));
    }

    /// <summary>Falha do forward — carrega status/erro/resposta para replay sem cache.</summary>
    private sealed class ProxyFailureException(int statusCode, string? error, HttpResponseMessage? response = null) : Exception
    {
        public int StatusCode { get; } = statusCode;
        public string? Error { get; } = error;
        public HttpResponseMessage? Response { get; } = response;
    }

    /// <summary>Resposta de proxy bufferizada para o HybridCache.</summary>
    private sealed record CachedProxyResponse(int Status, string? ContentType, byte[] Body);

    /// <summary>Result que replay uma resposta bufferizada do cache.</summary>
    private sealed class CachedResult(CachedProxyResponse cached) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = cached.Status;
            context.Response.ContentType = cached.ContentType ?? "application/octet-stream";
            await context.Response.Body.WriteAsync(cached.Body, context.RequestAborted);
        }
    }

    /// <summary>Headers hop-by-hop que não são repassados ao cliente.</summary>
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "transfer-encoding", "te", "trailer",
        "proxy-authenticate", "proxy-authorization", "upgrade",
    };

    /// <summary>Result que copia status, headers e corpo (streaming) do upstream.</summary>
    private sealed class ProxiedResult(HttpResponseMessage response) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            using (response)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                foreach (var header in response.Headers.Concat(response.Content.Headers)
                             .Where(h => !HopByHop.Contains(h.Key)))
                {
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }

                context.Response.Headers.Remove("transfer-encoding");
                // Respostas SSE upstream (api/chat, api/pull) precisam passar
                // sem buffering no proxy reverso.
                context.Response.Headers["X-Accel-Buffering"] = "no";
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
        }
    }
}
