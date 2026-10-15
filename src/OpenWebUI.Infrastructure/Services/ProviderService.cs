using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Resolve conexões com provedores de IA (Ollama e APIs compatíveis com OpenAI),
/// lista modelos e transmite completions no formato SSE da OpenAI.
/// </summary>
public class ProviderService(
    IHttpClientFactory httpClientFactory,
    ConfigService config,
    ILogger<ProviderService> logger,
    HybridCache cache)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Cache curto da lista de modelos: sem ele, cada mensagem de chat
    // (ResolveProviderAsync) fazia GET /models em TODAS as conexões.
    private static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Lista modelos de todas as conexões configuradas.</summary>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        var cacheKey = $"provider-models:{ConnectionsFingerprint(connections)}";
        return await cache.GetOrCreateAsync(
            cacheKey,
            async cancel => await FetchAllModelsAsync(connections, cancel),
            new HybridCacheEntryOptions
            {
                Expiration = ModelsCacheTtl,
                LocalCacheExpiration = ModelsCacheTtl,
            },
            tags: ["providers"],
            cancellationToken: ct);
    }

    private async Task<List<ModelInfo>> FetchAllModelsAsync(
        ConnectionsConfig connections, CancellationToken ct)
    {
        var models = new List<ModelInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var baseUrl in connections.OllamaBaseUrls.Where(u => !string.IsNullOrWhiteSpace(u)))
        {
            models.AddRange((await FetchOllamaModelsAsync(baseUrl, ct))
                .Where(model => seen.Add($"ollama:{model.Id}")));
        }

        for (var i = 0; i < connections.OpenAiBaseUrls.Count; i++)
        {
            models.AddRange((await FetchOpenAiModelsAsync(
                connections.OpenAiBaseUrls[i],
                connections.OpenAiApiKeys.ElementAtOrDefault(i), ct))
                .Where(model => seen.Add($"openai:{model.Id}")));
        }

        foreach (var provider in connections.ProvidersOrEmpty)
        {
            models.AddRange((await FetchTypedModelsAsync(provider, ct))
                .Where(model => seen.Add($"{provider.Type}:{model.Id}")));
        }

        return models;
    }

    private async Task<List<ModelInfo>> FetchTypedModelsAsync(
        ProviderConnection provider, CancellationToken ct) =>
        provider.Type switch
        {
            ProviderTypes.Anthropic => await FetchAnthropicModelsAsync(
                provider.BaseUrl, provider.ApiKey, ct),
            ProviderTypes.Google => await FetchGoogleModelsAsync(
                provider.BaseUrl, provider.ApiKey, ct),
            _ => [],
        };

    private static string ConnectionsFingerprint(ConnectionsConfig c)
    {
        var raw = string.Join('\n', c.OllamaBaseUrls) + '|'
            + string.Join('\n', c.OpenAiBaseUrls) + '|'
            + string.Join('\n', c.OpenAiApiKeys) + '|'
            + string.Join('\n',
                c.ProvidersOrEmpty.Select(p => $"{p.Type}|{p.BaseUrl}|{p.ApiKey}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    /// <summary>
    /// Lista modelos de uma única conexão cadastrada (por tipo e índice).
    /// Usado pela UI admin para combos de modelos por conexão.
    /// </summary>
    /// <param name="type">"ollama" ou "openai".</param>
    /// <param name="index">Índice da conexão na lista configurada.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<List<ModelInfo>> ListModelsForConnectionAsync(
        string type, int index, CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        if (type is ProviderTypes.Anthropic or ProviderTypes.Google)
        {
            var typed = connections.ProvidersOrEmpty
                .Where(p => string.Equals(p.Type, type, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return index >= 0 && index < typed.Count
                ? await FetchTypedModelsAsync(typed[index], ct)
                : [];
        }

        return type == "ollama"
            ? index >= 0 && index < connections.OllamaBaseUrls.Count
                ? await FetchOllamaModelsAsync(connections.OllamaBaseUrls[index], ct)
                : []
            : index >= 0 && index < connections.OpenAiBaseUrls.Count
                ? await FetchOpenAiModelsAsync(
                    connections.OpenAiBaseUrls[index],
                    connections.OpenAiApiKeys.ElementAtOrDefault(index), ct)
                : [];
    }

    /// <summary>GET {base}/api/tags do Ollama; vazio quando indisponível.</summary>
    private async Task<List<ModelInfo>> FetchOllamaModelsAsync(
        string baseUrl, CancellationToken ct)
    {
        var models = new List<ModelInfo>();
        try
        {
            using var response = await httpClientFactory.CreateClient()
                .GetAsync($"{TrimSlash(baseUrl)}/api/tags", ct);
            if (!response.IsSuccessStatusCode)
            {
                return models;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(json);
            foreach (var model in node?["models"]?.AsArray() ?? [])
            {
                var id = model?["model"]?.GetValue<string>()
                    ?? model?["name"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id))
                {
                    // Ollama recente reporta capabilities no /api/tags (SPEC-20261013-model-roles).
                    var caps = model!["capabilities"]?.AsArray()
                        .Select(c => c?.GetValue<string>())
                        .OfType<string>()
                        .Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
                    models.Add(new ModelInfo(
                        id, model!["name"]?.GetValue<string>() ?? id, "ollama", "ollama",
                        caps is { Count: > 0 } ? caps : null));
                }
            }
        }
        catch (HttpRequestException ex)
        {
            // Provedor indisponível: ignora e segue para o próximo.
            logger.LogDebug(ex, "Falha ao listar modelos do Ollama em {BaseUrl}.", baseUrl);
        }
        catch (TaskCanceledException ex)
        {
            // Timeout na conexão: ignora e segue para o próximo.
            logger.LogDebug(ex, "Timeout ao listar modelos do Ollama em {BaseUrl}.", baseUrl);
        }
        return models;
    }

    /// <summary>GET {base}/models de uma API OpenAI-compatível; vazio quando indisponível.</summary>
    private async Task<List<ModelInfo>> FetchOpenAiModelsAsync(
        string baseUrl, string? apiKey, CancellationToken ct)
    {
        var models = new List<ModelInfo>();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return models;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{TrimSlash(baseUrl)}/models");
            if (!string.IsNullOrEmpty(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await httpClientFactory.CreateClient().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return models;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(json);
            foreach (var model in node?["data"]?.AsArray() ?? [])
            {
                var id = model?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id))
                {
                    models.Add(new ModelInfo(
                        id,
                        id,
                        "openai",
                        model!["owned_by"]?.GetValue<string>()));
                }
            }
        }
        catch (HttpRequestException ex)
        {
            // Provedor indisponível: ignora e segue para o próximo.
            logger.LogDebug(ex, "Falha ao listar modelos OpenAI em {BaseUrl}.", baseUrl);
        }
        catch (TaskCanceledException ex)
        {
            // Timeout na conexão: ignora e segue para o próximo.
            logger.LogDebug(ex, "Timeout ao listar modelos OpenAI em {BaseUrl}.", baseUrl);
        }
        return models;
    }

    /// <summary>GET {base}/v1/models da Anthropic (x-api-key + anthropic-version); vazio quando indisponível.</summary>
    private async Task<List<ModelInfo>> FetchAnthropicModelsAsync(
        string baseUrl, string? apiKey, CancellationToken ct)
    {
        var models = new List<ModelInfo>();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            return models;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{TrimSlash(baseUrl)}/v1/models");
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            using var response = await httpClientFactory.CreateClient().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return models;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(json);
            foreach (var model in node?["data"]?.AsArray() ?? [])
            {
                var id = model?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id))
                {
                    models.Add(new ModelInfo(
                        id,
                        model!["display_name"]?.GetValue<string>() ?? id,
                        "anthropic",
                        "anthropic"));
                }
            }
        }
        catch (HttpRequestException ex)
        {
            // Provedor indisponível: ignora e segue para o próximo.
            logger.LogDebug(ex, "Falha ao listar modelos Anthropic em {BaseUrl}.", baseUrl);
        }
        catch (TaskCanceledException ex)
        {
            // Timeout na conexão: ignora e segue para o próximo.
            logger.LogDebug(ex, "Timeout ao listar modelos Anthropic em {BaseUrl}.", baseUrl);
        }
        return models;
    }

    /// <summary>GET {base}/models do Google AI Studio (x-goog-api-key); vazio quando indisponível.</summary>
    private async Task<List<ModelInfo>> FetchGoogleModelsAsync(
        string baseUrl, string? apiKey, CancellationToken ct)
    {
        var models = new List<ModelInfo>();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            return models;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{TrimSlash(baseUrl)}/models");
            request.Headers.Add("x-goog-api-key", apiKey);

            using var response = await httpClientFactory.CreateClient().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return models;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(json);
            foreach (var model in node?["models"]?.AsArray() ?? [])
            {
                var name = model?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var id = name.StartsWith("models/", StringComparison.Ordinal)
                    ? name["models/".Length..]
                    : name;
                models.Add(new ModelInfo(
                    id,
                    model!["displayName"]?.GetValue<string>() ?? id,
                    "google",
                    "google"));
            }
        }
        catch (HttpRequestException ex)
        {
            // Provedor indisponível: ignora e segue para o próximo.
            logger.LogDebug(ex, "Falha ao listar modelos Google em {BaseUrl}.", baseUrl);
        }
        catch (TaskCanceledException ex)
        {
            // Timeout na conexão: ignora e segue para o próximo.
            logger.LogDebug(ex, "Timeout ao listar modelos Google em {BaseUrl}.", baseUrl);
        }
        return models;
    }

    /// <summary>
    /// Aplica as chaves BYOK do usuário sobre a config global
    /// (SPEC-20261014-config-layers RF-005): quando
    /// <c>features.userProviderKeys</c> está ligado e o usuário tem chave
    /// para o tipo, ela substitui a(s) chave(s) globais daquele tipo —
    /// a config retornada nunca vaza a chave de outro usuário.
    /// </summary>
    internal async Task<ConnectionsConfig> ApplyUserKeysAsync(
        ConnectionsConfig connections, string? userId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(userId)
            || !await config.GetAsync(
                UserConfigOverlayService.UserProviderKeysFlag, false, ct))
        {
            return connections;
        }
        var keys = await config.GetAsync<Dictionary<string, string>?>(
            UserConfigOverlayService.ProviderKeysKey(userId), null, ct);
        if (keys is not { Count: > 0 })
        {
            return connections;
        }

        var openAiKeys = connections.OpenAiApiKeys;
        if (keys.TryGetValue(ProviderTypes.OpenAi, out var openAiKey)
            && !string.IsNullOrWhiteSpace(openAiKey))
        {
            // A chave do usuário cobre todas as conexões openai do tipo
            // (sem granularidade por URL na v1 — ver SPEC RF-005).
            openAiKeys = connections.OpenAiBaseUrls.Select(_ => openAiKey).ToList();
        }
        var providers = connections.ProvidersOrEmpty
            .Select(p => keys.TryGetValue(p.Type, out var key)
                    && !string.IsNullOrWhiteSpace(key)
                ? p with { ApiKey = key }
                : p)
            .ToList();
        return connections with { OpenAiApiKeys = openAiKeys, Providers = providers };
    }

    /// <summary>Executa uma completion sem streaming e retorna o texto do assistant.</summary>
    /// <param name="request">Requisição de completion (Stream é ignorado).</param>
    /// <param name="userId">Usuário da requisição — resolve chaves BYOK quando habilitadas.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<string> CompleteAsync(
        ChatCompletionRequest request, string? userId = null, CancellationToken ct = default)
    {
        var connections = await ApplyUserKeysAsync(
            await config.GetConnectionsAsync(ct), userId, ct);
        var provider = await ResolveProviderAsync(request, connections, ct);

        return provider switch
        {
            "ollama" => await CompleteOllamaAsync(request, connections, ct),
            "openai" => await CompleteOpenAiAsync(request, connections, ct),
            "anthropic" => await CompleteAnthropicAsync(request, connections, ct),
            "google" => await CompleteGoogleAsync(request, connections, ct),
            _ => throw new InvalidOperationException(
                $"Nenhuma conexão configurada atende ao modelo '{request.Model}'."),
        };
    }

    /// <summary>
    /// Transmite uma completion como linhas SSE no formato de chunks da OpenAI.
    /// Roteia para Ollama ou OpenAI conforme o provedor resolvido para o modelo.
    /// </summary>
    /// <param name="request">Requisição de completion enviada pelo cliente.</param>
    /// <param name="userId">Usuário da requisição — resolve chaves BYOK quando habilitadas.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        ChatCompletionRequest request,
        string? userId = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var connections = await ApplyUserKeysAsync(
            await config.GetConnectionsAsync(ct), userId, ct);
        var provider = await ResolveProviderAsync(request, connections, ct);

        var lines = provider switch
        {
            "ollama" => StreamOllamaAsync(request, connections, ct),
            "openai" => StreamOpenAiAsync(request, connections, ct),
            "anthropic" => StreamAnthropicAsync(request, connections, ct),
            "google" => StreamGoogleAsync(request, connections, ct),
            _ => throw new InvalidOperationException(
                $"Nenhuma conexão configurada atende ao modelo '{request.Model}'."),
        };

        await foreach (var line in lines.WithCancellation(ct))
        {
            yield return line;
        }
    }

    /// <summary>
    /// Executa uma completion não-streamed que pode retornar tool_calls
    /// (loop de tools). Roteia Ollama/OpenAI e normaliza as chamadas.
    /// </summary>
    /// <param name="request">Requisição com Tools preenchidas.</param>
    /// <param name="userId">Usuário da requisição — resolve chaves BYOK quando habilitadas.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<ProviderCompletion> CompleteWithToolsAsync(
        ChatCompletionRequest request, string? userId = null, CancellationToken ct = default)
    {
        var connections = await ApplyUserKeysAsync(
            await config.GetConnectionsAsync(ct), userId, ct);
        var provider = await ResolveProviderAsync(request, connections, ct);
        var payload = BuildPayload(request, stream: false, provider);
        JsonNode? json;
        bool openAi = provider == "openai";

        if (provider is ProviderTypes.Anthropic)
        {
            return await CompleteAnthropicWithToolsAsync(request, connections, ct);
        }

        if (provider is ProviderTypes.Google)
        {
            return await CompleteGoogleWithToolsAsync(request, connections, ct);
        }

        if (openAi)
        {
            var (index, baseUrl) = FirstOpenAiConnection(connections);
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post, $"{TrimSlash(baseUrl)}/chat/completions")
            {
                Content = new StringContent(
                    payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            var apiKey = connections.OpenAiApiKeys.ElementAtOrDefault(index);
            if (!string.IsNullOrEmpty(apiKey))
            {
                httpRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
            }
            using var response = await httpClientFactory.CreateClient()
                .SendAsync(httpRequest, ct);
            response.EnsureSuccessStatusCode();
            json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        else
        {
            var baseUrl = FirstBaseUrl(connections)
                ?? throw new InvalidOperationException("Nenhuma URL do Ollama configurada.");
            using var content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await httpClientFactory.CreateClient().PostAsync(
                $"{TrimSlash(baseUrl)}/api/chat", content, ct);
            response.EnsureSuccessStatusCode();
            json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }

        return ParseCompletionMessage(json, openAi);
    }

    /// <summary>
    /// Extrai o texto de raciocínio de um <c>delta</c>/<c>message</c>
    /// OpenAI-compatível: <c>reasoning_content</c> (deepseek/qwen),
    /// <c>reasoning</c> (openrouter/gpt-oss) ou
    /// <c>reasoning_details[].text</c> (openrouter detalhado).
    /// </summary>
    private static string? ExtractOpenAiReasoning(JsonNode? node)
    {
        if (node?["reasoning_content"] is JsonValue rc
            && rc.TryGetValue<string>(out var s) && s.Length > 0)
        {
            return s;
        }
        if (node?["reasoning"] is JsonValue r
            && r.TryGetValue<string>(out s) && s.Length > 0)
        {
            return s;
        }
        if (node?["reasoning_details"] is JsonArray details)
        {
            var joined = string.Concat(details
                .Select(d => d?["text"] as JsonValue)
                .Where(t => t is not null && t.TryGetValue<string>(out _))
                .Select(t => t!.GetValue<string>()));
            return joined.Length > 0 ? joined : null;
        }
        return null;
    }

    /// <summary>Normaliza <c>choices[0].message</c> (OpenAI) ou <c>message</c> (Ollama).</summary>
    private static ProviderCompletion ParseCompletionMessage(JsonNode? json, bool openAi)
    {
        var firstChoice = json?["choices"] is JsonArray { Count: > 0 } arr ? arr[0] : null;
        var message = openAi ? firstChoice?["message"] : json?["message"];
        var content = message?["content"]?.GetValue<string>() ?? string.Empty;
        var reasoning = openAi
            ? ExtractOpenAiReasoning(message)
            : message?["thinking"] is JsonValue th && th.TryGetValue<string>(out var t)
                ? t : null;
        var calls = new List<ProviderToolCall>();
        var callsNode = message?["tool_calls"]?.AsArray();
        var callsJson = callsNode?.ToJsonString() ?? "[]";
        if (callsNode is not null)
        {
            foreach (var call in callsNode)
            {
                var name = call!["function"]?["name"]?.GetValue<string>();
                if (name is null)
                {
                    continue;
                }
                var args = call!["function"]?["arguments"];
                var argsJson = args is JsonValue ? args.GetValue<string>() : args?.ToJsonString() ?? "{}";
                calls.Add(new ProviderToolCall(
                    call!["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                    name, argsJson));
            }
        }

        return new ProviderCompletion(content, calls, callsJson, reasoning);
    }

    /// <summary>
    /// Variante streamed de <see cref="CompleteWithToolsAsync"/>: emite os
    /// pedaços de texto via <paramref name="onDelta"/> conforme chegam e
    /// acumula os fragmentos de <c>tool_calls</c> (OpenAI manda name/id uma
    /// vez e arguments em pedaços por <c>index</c>; Ollama manda o array
    /// completo na mensagem final). Retorna null quando a requisição falha
    /// antes de produzir saída — sinal para o chamador cair na variante
    /// buffered (alguns endpoints OpenAI-compatíveis rejeitam stream+tools).
    /// </summary>
    /// <param name="request">Requisição com Tools preenchidas (ou null para o round final).</param>
    /// <param name="onDelta">Callback por pedaço de texto (pode ser null — vira buffered).</param>
    /// <param name="onReasoning">Callback por pedaço de raciocínio/thinking
    /// (reasoning_content, thinking_delta, thought:true, message.thinking).
    /// Canal separado do conteúdo — a UI mostra colapsado e nada é
    /// persistido na mensagem (SPEC-20261015-chat-thinking-stream).</param>
    /// <param name="userId">Usuário da requisição — resolve chaves BYOK quando habilitadas.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<ProviderCompletion?> CompleteWithToolsStreamingAsync(
        ChatCompletionRequest request,
        Func<string, CancellationToken, Task>? onDelta,
        Func<string, CancellationToken, Task>? onReasoning = null,
        string? userId = null,
        CancellationToken ct = default)
    {
        var connections = await ApplyUserKeysAsync(
            await config.GetConnectionsAsync(ct), userId, ct);
        var provider = await ResolveProviderAsync(request, connections, ct);
        var payload = BuildPayload(request, stream: true, provider);

        try
        {
            return provider switch
            {
                "openai" => await StreamOpenAiToolsAsync(request, connections, payload, onDelta, onReasoning, ct),
                "anthropic" => await StreamAnthropicToolsAsync(request, connections, onDelta, onReasoning, ct),
                "google" => await StreamGoogleToolsAsync(request, connections, onDelta, onReasoning, ct),
                _ => await StreamOllamaToolsAsync(request, connections, payload, onDelta, onReasoning, ct),
            };
        }
        catch (HttpRequestException)
        {
            // Falha de transporte/setup antes da resposta — o chamador
            // refaz a rodada buffered. Erros mid-stream chegam aqui também,
            // mas o texto já emitido é reenviado inteiro no fallback: melhor
            // duplicar do que perder a rodada.
            return null;
        }
    }

    /// <summary>
    /// Round streamed OpenAI: lê linhas <c>data:</c>, emite deltas de
    /// <c>content</c> e monta os tool_calls fragmentados por
    /// <c>index</c>. <c>ToolCallsJson</c> reproduz o formato upstream
    /// (<c>arguments</c> como string) para o echo na próxima mensagem.
    /// </summary>
    private async Task<ProviderCompletion> StreamOpenAiToolsAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        JsonObject payload,
        Func<string, CancellationToken, Task>? onDelta,
        Func<string, CancellationToken, Task>? onReasoning,
        CancellationToken ct)
    {
        var (index, baseUrl) = FirstOpenAiConnection(connections);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{TrimSlash(baseUrl)}/chat/completions")
        {
            Content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var apiKey = connections.OpenAiApiKeys.ElementAtOrDefault(index);
        if (!string.IsNullOrEmpty(apiKey))
        {
            httpRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        if (!string.Equals(response.Content.Headers.ContentType?.MediaType,
                "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // Endpoint ignorou stream:true (compat/proxy) e devolveu a
            // completion de uma vez — parseia buffered e entrega o texto
            // num delta só, mantendo a semântica do método.
            var buffered = ParseCompletionMessage(
                JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)), openAi: true);
            if (buffered.Content.Length > 0 && onDelta is not null)
            {
                await onDelta(buffered.Content, ct);
            }
            if (!string.IsNullOrEmpty(buffered.Reasoning) && onReasoning is not null)
            {
                await onReasoning(buffered.Reasoning, ct);
            }
            return buffered;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var callBuffers = new SortedDictionary<int, OpenAiToolCallBuffer>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                continue;
            }

            var choice = node?["choices"] is JsonArray { Count: > 0 } arr ? arr[0] : null;
            var delta = choice?["delta"];
            var piece = delta?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(piece))
            {
                content.Append(piece);
                if (onDelta is not null)
                {
                    await onDelta(piece, ct);
                }
            }

            // Reasoning/thinking de gateways OpenAI-compatíveis (deepseek,
            // openrouter, OmniRoute): reasoning_content | reasoning |
            // reasoning_details[].text — canal separado do content.
            if (onReasoning is not null
                && ExtractOpenAiReasoning(delta) is { Length: > 0 } thought)
            {
                await onReasoning(thought, ct);
            }

            if (delta?["tool_calls"] is not JsonArray callParts)
            {
                continue;
            }

            foreach (var part in callParts)
            {
                var position = part?["index"]?.GetValue<int>() ?? callBuffers.Count;
                if (!callBuffers.TryGetValue(position, out var buffer))
                {
                    buffer = new OpenAiToolCallBuffer();
                    callBuffers[position] = buffer;
                }
                buffer.Id = part?["id"]?.GetValue<string>() ?? buffer.Id;
                var name = part?["function"]?["name"]?.GetValue<string>();
                if (name is not null)
                {
                    buffer.Name = name;
                }
                var args = part?["function"]?["arguments"]?.GetValue<string>();
                if (args is not null)
                {
                    buffer.Args.Append(args);
                }
            }
        }

        var calls = callBuffers.Values
            .Where(b => b.Name is not null)
            .Select(b => new ProviderToolCall(
                b.Id ?? Guid.NewGuid().ToString("N"),
                b.Name!,
                b.Args.Length > 0 ? b.Args.ToString() : "{}"))
            .ToList();
        var callsJson = JsonSerializer.Serialize(calls.Select(c => new
        {
            id = c.Id,
            type = "function",
            function = new { name = c.Name, arguments = c.ArgumentsJson },
        }));
        return new ProviderCompletion(content.ToString(), calls, callsJson);
    }

    /// <summary>Buffer de uma tool_call fragmentada no stream OpenAI.</summary>
    private sealed class OpenAiToolCallBuffer
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Args { get; } = new();
    }

    /// <summary>
    /// Round streamed Ollama (<c>/api/chat</c> NDJSON): emite
    /// <c>message.content</c> por linha e captura o array
    /// <c>message.tool_calls</c> verbatim — o echo na próxima mensagem
    /// precisa do formato original (arguments como objeto).
    /// </summary>
    private async Task<ProviderCompletion> StreamOllamaToolsAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        JsonObject payload,
        Func<string, CancellationToken, Task>? onDelta,
        Func<string, CancellationToken, Task>? onReasoning,
        CancellationToken ct)
    {
        var baseUrl = FirstBaseUrl(connections)
            ?? throw new InvalidOperationException("Nenhuma URL do Ollama configurada.");

        // Ollama aceita "options" em vez de parâmetros de topo (mesmo remap de StreamOllamaAsync).
        if (request.Params is { Count: > 0 })
        {
            var options = new JsonObject();
            foreach (var key in new[] { "temperature", "top_p", "top_k", "num_predict", "repeat_penalty", "seed", "stop" }
                         .Where(k => payload.ContainsKey(k)))
            {
                options[MapOllamaParam(key)] = payload[key];
                payload.Remove(key);
            }

            if (options.Count > 0)
            {
                payload["options"] = options;
            }
        }

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{TrimSlash(baseUrl)}/api/chat")
        {
            Content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var callNodes = new List<JsonNode>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var piece = node?["message"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(piece))
            {
                content.Append(piece);
                if (onDelta is not null)
                {
                    await onDelta(piece, ct);
                }
            }

            // Modelos "think" do Ollama emitem message.thinking separado
            // de message.content na mesma linha NDJSON.
            if (onReasoning is not null
                && node?["message"]?["thinking"] is JsonValue think
                && think.TryGetValue<string>(out var thought)
                && thought.Length > 0)
            {
                await onReasoning(thought, ct);
            }

            if (node?["message"]?["tool_calls"] is JsonArray { Count: > 0 } calls)
            {
                callNodes.AddRange(calls.Select(c => c!.DeepClone()));
            }
        }

        var callsJson = new JsonArray(callNodes.ToArray());
        var parsed = new List<ProviderToolCall>();
        foreach (var call in callNodes)
        {
            var name = call!["function"]?["name"]?.GetValue<string>();
            if (name is null)
            {
                continue;
            }
            var args = call!["function"]?["arguments"];
            var argsJson = args is JsonValue value && value.TryGetValue<string>(out var s)
                ? s
                : args?.ToJsonString() ?? "{}";
            parsed.Add(new ProviderToolCall(
                call!["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                name, argsJson));
        }

        return new ProviderCompletion(content.ToString(), parsed, callsJson.ToJsonString());
    }

    private async Task<string> ResolveProviderAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.Connection))
        {
            return request.Connection;
        }

        var models = await ListModelsAsync(ct);
        var byProvider = models
            .GroupBy(m => m.Provider)
            .ToDictionary(g => g.Key,
                g => g.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        // Ordem de precedência: quem declarou o modelo primeiro leva.
        foreach (var provider in new[]
        {
            ProviderTypes.Ollama, ProviderTypes.OpenAi,
            ProviderTypes.Anthropic, ProviderTypes.Google,
        })
        {
            if (byProvider.TryGetValue(provider, out var ids) && ids.Contains(request.Model))
            {
                return provider;
            }
        }

        return connections.OpenAiBaseUrls.Count > 0 ? "openai" : "ollama";
    }

    // ---------------- Anthropic (Messages API) ----------------

    private const string AnthropicVersion = "2023-06-01";

    private static ProviderConnection FindTypedConnection(
        ConnectionsConfig connections, string type) =>
        connections.ProvidersOrEmpty.FirstOrDefault(p =>
            string.Equals(p.Type, type, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Nenhuma conexão '{type}' configurada.");

    private static HttpRequestMessage NewAnthropicRequest(
        ProviderConnection connection, JsonObject payload)
    {
        var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{TrimSlash(connection.BaseUrl)}/v1/messages")
        {
            Content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(connection.ApiKey))
        {
            httpRequest.Headers.Add("x-api-key", connection.ApiKey);
        }
        httpRequest.Headers.Add("anthropic-version", AnthropicVersion);
        return httpRequest;
    }

    /// <summary>
    /// Converte a conversa OpenAI-shaped para o formato da Messages API:
    /// <c>system</c> vira campo top-level, mensagens <c>tool</c> viram blocos
    /// <c>tool_result</c> numa mensagem user consecutiva, <c>tool_calls</c> do
    /// assistant viram blocos <c>tool_use</c> com <c>input</c> objeto e tools
    /// OpenAI viram <c>{name, description, input_schema}</c>.
    /// </summary>
    private static JsonObject BuildAnthropicPayload(ChatCompletionRequest request, bool stream)
    {
        var system = new List<string>();
        var messages = new List<JsonObject>();
        foreach (var m in request.Messages)
        {
            if (m.Role == "system")
            {
                if (!string.IsNullOrWhiteSpace(m.Content))
                {
                    system.Add(m.Content);
                }
                continue;
            }

            if (m.ToolCallId is not null || m.Role == "tool")
            {
                var block = new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = m.ToolCallId,
                    ["content"] = m.Content,
                };
                if (messages.Count > 0
                    && messages[^1]["role"]?.GetValue<string>() == "user"
                    && messages[^1]["content"] is JsonArray prev
                    && prev.All(n => n?["type"]?.GetValue<string>() == "tool_result"))
                {
                    // Tool results da mesma rodada entram numa única mensagem user.
                    prev.Add(block);
                }
                else
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray(block),
                    });
                }
                continue;
            }

            if (m.Role == "assistant" && m.ToolCallsJson is not null)
            {
                var blocks = new JsonArray();
                if (!string.IsNullOrEmpty(m.Content))
                {
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                }
                foreach (var call in JsonNode.Parse(m.ToolCallsJson)!.AsArray())
                {
                    var name = call?["function"]?["name"]?.GetValue<string>();
                    if (name is null)
                    {
                        continue;
                    }
                    var argsRaw = call?["function"]?["arguments"];
                    JsonNode? input;
                    try
                    {
                        input = argsRaw is JsonValue s
                            ? JsonNode.Parse(s.GetValue<string>())
                            : argsRaw?.DeepClone();
                    }
                    catch (Exception)
                    {
                        input = new JsonObject();
                    }
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call?["id"]?.GetValue<string>()
                            ?? Guid.NewGuid().ToString("N"),
                        ["name"] = name,
                        ["input"] = input ?? new JsonObject(),
                    });
                }
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = blocks,
                });
                continue;
            }

            // SPEC-20261013-chat-media-attachments RF-006: imagens do anexo
            // viram blocos {type:"image", source:{base64}} na Messages API.
            if (m.Images is { Count: > 0 } images && m.Role == "user")
            {
                var blocks = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = m.Content },
                };
                foreach (var img in images)
                {
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = img.MimeType,
                            ["data"] = img.Base64Data,
                        },
                    });
                }
                messages.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = blocks,
                });
                continue;
            }

            messages.Add(new JsonObject
            {
                ["role"] = m.Role == "assistant" ? "assistant" : "user",
                ["content"] = m.Content,
            });
        }

        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = 4096,
            ["messages"] = new JsonArray(messages.Cast<JsonNode>().ToArray()),
            ["stream"] = stream,
        };
        if (system.Count > 0)
        {
            payload["system"] = string.Join("\n", system);
        }

        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                var fn = JsonNode.Parse(tool.GetRawText())?["function"];
                var name = fn?["name"]?.GetValue<string>();
                if (name is null)
                {
                    continue;
                }
                tools.Add(new JsonObject
                {
                    ["name"] = name,
                    ["description"] = fn?["description"]?.GetValue<string>(),
                    ["input_schema"] = fn?["parameters"]?.DeepClone()
                        ?? new JsonObject { ["type"] = "object" },
                });
            }
            if (tools.Count > 0)
            {
                payload["tools"] = tools;
            }
        }

        if (request.Params is not null)
        {
            foreach (var (key, value) in request.Params)
            {
                switch (key)
                {
                    case "temperature":
                    case "top_p":
                    case "top_k":
                        payload[key] = ToJsonNode(value);
                        break;
                    case "max_tokens":
                    case "max_completion_tokens":
                        payload["max_tokens"] = ToJsonNode(value);
                        break;
                    case "stop":
                        payload["stop_sequences"] = value is string s
                            ? new JsonArray(s)
                            : ToJsonNode(value);
                        break;
                }
            }
        }

        return payload;
    }

    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        JsonElement e => JsonNode.Parse(e.GetRawText()),
        bool b => b,
        int i => i,
        long l => l,
        float f => f,
        double d => d,
        decimal m => m,
        string s => s,
        null => null,
        _ => JsonSerializer.SerializeToNode(value, JsonOptions),
    };

    /// <summary>Parseia a resposta da Messages API: texto + tool_use normalizados.</summary>
    private static ProviderCompletion ParseAnthropicResponse(JsonNode? json)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new List<ProviderToolCall>();
        var callsJson = new JsonArray();
        foreach (var block in json?["content"]?.AsArray() ?? [])
        {
            switch (block?["type"]?.GetValue<string>())
            {
                case "text":
                    content.Append(block?["text"]?.GetValue<string>());
                    break;
                case "thinking":
                    // Bloco de raciocínio (extended thinking): canal separado —
                    // não entra no Content persistido.
                    reasoning.Append(block?["thinking"]?.GetValue<string>());
                    break;
                case "tool_use":
                    var id = block?["id"]?.GetValue<string>()
                        ?? Guid.NewGuid().ToString("N");
                    var name = block?["name"]?.GetValue<string>() ?? string.Empty;
                    var input = block?["input"]?.ToJsonString() ?? "{}";
                    calls.Add(new ProviderToolCall(id, name, input));
                    callsJson.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = name,
                            ["arguments"] = input,
                        },
                    });
                    break;
            }
        }
        return new ProviderCompletion(
            content.ToString(), calls, callsJson.ToJsonString(),
            reasoning.Length > 0 ? reasoning.ToString() : null);
    }

    private async Task<string> CompleteAnthropicAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var connection = FindTypedConnection(connections, "anthropic");
        var payload = BuildAnthropicPayload(request, stream: false);
        using var httpRequest = NewAnthropicRequest(connection, payload);
        using var response = await httpClientFactory.CreateClient().SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseAnthropicResponse(json).Content;
    }

    private async Task<ProviderCompletion> CompleteAnthropicWithToolsAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var connection = FindTypedConnection(connections, "anthropic");
        var payload = BuildAnthropicPayload(request, stream: false);
        using var httpRequest = NewAnthropicRequest(connection, payload);
        using var response = await httpClientFactory.CreateClient().SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseAnthropicResponse(json);
    }

    /// <summary>Lê eventos SSE (pares event:/data: separados por linha em branco).</summary>
    private static async IAsyncEnumerable<(string Event, JsonNode? Data)> ReadSseEventsAsync(
        StreamReader reader, [EnumeratorCancellation] CancellationToken ct)
    {
        string? eventName = null;
        JsonNode? data = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (eventName is not null || data is not null)
                {
                    yield return (eventName ?? string.Empty, data);
                    eventName = null;
                    data = null;
                }
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                try
                {
                    data = JsonNode.Parse(line["data:".Length..].Trim());
                }
                catch (JsonException)
                {
                    data = null;
                }
            }
        }
        if (eventName is not null || data is not null)
        {
            yield return (eventName ?? string.Empty, data);
        }
    }

    /// <summary>
    /// Stream simples para o cliente: traduz content_block_delta de texto da
    /// Anthropic em linhas <c>data:</c> de chunk OpenAI, terminando em [DONE].
    /// </summary>
    private async IAsyncEnumerable<string> StreamAnthropicAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var connection = FindTypedConnection(connections, "anthropic");
        var payload = BuildAnthropicPayload(request, stream: true);
        using var httpRequest = NewAnthropicRequest(connection, payload);
        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        await foreach (var (eventName, data) in ReadSseEventsAsync(reader, ct))
        {
            if (eventName == "content_block_delta")
            {
                var deltaType = data?["delta"]?["type"]?.GetValue<string>();
                // thinking_delta sai como delta.reasoning (canal separado);
                // signature_delta (metadado criptográfico) é descartada.
                var (field, prop) = deltaType switch
                {
                    "text_delta" => ("content", "text"),
                    "thinking_delta" => ("reasoning", "thinking"),
                    _ => (null, null),
                };
                if (field is not null && prop is not null
                    && data?["delta"]?[prop]?.GetValue<string>() is { Length: > 0 } text)
                {
                    var chunk = new JsonObject
                    {
                        ["object"] = "chat.completion.chunk",
                        ["choices"] = new JsonArray(new JsonObject
                        {
                            ["index"] = 0,
                            ["delta"] = new JsonObject { [field] = text },
                        }),
                    };
                    yield return $"data: {chunk.ToJsonString()}";
                }
            }
            else if (eventName == "message_stop")
            {
                break;
            }
        }
        yield return "data: [DONE]";
    }

    /// <summary>
    /// Round streamed Anthropic: text_delta dispara <paramref name="onDelta"/>;
    /// blocos tool_use chegam via content_block_start (id/name) + deltas
    /// input_json_delta (partial_json acumulado por index) até o stop do bloco.
    /// </summary>
    private async Task<ProviderCompletion> StreamAnthropicToolsAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        Func<string, CancellationToken, Task>? onDelta,
        Func<string, CancellationToken, Task>? onReasoning,
        CancellationToken ct)
    {
        var connection = FindTypedConnection(connections, "anthropic");
        var payload = BuildAnthropicPayload(request, stream: true);
        using var httpRequest = NewAnthropicRequest(connection, payload);
        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var calls = new List<ProviderToolCall>();
        var callsJson = new JsonArray();
        var openTools = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();

        await foreach (var (eventName, data) in ReadSseEventsAsync(reader, ct))
        {
            var index = data?["index"]?.GetValue<int>() ?? 0;
            if (eventName == "content_block_start"
                && data?["content_block"]?["type"]?.GetValue<string>() == "tool_use")
            {
                openTools[index] = (
                    data?["content_block"]?["id"]?.GetValue<string>()
                        ?? Guid.NewGuid().ToString("N"),
                    data?["content_block"]?["name"]?.GetValue<string>() ?? string.Empty,
                    new StringBuilder());
            }
            else if (eventName == "content_block_delta")
            {
                var delta = data?["delta"];
                var deltaType = delta?["type"]?.GetValue<string>();
                if (deltaType == "text_delta")
                {
                    var text = delta?["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(text))
                    {
                        content.Append(text);
                        if (onDelta is not null)
                        {
                            await onDelta(text, ct);
                        }
                    }
                }
                else if (deltaType == "thinking_delta")
                {
                    // Canal separado: thinking não entra no content da run.
                    var thinking = delta?["thinking"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(thinking) && onReasoning is not null)
                    {
                        await onReasoning(thinking, ct);
                    }
                }
                else if (deltaType == "input_json_delta"
                    && openTools.TryGetValue(index, out var tool))
                {
                    tool.Args.Append(delta?["partial_json"]?.GetValue<string>());
                }
            }
            else if (eventName == "content_block_stop"
                && openTools.Remove(index, out var closed))
            {
                var args = closed.Args.ToString();
                if (args.Length == 0)
                {
                    args = "{}";
                }
                calls.Add(new ProviderToolCall(closed.Id, closed.Name, args));
                callsJson.Add(new JsonObject
                {
                    ["id"] = closed.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = closed.Name,
                        ["arguments"] = args,
                    },
                });
            }
        }

        return new ProviderCompletion(content.ToString(), calls, callsJson.ToJsonString());
    }

    // ---------------- Google AI Studio (Gemini API) ----------------

    private static HttpRequestMessage NewGoogleRequest(
        ProviderConnection connection, string model, bool stream, JsonObject payload)
    {
        var action = stream ? "streamGenerateContent" : "generateContent";
        var url = $"{TrimSlash(connection.BaseUrl)}/models/" +
            $"{Uri.EscapeDataString(model)}:{action}";
        if (stream)
        {
            url += "?alt=sse";
        }

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(connection.ApiKey))
        {
            httpRequest.Headers.Add("x-goog-api-key", connection.ApiKey);
        }
        return httpRequest;
    }

    /// <summary>
    /// Converte a conversa OpenAI-shaped para o formato Gemini: <c>system</c> vira
    /// <c>systemInstruction</c>, assistant vira role <c>model</c>, tool_calls viram
    /// partes <c>functionCall</c>, tool results viram <c>functionResponse</c>
    /// (agrupados num turno user) e tools viram <c>functionDeclarations</c>.
    /// </summary>
    private static JsonObject BuildGooglePayload(ChatCompletionRequest request)
    {
        var system = new List<string>();
        var contents = new List<JsonObject>();
        var toolNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in request.Messages)
        {
            if (m.Role == "system")
            {
                if (!string.IsNullOrWhiteSpace(m.Content))
                {
                    system.Add(m.Content);
                }
                continue;
            }

            if (m.ToolCallId is not null || m.Role == "tool")
            {
                var part = new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = toolNames.TryGetValue(m.ToolCallId ?? "", out var n)
                            ? n
                            : m.ToolCallId ?? "tool",
                        ["response"] = new JsonObject
                        {
                            ["result"] = m.Content,
                        },
                    },
                };
                if (contents.Count > 0
                    && contents[^1]["role"]?.GetValue<string>() == "user"
                    && contents[^1]["parts"] is JsonArray prevParts
                    && prevParts.All(p => p?["functionResponse"] is not null))
                {
                    prevParts.Add(part);
                }
                else
                {
                    contents.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray(part),
                    });
                }
                continue;
            }

            if (m.Role == "assistant")
            {
                var parts = new JsonArray();
                if (!string.IsNullOrEmpty(m.Content))
                {
                    parts.Add(new JsonObject { ["text"] = m.Content });
                }
                if (m.ToolCallsJson is not null)
                {
                    foreach (var call in JsonNode.Parse(m.ToolCallsJson)!.AsArray())
                    {
                        var name = call?["function"]?["name"]?.GetValue<string>();
                        if (name is null)
                        {
                            continue;
                        }
                        var id = call?["id"]?.GetValue<string>();
                        if (id is not null)
                        {
                            toolNames[id] = name;
                        }
                        var argsRaw = call?["function"]?["arguments"];
                        JsonNode? args;
                        try
                        {
                            args = argsRaw is JsonValue s
                                ? JsonNode.Parse(s.GetValue<string>())
                                : argsRaw?.DeepClone();
                        }
                        catch (Exception)
                        {
                            args = new JsonObject();
                        }
                        parts.Add(new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["name"] = name,
                                ["args"] = args ?? new JsonObject(),
                            },
                        });
                    }
                }
                contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
                continue;
            }

            contents.Add(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = m.Content }),
            });
        }

        var payload = new JsonObject
        {
            ["contents"] = new JsonArray(contents.Cast<JsonNode>().ToArray()),
        };
        if (system.Count > 0)
        {
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(
                    new JsonObject { ["text"] = string.Join("\n", system) }),
            };
        }

        if (request.Tools is { Count: > 0 })
        {
            var declarations = new JsonArray();
            foreach (var tool in request.Tools)
            {
                var fn = JsonNode.Parse(tool.GetRawText())?["function"];
                var name = fn?["name"]?.GetValue<string>();
                if (name is null)
                {
                    continue;
                }
                declarations.Add(new JsonObject
                {
                    ["name"] = name,
                    ["description"] = fn?["description"]?.GetValue<string>(),
                    ["parameters"] = fn?["parameters"]?.DeepClone()
                        ?? new JsonObject { ["type"] = "object" },
                });
            }
            if (declarations.Count > 0)
            {
                payload["tools"] = new JsonArray(
                    new JsonObject { ["functionDeclarations"] = declarations });
            }
        }

        if (request.Params is { Count: > 0 })
        {
            var config = new JsonObject();
            foreach (var (key, value) in request.Params)
            {
                switch (key)
                {
                    case "temperature":
                        config["temperature"] = ToJsonNode(value);
                        break;
                    case "top_p":
                        config["topP"] = ToJsonNode(value);
                        break;
                    case "top_k":
                        config["topK"] = ToJsonNode(value);
                        break;
                    case "max_tokens":
                    case "max_completion_tokens":
                        config["maxOutputTokens"] = ToJsonNode(value);
                        break;
                    case "stop":
                        config["stopSequences"] = value is string s
                            ? new JsonArray(s)
                            : ToJsonNode(value);
                        break;
                }
            }
            if (config.Count > 0)
            {
                payload["generationConfig"] = config;
            }
        }

        return payload;
    }

    /// <summary>Parseia candidates[].content.parts: text + functionCall normalizados.</summary>
    private static ProviderCompletion ParseGoogleResponse(JsonNode? json)
    {
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new List<ProviderToolCall>();
        var callsJson = new JsonArray();
        var parts = json?["candidates"] is JsonArray { Count: > 0 } c
            ? c[0]?["content"]?["parts"]?.AsArray()
            : null;
        foreach (var part in parts ?? [])
        {
            if (part?["text"] is { } text)
            {
                // part.thought=true é raciocínio — não vai para o Content.
                var isThought = part["thought"] is JsonValue flag
                    && flag.TryGetValue<bool>(out var b) && b;
                if (isThought)
                {
                    reasoning.Append(text.GetValue<string>());
                }
                else
                {
                    content.Append(text.GetValue<string>());
                }
            }
            else if (part?["functionCall"] is { } call)
            {
                var id = Guid.NewGuid().ToString("N");
                var name = call["name"]?.GetValue<string>() ?? string.Empty;
                var args = call["args"]?.ToJsonString() ?? "{}";
                calls.Add(new ProviderToolCall(id, name, args));
                callsJson.Add(new JsonObject
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = name,
                        ["arguments"] = args,
                    },
                });
            }
        }
        return new ProviderCompletion(
            content.ToString(), calls, callsJson.ToJsonString(),
            reasoning.Length > 0 ? reasoning.ToString() : null);
    }

    private async Task<string> CompleteGoogleAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var connection = FindTypedConnection(connections, "google");
        var payload = BuildGooglePayload(request);
        using var httpRequest = NewGoogleRequest(connection, request.Model, stream: false, payload);
        using var response = await httpClientFactory.CreateClient().SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseGoogleResponse(json).Content;
    }

    private async Task<ProviderCompletion> CompleteGoogleWithToolsAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var connection = FindTypedConnection(connections, "google");
        var payload = BuildGooglePayload(request);
        using var httpRequest = NewGoogleRequest(connection, request.Model, stream: false, payload);
        using var response = await httpClientFactory.CreateClient().SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseGoogleResponse(json);
    }

    /// <summary>Stream simples: text parts de cada chunk viram linhas <c>data:</c> OpenAI.</summary>
    private async IAsyncEnumerable<string> StreamGoogleAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var connection = FindTypedConnection(connections, "google");
        var payload = BuildGooglePayload(request);
        using var httpRequest = NewGoogleRequest(connection, request.Model, stream: true, payload);
        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        await foreach (var (_, data) in ReadSseEventsAsync(reader, ct))
        {
            var parts = data?["candidates"] is JsonArray { Count: > 0 } c
                ? c[0]?["content"]?["parts"]?.AsArray()
                : null;
            foreach (var part in parts ?? [])
            {
                var text = part?["text"]?.GetValue<string>();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }
                // thought:true = raciocínio do modelo → delta.reasoning.
                var field = part?["thought"] is JsonValue flag
                    && flag.TryGetValue<bool>(out var b) && b
                    ? "reasoning" : "content";
                var chunk = new JsonObject
                {
                    ["object"] = "chat.completion.chunk",
                    ["choices"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = new JsonObject { [field] = text },
                    }),
                };
                yield return $"data: {chunk.ToJsonString()}";
            }
        }
        yield return "data: [DONE]";
    }

    /// <summary>
    /// Round streamed Gemini: chunks trazem parts com text (deltas) ou
    /// functionCall (completo num chunk — Gemini não fragmenta args).
    /// </summary>
    private async Task<ProviderCompletion> StreamGoogleToolsAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        Func<string, CancellationToken, Task>? onDelta,
        Func<string, CancellationToken, Task>? onReasoning,
        CancellationToken ct)
    {
        var connection = FindTypedConnection(connections, "google");
        var payload = BuildGooglePayload(request);
        using var httpRequest = NewGoogleRequest(connection, request.Model, stream: true, payload);
        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var calls = new List<ProviderToolCall>();
        var callsJson = new JsonArray();

        await foreach (var (_, data) in ReadSseEventsAsync(reader, ct))
        {
            var parts = data?["candidates"] is JsonArray { Count: > 0 } c
                ? c[0]?["content"]?["parts"]?.AsArray()
                : null;
            foreach (var part in parts ?? [])
            {
                if (part?["text"] is { } textNode)
                {
                    var text = textNode.GetValue<string>();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }
                    // part.thought=true é a cadeia de raciocínio do Gemini —
                    // antes vazava para o content persistido da resposta.
                    var isThought = part["thought"] is JsonValue flag
                        && flag.TryGetValue<bool>(out var b) && b;
                    if (isThought)
                    {
                        if (onReasoning is not null)
                        {
                            await onReasoning(text, ct);
                        }
                        continue;
                    }
                    content.Append(text);
                    if (onDelta is not null)
                    {
                        await onDelta(text, ct);
                    }
                }
                else if (part?["functionCall"] is { } call)
                {
                    var id = Guid.NewGuid().ToString("N");
                    var name = call["name"]?.GetValue<string>() ?? string.Empty;
                    var args = call["args"]?.ToJsonString() ?? "{}";
                    calls.Add(new ProviderToolCall(id, name, args));
                    callsJson.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = name,
                            ["arguments"] = args,
                        },
                    });
                }
            }
        }

        return new ProviderCompletion(content.ToString(), calls, callsJson.ToJsonString());
    }

    /// <summary>
    /// Monta o payload chat-completions. <paramref name="provider"/> decide o
    /// formato das imagens (SPEC-20261013-chat-media-attachments RF-006):
    /// Ollama recebe <c>images: [base64]</c> ao lado do content string;
    /// OpenAI-compatible recebe <c>content</c> como array de partes
    /// <c>text</c> + <c>image_url</c> (data-url). Mensagem sem imagens
    /// mantém <c>content</c> string (compat).
    /// </summary>
    private JsonObject BuildPayload(ChatCompletionRequest request, bool stream, string provider)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray(request.Messages
                .Select(m =>
                {
                    var node = new JsonObject
                    {
                        ["role"] = m.Role,
                        ["content"] = m.Content,
                    };
                    if (m.Images is { Count: > 0 } images)
                    {
                        if (provider == ProviderTypes.Ollama)
                        {
                            node["images"] = new JsonArray(images
                                .Select(img => (JsonNode)JsonValue.Create(img.Base64Data)!)
                                .ToArray());
                        }
                        else
                        {
                            var parts = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = m.Content,
                                },
                            };
                            foreach (var img in images)
                            {
                                parts.Add(new JsonObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JsonObject
                                    {
                                        ["url"] = $"data:{img.MimeType};base64,{img.Base64Data}",
                                    },
                                });
                            }
                            node["content"] = parts;
                        }
                    }
                    if (m.ToolCallId is not null)
                    {
                        node["tool_call_id"] = m.ToolCallId;
                    }
                    if (m.ToolCallsJson is not null)
                    {
                        node["tool_calls"] = JsonNode.Parse(m.ToolCallsJson);
                    }
                    return (JsonNode)node;
                })
                .ToArray()),
            ["stream"] = stream,
        };

        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = new JsonArray(request.Tools
                .Select(t => JsonNode.Parse(t.GetRawText()))
                .ToArray());
        }

        if (request.Params is not null)
        {
            foreach (var (key, value) in request.Params)
            {
                payload[key] = value switch
                {
                    JsonElement e => JsonNode.Parse(e.GetRawText()),
                    bool b => b,
                    int i => i,
                    long l => l,
                    float f => f,
                    double d => d,
                    decimal m => m,
                    string s => s,
                    null => null,
                    _ => JsonSerializer.SerializeToNode(value, JsonOptions),
                };
            }
        }

        return payload;
    }

    private static string? FirstBaseUrl(ConnectionsConfig connections) =>
        connections.OllamaBaseUrls.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

    private async Task<string> CompleteOllamaAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var baseUrl = FirstBaseUrl(connections)
            ?? throw new InvalidOperationException("Nenhuma URL do Ollama configurada.");

        var payload = BuildPayload(request, stream: false, ProviderTypes.Ollama);
        using var content = new StringContent(
            payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await httpClientFactory.CreateClient().PostAsync(
            $"{TrimSlash(baseUrl)}/api/chat", content, ct);
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return json?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
    }

    private async Task<string> CompleteOpenAiAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var (index, baseUrl) = FirstOpenAiConnection(connections);

        var payload = BuildPayload(request, stream: false, ProviderTypes.OpenAi);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{TrimSlash(baseUrl)}/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var apiKey = connections.OpenAiApiKeys.ElementAtOrDefault(index);
        if (!string.IsNullOrEmpty(apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await httpClientFactory.CreateClient().SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return json?["choices"] is JsonArray { Count: > 0 } arr
            ? arr[0]?["message"]?["content"]?.GetValue<string>() ?? string.Empty
            : string.Empty;
    }

    private async IAsyncEnumerable<string> StreamOllamaAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var baseUrl = FirstBaseUrl(connections)
            ?? throw new InvalidOperationException("Nenhuma URL do Ollama configurada.");

        var payload = BuildPayload(request, stream: true, ProviderTypes.Ollama);
        // Ollama aceita "options" em vez de parâmetros de topo: move extras para lá.
        if (request.Params is { Count: > 0 })
        {
            var options = new JsonObject();
            foreach (var key in new[] { "temperature", "top_p", "top_k", "num_predict", "repeat_penalty", "seed", "stop" }
                .Where(payload.ContainsKey))
            {
                payload.Remove(key, out var value);
                options[MapOllamaParam(key)] = value;
            }

            if (options.Count > 0)
            {
                payload["options"] = options;
            }
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{TrimSlash(baseUrl)}/api/chat")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var done = node?["done"]?.GetValue<bool>() ?? false;
            var content = node?["message"]?["content"]?.GetValue<string>();
            var delta = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = content ?? string.Empty,
            };
            // Modelos "think" do Ollama: message.thinking sai separado.
            if (node?["message"]?["thinking"] is JsonValue think
                && think.TryGetValue<string>(out var thinking)
                && thinking.Length > 0)
            {
                delta["reasoning"] = thinking;
            }
            var chunk = new JsonObject
            {
                ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
                ["object"] = "chat.completion.chunk",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = request.Model,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = done ? "stop" : null,
                }),
            };

            yield return $"data: {chunk.ToJsonString()}";

            if (done)
            {
                yield return "data: [DONE]";
                yield break;
            }
        }

        yield return "data: [DONE]";
    }

    private static string MapOllamaParam(string key) => key switch
    {
        "max_tokens" => "num_predict",
        _ => key,
    };

    private async IAsyncEnumerable<string> StreamOpenAiAsync(
        ChatCompletionRequest request,
        ConnectionsConfig connections,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var (index, baseUrl) = FirstOpenAiConnection(connections);

        var payload = BuildPayload(request, stream: true, ProviderTypes.OpenAi);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{TrimSlash(baseUrl)}/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        var apiKey = connections.OpenAiApiKeys.ElementAtOrDefault(index);
        if (!string.IsNullOrEmpty(apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return line;
            if (line == "data: [DONE]")
            {
                yield break;
            }
        }
    }

    private static (int Index, string BaseUrl) FirstOpenAiConnection(ConnectionsConfig connections)
    {
        for (var i = 0; i < connections.OpenAiBaseUrls.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(connections.OpenAiBaseUrls[i]))
            {
                return (i, connections.OpenAiBaseUrls[i]);
            }
        }

        throw new InvalidOperationException("Nenhuma conexão OpenAI configurada.");
    }

    private static string TrimSlash(string url) => url.TrimEnd('/');
}

/// <summary>Resultado de uma completion com suporte a tool_calls.</summary>
/// <param name="Content">Texto final (pode ser vazio quando só há calls).</param>
/// <param name="ToolCalls">Chamadas de função pedidas pelo modelo.</param>
public sealed record ProviderCompletion(
    string Content,
    IReadOnlyList<ProviderToolCall> ToolCalls,
    string ToolCallsJson,
    /// <summary>Raciocínio do modelo (reasoning/thinking) quando o provider
    /// o expõe — nunca faz parte de <paramref name="Content"/>: vai para o
    /// canal próprio do stream e não deve ser persistido na mensagem.</param>
    string? Reasoning = null);

/// <summary>Chamada de função normalizada (Ollama/OpenAI).</summary>
/// <param name="Id">Id da chamada (echo em tool_call_id).</param>
/// <param name="Name">Nome da função.</param>
/// <param name="ArgumentsJson">Argumentos em JSON.</param>
public sealed record ProviderToolCall(string Id, string Name, string ArgumentsJson);
