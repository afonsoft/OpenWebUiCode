using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Serviço de embeddings: chama Ollama (/api/embed) ou API compatível com
/// OpenAI (/v1/embeddings) usando as conexões configuradas. Sem provider
/// configurado ou em falha, retorna null (RAG fica desabilitado e o
/// comportamento de injeção de texto integral é preservado).
/// </summary>
public class EmbeddingService(IHttpClientFactory httpClientFactory, ConfigService config)
{
    private const string OllamaModelEnv = "RAG_EMBEDDING_MODEL";
    private const string OpenAiModelEnv = "RAG_EMBEDDING_MODEL_OPENAI";

    /// <summary>Gera o embedding de um texto ou null se nenhum provider atende.</summary>
    /// <param name="text">Texto a embeddar.</param>
    /// <param name="ct">Cancelamento.</param>
    public virtual async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        var http = httpClientFactory.CreateClient("ai-providers");
        http.Timeout = TimeSpan.FromSeconds(30);

        foreach (var baseUrl in connections.OllamaBaseUrls)
        {
            if (await PostOllamaEmbedAsync(http, baseUrl, text, ct)
                    is { } payload && payload.Embeddings?.FirstOrDefault()
                        is { Length: > 0 } vector)
            {
                return vector;
            }
        }

        var openAiModel = await ResolveOpenAiModelAsync(ct);
        for (var i = 0; i < connections.OpenAiBaseUrls.Count; i++)
        {
            if (await PostOpenAiEmbedAsync(http, connections, i, openAiModel, text, ct)
                    is { } payload && payload.Data?.FirstOrDefault()?.Embedding
                        is { Length: > 0 } vector)
            {
                return vector;
            }
        }

        return null;
    }

    /// <summary>
    /// Gera embeddings de vários textos em uma única requisição por provider
    /// (Ollama /api/embed e OpenAI /embeddings aceitam input como array).
    /// Retorna null quando nenhum provider atende o batch — o chamador pode
    /// cair no modo sequencial por EmbedAsync.
    /// </summary>
    /// <param name="texts">Textos a embeddar (ordem preservada).</param>
    /// <param name="ct">Cancelamento.</param>
    public virtual async Task<float[][]?> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        ConnectionsConfig connections;
        HttpClient http;
        try
        {
            connections = await config.GetConnectionsAsync(ct);
            http = httpClientFactory.CreateClient("ai-providers");
            http.Timeout = TimeSpan.FromSeconds(60);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        foreach (var baseUrl in connections.OllamaBaseUrls)
        {
            if (await PostOllamaEmbedAsync(http, baseUrl, texts, ct)
                    is { } payload && payload.Embeddings is { Length: > 0 } vectors)
            {
                return vectors;
            }
        }

        var openAiModel = await ResolveOpenAiModelAsync(ct);
        for (var i = 0; i < connections.OpenAiBaseUrls.Count; i++)
        {
            if (await PostOpenAiEmbedAsync(http, connections, i, openAiModel, texts, ct)
                    is not { } payload)
            {
                continue;
            }
            var vectors = payload.Data?.Select(d => d.Embedding)
                .Where(e => e is { Length: > 0 })
                .Select(e => e!)
                .ToArray();
            if (vectors is { Length: > 0 } && vectors.Length == texts.Count)
            {
                return vectors;
            }
        }

        return null;
    }

    /// <summary>POST <c>/api/embed</c> numa base Ollama — null em falha
    /// de conexão/status (o chamador tenta a próxima).</summary>
    private static async Task<OllamaEmbedResponse?> PostOllamaEmbedAsync(
        HttpClient http, string baseUrl, object input, CancellationToken ct)
    {
        try
        {
            var model = Environment.GetEnvironmentVariable(OllamaModelEnv)
                ?? "nomic-embed-text";
            var response = await http.PostAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/api/embed",
                new { model, input }, ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(ct)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Connection falhou — tenta a próxima base URL.
            return null;
        }
    }

    /// <summary>POST <c>/embeddings</c> numa base OpenAI-compatible (auth
    /// pela apiKey do índice) — null em falha (tenta a próxima).</summary>
    private static async Task<OpenAiEmbedResponse?> PostOpenAiEmbedAsync(
        HttpClient http, ConnectionsConfig connections, int i,
        string model, object input, CancellationToken ct)
    {
        try
        {
            var baseUrl = connections.OpenAiBaseUrls[i].TrimEnd('/');
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{baseUrl}/embeddings");
            if (i < connections.OpenAiApiKeys.Count &&
                !string.IsNullOrEmpty(connections.OpenAiApiKeys[i]))
            {
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer", connections.OpenAiApiKeys[i]);
            }
            request.Content = JsonContent.Create(new { model, input });
            var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<OpenAiEmbedResponse>(ct)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Connection falhou — tenta a próxima base URL.
            return null;
        }
    }

    /// <summary>
    /// Modelo de embedding OpenAI: env <c>RAG_EMBEDDING_MODEL_OPENAI</c> →
    /// kv <c>rag.embedding.model</c> (preenchido pela detecção de
    /// capacidades) → <c>text-embedding-3-small</c>.
    /// </summary>
    private async Task<string> ResolveOpenAiModelAsync(CancellationToken ct) =>
        Environment.GetEnvironmentVariable(OpenAiModelEnv)
        ?? await config.GetAsync<string?>("rag.embedding.model", null, ct)
        ?? "text-embedding-3-small";

    private sealed record OllamaEmbedResponse(
        [property: JsonPropertyName("embeddings")] float[][]? Embeddings);

    private sealed record OpenAiEmbedResponse(
        [property: JsonPropertyName("data")] OpenAiEmbedItem[]? Data);

    private sealed record OpenAiEmbedItem(
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
