using System.Net.Http.Headers;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Núcleo do proxy transparente dos endpoints `/ollama/*` e `/openai/*`:
/// valida a allowlist de paths, resolve a conexão configurada e encaminha
/// a requisição ao provedor. Não conhece tipos do ASP.NET — quem traduz
/// para HTTP é a camada Api (request em, resposta em streaming out).
/// </summary>
public class ProviderProxyService(IHttpClientFactory httpClientFactory, ConfigService config)
{
    /// <summary>Paths permitidos por provedor (cada entrada é "PATH:METHOD").</summary>
    private static readonly HashSet<string> OllamaAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "api/tags:GET", "api/version:GET", "api/show:POST", "api/chat:POST",
        "api/generate:POST", "api/embed:POST", "api/pull:POST", "api/create:POST",
        "api/delete:DELETE", "api/copy:POST",
        "api/blobs/*:GET", "api/blobs/*:HEAD", "api/blobs/*:POST",
    };

    private static readonly HashSet<string> OpenAiAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "models:GET", "chat/completions:POST", "embeddings:POST",
        "audio/speech:POST", "audio/transcriptions:POST", "images/generations:POST",
    };

    /// <summary>
    /// Encaminha a requisição ao provedor. Em sucesso, <see cref="ProxiedResponse.Response"/>
    /// carrega a resposta upstream (ownership do chamador — descartar após copiar);
    /// em erro, descreve o status e a mensagem a devolver.
    /// </summary>
    /// <param name="method">Método HTTP da requisição recebida.</param>
    /// <param name="provider">"ollama" ou "openai".</param>
    /// <param name="path">Path relativo ao provedor (ex.: "api/tags", "chat/completions").</param>
    /// <param name="index">Índice opcional da conexão; null = primeira configurada.</param>
    /// <param name="body">Corpo da requisição recebida (pode ser null).</param>
    /// <param name="contentType">Content-Type do corpo recebido.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public sealed record ForwardRequest(
        HttpMethod Method, string Provider, string Path, int? Index,
        byte[]? Body, string? ContentType, string? QueryString);

    public async Task<ProxiedResponse> ForwardAsync(
        ForwardRequest r, CancellationToken ct)
    {
        var method = r.Method; var provider = r.Provider; var path = r.Path;
        var index = r.Index; var body = r.Body; var contentType = r.ContentType;
        var queryString = r.QueryString;
        var allowed = provider == "ollama" ? OllamaAllowed : OpenAiAllowed;
        var key = $"{path}:{method.Method}";
        var ok = allowed.Contains(key) || allowed.Any(entry =>
            entry.Split('*') is [var prefix, var suffix]
            && key.StartsWith(prefix, StringComparison.Ordinal)
            && key.EndsWith(suffix, StringComparison.Ordinal));
        if (!ok)
        {
            return new ProxiedResponse(404, null, "Endpoint não suportado pelo proxy.");
        }

        var connections = await config.GetConnectionsAsync(ct);
        string? baseUrl;
        string? apiKey = null;
        if (provider == "ollama")
        {
            baseUrl = Pick(connections.OllamaBaseUrls, index);
        }
        else
        {
            var i = index ?? 0;
            baseUrl = Pick(connections.OpenAiBaseUrls, index);
            if (baseUrl is not null && i >= 0 && i < connections.OpenAiApiKeys.Count)
            {
                apiKey = connections.OpenAiApiKeys[i];
            }
        }

        if (baseUrl is null)
        {
            return new ProxiedResponse(503, null, $"Nenhuma conexão {provider} configurada.");
        }

        using var request = new HttpRequestMessage(method, $"{baseUrl.TrimEnd('/')}/{path}{queryString}");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
            {
                request.Content.Headers.ContentType = mediaType;
            }
        }

        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await httpClientFactory.CreateClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            return new ProxiedResponse(502, null, $"Provedor {provider} indisponível.");
        }
        catch (TaskCanceledException)
        {
            return new ProxiedResponse(502, null, $"Provedor {provider} indisponível.");
        }

        return new ProxiedResponse((int)upstream.StatusCode, upstream, null);
    }

    private static string? Pick(IReadOnlyList<string> urls, int? index)
    {
        var valid = urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        var i = index ?? 0;
        return i >= 0 && i < valid.Count ? valid[i] : null;
    }
}

/// <summary>Resultado do proxy: resposta upstream ou erro a devolver ao cliente.</summary>
/// <param name="StatusCode">Status HTTP a responder.</param>
/// <param name="Response">Resposta upstream (streaming) quando não há erro.</param>
/// <param name="Error">Mensagem de erro quando <paramref name="Response"/> é null.</param>
public sealed record ProxiedResponse(int StatusCode, HttpResponseMessage? Response, string? Error);
