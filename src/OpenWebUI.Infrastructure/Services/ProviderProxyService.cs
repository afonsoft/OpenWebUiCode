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
        if (!IsAllowed(allowed, $"{path}:{method.Method}"))
        {
            return new ProxiedResponse(404, null, "Endpoint não suportado pelo proxy.");
        }

        var connections = await config.GetConnectionsAsync(ct);
        var (baseUrl, apiKey) = ResolveConnection(provider, index, connections);
        if (baseUrl is null)
        {
            return new ProxiedResponse(503, null, $"Nenhuma conexão {provider} configurada.");
        }

        using var request = BuildRequest(method, baseUrl, path, queryString, body, contentType, apiKey);
        var (upstream, error) = await SendUpstreamAsync(provider, request, ct);
        return upstream is not null
            ? new ProxiedResponse((int)upstream.StatusCode, upstream, null)
            : new ProxiedResponse(502, null, error);
    }

    /// <summary>Allowlist: match exato ou wildcard <c>prefix*suffix</c>.</summary>
    private static bool IsAllowed(HashSet<string> allowed, string key) =>
        allowed.Contains(key) || allowed.Any(entry =>
            entry.Split('*') is [var prefix, var suffix]
            && key.StartsWith(prefix, StringComparison.Ordinal)
            && key.EndsWith(suffix, StringComparison.Ordinal));

    /// <summary>Base URL + API key da conexão indexada (OpenAI pareia a key).</summary>
    private static (string? BaseUrl, string? ApiKey) ResolveConnection(
        string provider, int? index, ConnectionsConfig connections)
    {
        if (provider == "ollama")
        {
            return (Pick(connections.OllamaBaseUrls, index), null);
        }
        var i = index ?? 0;
        var baseUrl = Pick(connections.OpenAiBaseUrls, index);
        var apiKey = baseUrl is not null && i >= 0 && i < connections.OpenAiApiKeys.Count
            ? connections.OpenAiApiKeys[i]
            : null;
        return (baseUrl, apiKey);
    }

    private static HttpRequestMessage BuildRequest(
        HttpMethod method, string baseUrl, string path, string? queryString,
        byte[]? body, string? contentType, string? apiKey)
    {
        var request = new HttpRequestMessage(method, $"{baseUrl.TrimEnd('/')}/{path}{queryString}");
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
        return request;
    }

    private async Task<(HttpResponseMessage? Upstream, string? Error)> SendUpstreamAsync(
        string provider, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return (await httpClientFactory.CreateClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct), null);
        }
        catch (HttpRequestException)
        {
            return (null, $"Provedor {provider} indisponível.");
        }
        catch (TaskCanceledException)
        {
            return (null, $"Provedor {provider} indisponível.");
        }
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
