using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
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
    HybridCache cache,
    IConfiguration cfg,
    PolicyService? policies = null)
{
    /// <summary>Limites de retry lidos de <c>Providers:*</c>
    /// (SPEC-20261016-provider-resilience).</summary>
    public RetryPolicyOptions RetryOptions { get; } =
        RetryPolicyOptions.FromConfig(cfg);

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
            // Indexer defensivo (SPEC-20261019-gemini-response-shape): lista
            // escalar ou sem "models" (gateway/proxy de erro) vira vazio em
            // vez de InvalidOperationException.
            foreach (var model in Prop(node, "models") as JsonArray ?? [])
            {
                var id = Str(Prop(model, "model")) ?? Str(Prop(model, "name"));
                if (!string.IsNullOrEmpty(id))
                {
                    // Ollama recente reporta capabilities no /api/tags (SPEC-20261013-model-roles).
                    var caps = (Prop(model, "capabilities") as JsonArray)
                        ?.Select(Str)
                        .OfType<string>()
                        .Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
                    models.Add(new ModelInfo(
                        id, Str(Prop(model, "name")) ?? id, "ollama", "ollama",
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
            foreach (var model in Prop(node, "data") as JsonArray ?? [])
            {
                var id = Str(Prop(model, "id"));
                if (!string.IsNullOrEmpty(id))
                {
                    models.Add(new ModelInfo(
                        id,
                        id,
                        "openai",
                        Str(Prop(model, "owned_by"))));
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
            foreach (var model in Prop(node, "data") as JsonArray ?? [])
            {
                var id = Str(Prop(model, "id"));
                if (!string.IsNullOrEmpty(id))
                {
                    models.Add(new ModelInfo(
                        id,
                        Str(Prop(model, "display_name")) ?? id,
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
            foreach (var model in Prop(node, "models") as JsonArray ?? [])
            {
                var name = Str(Prop(model, "name"));
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var id = name.StartsWith("models/", StringComparison.Ordinal)
                    ? name["models/".Length..]
                    : name;
                models.Add(new ModelInfo(
                    id,
                    Str(Prop(model, "displayName")) ?? id,
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
        request = ApplyProviderOverlay(request, provider, connections);

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

        // Policies admin (SPEC-20261018-policies-admin RF-003): hard-deny
        // por provider (provider.use) e por modelo (model.use) — avaliado
        // antes de qualquer request sair.
        if (policies is not null)
        {
            var deniedProvider = await policies.DenyAsync(PolicyService.ActionProviderUse,
                new[] { provider, $"{provider}/{request.Model}" }, userId, ct)
                ?? await policies.DenyAsync(PolicyService.ActionModelUse,
                    new[] { request.Model, $"{provider}/{request.Model}" }, userId, ct);
            if (deniedProvider is not null)
            {
                throw new InvalidOperationException(
                    PolicyService.DeniedMessage(deniedProvider, request.Model));
            }
        }

        request = ApplyProviderOverlay(request, provider, connections);
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
        request = ApplyProviderOverlay(request, provider, connections);
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
            ApplyExtraHeaders(httpRequest, request);
            using var response = await httpClientFactory.CreateClient()
                .SendAsync(httpRequest, ct);
            await EnsureSuccessOrProviderErrorAsync(response, ct);
            json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        else
        {
            var baseUrl = FirstBaseUrl(connections)
                ?? throw new InvalidOperationException("Nenhuma URL do Ollama configurada.");
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post, $"{TrimSlash(baseUrl)}/api/chat")
            {
                Content = new StringContent(
                    payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            ApplyExtraHeaders(httpRequest, request);
            using var response = await httpClientFactory.CreateClient()
                .SendAsync(httpRequest, ct);
            await EnsureSuccessOrProviderErrorAsync(response, ct);
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
        if (Str(Prop(node, "reasoning_content")) is { Length: > 0 } s)
        {
            return s;
        }
        if (Str(Prop(node, "reasoning")) is { Length: > 0 } r)
        {
            return r;
        }
        if (Prop(node, "reasoning_details") is JsonArray details)
        {
            var joined = string.Concat(details.Select(d => Str(Prop(d, TextKey))));
            return joined.Length > 0 ? joined : null;
        }
        return null;
    }

    /// <summary>
    /// Acesso defensivo a propriedade de nó: providers/gateways devolvem
    /// String/Number onde o schema espera Object — o indexer cru lançava
    /// <see cref="InvalidOperationException"/> e derrubava a run inteira
    /// (SPEC-20261019-gemini-response-shape). Não-objeto → null.
    /// </summary>
    private static JsonNode? Prop(JsonNode? node, string name) =>
        node is JsonObject obj ? obj[name] : null;

    /// <summary>Leitura defensiva de string — só JsonValue realmente textual.</summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Leitura defensiva de inteiro — só JsonValue realmente numérico.</summary>
    private static int? IntNum(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    /// <summary>
    /// <c>{"error": ...}</c> no meio de um stream SSE: monta
    /// <see cref="ProviderHttpException"/> classificável pela retry
    /// policy — rate/quota → 429, janela de contexto → 400 (o detector de
    /// overflow lê o corpo), demais → 502 transitório. O nó de erro pode
    /// ser string solta ou objeto <c>{message, type}</c>.
    /// </summary>
    private static ProviderHttpException MidStreamProviderError(JsonNode errorNode)
    {
        var message = Str(errorNode) ?? Str(Prop(errorNode, MessageKey))
            ?? "erro de provider não identificado";
        var snippet = errorNode.ToJsonString();
        if (snippet.Length > ProviderHttpException.MaxBodyChars)
        {
            snippet = snippet[..ProviderHttpException.MaxBodyChars];
        }
        var status = HttpStatusCode.BadGateway;
        if (message.Contains("rate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("quota", StringComparison.OrdinalIgnoreCase))
        {
            status = HttpStatusCode.TooManyRequests;
        }
        else if (message.Contains("context", StringComparison.OrdinalIgnoreCase)
            || message.Contains("token", StringComparison.OrdinalIgnoreCase)
            || message.Contains("length", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too long", StringComparison.OrdinalIgnoreCase))
        {
            status = HttpStatusCode.BadRequest;
        }
        return new ProviderHttpException(status, null, snippet, midStream: true);
    }

    /// <summary>
    /// Texto de um campo que pode chegar como string OU como array de
    /// parts <c>[{type:"text",text:"..."}]</c> (shape multimodal que
    /// gateways devolvem no lugar do content plano).
    /// </summary>
    private static string CoerceText(JsonNode? node) =>
        Str(node) ?? (node is JsonArray parts
            ? string.Concat(parts.Select(p => Str(Prop(p, TextKey))))
            : string.Empty);

    /// <summary>Normaliza <c>choices[0].message</c> (OpenAI) ou <c>message</c> (Ollama).</summary>
    private ProviderCompletion ParseCompletionMessage(JsonNode? json, bool openAi)
    {
        // Resposta buffered com {"error":...} (gateway devolve 200 + corpo
        // de erro): classifica como erro de provider em vez de completion
        // vazia silenciosa.
        if (Prop(json, ErrorKey) is { } bufferedError)
        {
            throw MidStreamProviderError(bufferedError);
        }
        var firstChoice = Prop(json, ChoicesKey) is JsonArray { Count: > 0 } arr ? arr[0] : null;
        var message = openAi ? Prop(firstChoice, MessageKey) : Prop(json, MessageKey);
        var (content, reasoning, dsmlCalls) = ExtractDsmlFields(message, openAi);
        var calls = new List<ProviderToolCall>();
        var callsNode = Prop(message, ToolCallsKey) as JsonArray;
        var callsJson = callsNode?.ToJsonString() ?? "[]";
        if (callsNode is not null)
        {
            foreach (var call in callsNode)
            {
                var function = Prop(call, FunctionKey);
                var name = Str(Prop(function, "name"));
                if (name is null)
                {
                    continue;
                }
                var args = Prop(function, ArgumentsKey);
                var argsJson = Str(args) ?? args?.ToJsonString() ?? "{}";
                calls.Add(new ProviderToolCall(
                    Str(Prop(call, "id")) ?? Guid.NewGuid().ToString("N"),
                    name, argsJson));
            }
        }
        if (dsmlCalls.Count > 0)
        {
            calls.AddRange(dsmlCalls);
            // Echo no formato OpenAI: o callsJson precisa incluir as calls
            // sintetizadas para o modelo ver na próxima rodada.
            callsJson = SerializeToolCallsEcho(calls);
        }

        return new ProviderCompletion(content, calls, callsJson, reasoning,
            ParseUsage(json, openAi),
            FinishReason: Str(Prop(firstChoice, "finish_reason")));
    }

    /// <summary>
    /// Extrai o uso de tokens da resposta (RF-004 SPEC-20261016-agent-
    /// steps-usage): OpenAI <c>usage.{prompt_tokens,completion_tokens,
    /// completion_tokens_details.reasoning_tokens}</c>; Ollama
    /// <c>{prompt_eval_count,eval_count}</c> (buffered ou linha
    /// <c>done:true</c> do NDJSON). Ausente → null (nunca estima).
    /// </summary>
    internal static ProviderUsage? ParseUsage(JsonNode? root, bool openAi)
    {
        if (openAi)
        {
            var u = Prop(root, "usage");
            if (IntNum(Prop(u, "prompt_tokens")) is not { } prompt)
            {
                return null;
            }
            var completion = IntNum(Prop(u, "completion_tokens")) ?? 0;
            var reasoning = IntNum(Prop(
                Prop(u, "completion_tokens_details"), "reasoning_tokens"));
            return new ProviderUsage(prompt, completion, reasoning);
        }

        var promptEval = IntNum(Prop(root, "prompt_eval_count"));
        var eval = IntNum(Prop(root, "eval_count"));
        return promptEval is null && eval is null
            ? null
            : new ProviderUsage(promptEval ?? 0, eval ?? 0);
    }

    /// <summary>Limpa markup DSML (DeepSeek) e tags de raciocínio inline /
    /// special tokens de content/reasoning de uma message OpenAI-compatível
    /// e devolve as calls extraídas.</summary>
    private (string Content, string? Reasoning, List<ProviderToolCall> DsmlCalls)
        ExtractDsmlFields(JsonNode? message, bool openAi)
    {
        var (content, dsmlCalls) = DsmlToolCallFilter.Extract(
            CoerceText(Prop(message, ContentKey)));
        var rawReasoning = openAi
            ? ExtractOpenAiReasoning(message)
            : Str(Prop(message, ThinkingKey));
        if (rawReasoning is not null)
        {
            var (clean, moreCalls) = DsmlToolCallFilter.Extract(rawReasoning);
            rawReasoning = clean.Length > 0 ? clean : null;
            dsmlCalls.AddRange(moreCalls);
        }
        // Tags de raciocínio inline (<think>…, <|channel>…) e special
        // tokens <|…|>: o interno vai ao canal reasoning, os marcadores
        // somem do content (paridade upstream tag_output_handler).
        var (cleanContent, lifted) = ReasoningMarkupFilter.Extract(content);
        content = cleanContent;
        if (rawReasoning is not null)
        {
            var (cleanReasoning, _) = ReasoningMarkupFilter.Extract(
                rawReasoning, keepInnerText: true);
            rawReasoning = cleanReasoning.Length > 0 ? cleanReasoning : null;
        }
        if (lifted.Length > 0)
        {
            rawReasoning = (rawReasoning ?? string.Empty) + lifted;
        }
        if (dsmlCalls.Count > 0)
        {
            logger.LogInformation(
                "{Count} tool call(s) extraídas de markup DSML na resposta buffered ({Names}) — o gateway não emitiu tool_calls estruturado.",
                dsmlCalls.Count, string.Join(", ", dsmlCalls.Select(c => c.Name).Distinct()));
        }
        return (content, rawReasoning, dsmlCalls);
    }

    /// <summary>Serializa calls no shape <c>tool_calls</c> OpenAI (echo p/
    /// o modelo ver as chamadas na próxima rodada).</summary>
    private static string SerializeToolCallsEcho(IEnumerable<ProviderToolCall> calls) =>
        JsonSerializer.Serialize(calls.Select(c => new
        {
            id = c.Id,
            type = FunctionKey,
            function = new { name = c.Name, arguments = c.ArgumentsJson },
        }));

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
        request = ApplyProviderOverlay(request, provider, connections);
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
        catch (ProviderHttpException ex) when (!ex.MidStream)
        {
            // Erro da RESPOSTA inicial (não-2xx): a tentativa streamed é só
            // um probe — devolve null e a reposição buffered refaz o pedido;
            // o erro da reposição é quem a retry policy classifica (1 retry
            // por round, contabilidade que os testes de resiliência travam).
            return null;
        }
        catch (Exception ex) when ((ex is HttpRequestException and not ProviderHttpException)
            || ex is IOException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Falha de transporte/setup antes da resposta — o chamador
            // refaz a rodada buffered. Erros mid-stream chegam aqui também
            // (timeout do HttpClient com o ct da run íntegro conta como
            // stream morto; cancelamento do usuário propaga): o texto já
            // emitido vira mensagem assistant parcial e a reposição pede
            // continuação (RF-006) em vez de recomeçar.
            // ProviderHttpException mid-stream NÃO entra em nenhum dos dois
            // catches: {"error":...} dentro do SSE sobe para a retry policy
            // classificar — engoli-lo re-pediria sem backoff e a reposição
            // buffered quebraria no parse do corpo SSE
            // (SPEC-20261019-gemini-response-shape RF-003).
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
        ApplyExtraHeaders(httpRequest, request);

        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);

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
        // Saneamento do texto do provider: DSML (tool calls markup →
        // ProviderToolCall) + tags de raciocínio inline / special tokens
        // <|…|> — markup nunca vaza na UI nem no content persistido.
        var dsmlContent = new DeltaFilterChain();
        var dsmlReasoning = new DeltaFilterChain(forReasoning: true);

        // Diagnóstico do round: contadores estruturais alimentam o warning de
        // "round vazio" (a run que termina sem resposta nem tool call) e o
        // trace opcional por chunk (OPENWEBUI_LOG_UPSTREAM_STREAM=1).
        var traceUpstream = string.Equals(
            Environment.GetEnvironmentVariable("OPENWEBUI_LOG_UPSTREAM_STREAM"),
            "1", StringComparison.Ordinal);
        var chunks = 0;
        var toolCallParts = 0;
        var nonArrayToolCalls = 0;
        var nonObjectChunks = 0;
        var reasoningChars = 0;
        var sawDsmlMarker = false;
        string? finishReason = null;
        string? upstreamModel = null;
        ProviderUsage? streamUsage = null;

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
            chunks++;
            sawDsmlMarker |= data.Contains("DSML", StringComparison.Ordinal);
            // Chunk fora de objeto — heartbeat serializado como escalar
            // ("keepalive", 42, true): nada a extrair, e o indexer cru era
            // o que derrubava a run com 'Object' esperado vs 'String'
            // (SPEC-20261019-gemini-response-shape RF-002).
            if (node is not JsonObject)
            {
                nonObjectChunks++;
                continue;
            }

            // {"error": ...} mid-stream: erro honesto de provider que a
            // retry policy classifica — antes era engolido em silêncio.
            if (Prop(node, ErrorKey) is { } errorNode)
            {
                throw MidStreamProviderError(errorNode);
            }

            // Gateways como o OmniRoute roteiam o alias pedido para backends
            // diferentes por request — registrar o modelo real que respondeu
            // explica diferenças de comportamento (tool calling, tetos).
            // Chunks de heartbeat chegam com model="keepalive": ignorar.
            if (upstreamModel is null
                && Str(Prop(node, "model")) is { } m
                && m != "keepalive")
            {
                upstreamModel = m;
            }

            var choice = Prop(node, ChoicesKey) is JsonArray { Count: > 0 } arr ? arr[0] : null;
            var delta = Prop(choice, DeltaKey);
            if (Str(Prop(choice, "finish_reason")) is { Length: > 0 } fr)
            {
                finishReason = fr;
            }
            // Chunk final com stream_options.include_usage traz usage
            // sem choices (SPEC-20261016-agent-steps-usage RF-004).
            if (Prop(node, "usage") is not null)
            {
                streamUsage = ParseUsage(node, openAi: true);
            }
            // content chega string ou array de parts (CoerceText); um delta
            // escalar solto — gateway que não estrutura o objeto — vale
            // como o próprio texto.
            var piece = CoerceText(Prop(delta, ContentKey));
            if (piece.Length == 0 && delta is JsonValue)
            {
                piece = Str(delta) ?? string.Empty;
            }
            if (!string.IsNullOrEmpty(piece))
            {
                var visible = dsmlContent.Feed(piece);
                if (visible.Length > 0)
                {
                    content.Append(visible);
                    if (onDelta is not null)
                    {
                        await onDelta(visible, ct);
                    }
                }
                // Raciocínio extraído de tags inline (<think>…,
                // <|channel>…) dentro do content sai pelo canal próprio.
                if (onReasoning is not null
                    && dsmlContent.Markup.TakeReasoning() is { Length: > 0 } lifted)
                {
                    await onReasoning(lifted, ct);
                }
            }

            // Reasoning/thinking de gateways OpenAI-compatíveis (deepseek,
            // openrouter, OmniRoute): reasoning_content | reasoning |
            // reasoning_details[].text — canal separado do content.
            if (ExtractOpenAiReasoning(delta) is { Length: > 0 } thought)
            {
                reasoningChars += thought.Length;
                if (onReasoning is not null)
                {
                    var clean = dsmlReasoning.Feed(thought);
                    if (clean.Length > 0)
                    {
                        await onReasoning(clean, ct);
                    }
                }
            }

            var callsNode = Prop(delta, ToolCallsKey);
            if (callsNode is not JsonArray callParts)
            {
                // Gateway emitiu tool_calls fora do shape OpenAI (objeto
                // solto, string etc.) — contar em vez de engolir em silêncio.
                if (callsNode is not null)
                {
                    nonArrayToolCalls++;
                }
                continue;
            }
            toolCallParts += callParts.Count;

            foreach (var part in callParts)
            {
                var position = IntNum(Prop(part, IndexKey)) ?? callBuffers.Count;
                if (!callBuffers.TryGetValue(position, out var buffer))
                {
                    buffer = new OpenAiToolCallBuffer();
                    callBuffers[position] = buffer;
                }
                buffer.Id = Str(Prop(part, "id")) ?? buffer.Id;
                var function = Prop(part, FunctionKey);
                var name = Str(Prop(function, "name"));
                if (name is not null)
                {
                    buffer.Name = name;
                }
                // arguments chega fragmentado como string; alguns gateways
                // mandam o objeto já montado — serializa em vez de quebrar.
                var argsNode = Prop(function, ArgumentsKey);
                if (Str(argsNode) is { } args)
                {
                    buffer.Args.Append(args);
                }
                else if (argsNode is not null)
                {
                    buffer.Args.Append(argsNode.ToJsonString());
                }
            }
        }

        // [DONE]: solta o que os filtros seguraram (texto normal segue;
        // markup truncado já entregou os invokes completos que couberam).
        var tailContent = dsmlContent.Flush();
        if (tailContent.Length > 0)
        {
            content.Append(tailContent);
            if (onDelta is not null)
            {
                await onDelta(tailContent, ct);
            }
        }
        var tailReasoning = dsmlReasoning.Flush() + dsmlContent.Markup.TakeReasoning();
        if (tailReasoning.Length > 0 && onReasoning is not null)
        {
            await onReasoning(tailReasoning, ct);
        }

        var calls = callBuffers.Values
            .Where(b => b.Name is not null)
            .Select(b => new ProviderToolCall(
                b.Id ?? Guid.NewGuid().ToString("N"),
                b.Name!,
                b.Args.Length > 0 ? b.Args.ToString() : "{}"))
            .ToList();
        calls.AddRange(dsmlContent.Calls);
        calls.AddRange(dsmlReasoning.Calls);
        if (dsmlContent.Calls.Count + dsmlReasoning.Calls.Count is > 0 and var dsmlCount)
        {
            logger.LogInformation(
                "{Count} tool call(s) extraídas de markup DSML no texto do stream ({Names}) — o gateway não emitiu tool_calls estruturado.",
                dsmlCount,
                string.Join(", ", dsmlContent.Calls.Concat(dsmlReasoning.Calls)
                    .Select(c => c.Name).Distinct()));
        }

        // Observabilidade do round: o trace por-chunk é opt-in; o "round
        // vazio" (sem content e sem calls — a run morre em silêncio na UI)
        // sempre loga, junto dos sinais que explicam a causa.
        if (traceUpstream)
        {
            logger.LogInformation(
                "Upstream stream: model={UpstreamModel}, {Chunks} chunks, "
                + "{ToolCallParts} tool_call part(s), "
                + "{NonArrayToolCalls} tool_calls fora de array, "
                + "{NonObjectChunks} chunks escalares, finish_reason={FinishReason}, "
                + "dsmlMarker={DsmlSeen}, content={ContentChars} chars, reasoning={ReasoningChars} chars.",
                upstreamModel ?? "(none)", chunks, toolCallParts, nonArrayToolCalls,
                nonObjectChunks, finishReason ?? "(none)", sawDsmlMarker,
                content.Length, reasoningChars);
        }
        if (calls.Count == 0 && content.Length == 0)
        {
            logger.LogWarning(
                "Round streamed encerrou vazio: model={UpstreamModel}, {Chunks} chunks, "
                + "reasoning={ReasoningChars} chars, "
                + "finish_reason={FinishReason}, dsmlMarker={DsmlSeen}, "
                + "tool_call_parts={ToolCallParts}, tool_calls_fora_de_array={NonArray}, "
                + "chunks_escalares={NonObject}.",
                upstreamModel ?? "(none)", chunks, reasoningChars,
                finishReason ?? "(none)", sawDsmlMarker, toolCallParts,
                nonArrayToolCalls, nonObjectChunks);
        }
        else if (nonArrayToolCalls > 0)
        {
            logger.LogWarning(
                "{Count} chunk(s) com tool_calls fora do array OpenAI ignorados no stream.",
                nonArrayToolCalls);
        }

        var callsJson = SerializeToolCallsEcho(calls);
        return new ProviderCompletion(
            content.ToString(), calls, callsJson, Usage: streamUsage,
            FinishReason: finishReason);
    }

    /// <summary>Buffer de uma tool_call fragmentada no stream OpenAI.</summary>
    private sealed class OpenAiToolCallBuffer
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Args { get; } = new();
    }

    /// <summary>
    /// Filtro streaming-safe de tool calls no markup DSML do DeepSeek
    /// (<c>&lt;｜DSML｜tool_calls&gt;…&lt;/｜DSML｜tool_calls&gt;</c>):
    /// alguns gateways OpenAI-compatíveis entregam as chamadas como texto
    /// dentro de <c>reasoning_content</c>/<c>content</c> em vez do campo
    /// <c>tool_calls</c> — sem o filtro o markup vaza na UI e a tool nunca
    /// executa (a run "trava"). O filtro segura apenas o que pode ser o
    /// início do marcador, converte invokes completos em
    /// <see cref="ProviderToolCall"/> e some com o markup.
    /// </summary>
    private sealed class DsmlToolCallFilter
    {
        private const string OpenMarker = "<｜DSML｜tool_calls>";
        private const string CloseMarker = "</｜DSML｜tool_calls>";
        /// <summary>Bloco aberto além desse tamanho é desistido (modelo quebrado).</summary>
        private const int MaxBlockLength = 256 * 1024;

        private static readonly Regex InvokePattern = new(
            "<｜DSML｜invoke name=\"(?<name>[^\"]+)\"\\s*>(?<body>.*?)</｜DSML｜invoke>",
            RegexOptions.Singleline | RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex ParameterPattern = new(
            "<｜DSML｜parameter name=\"(?<name>[^\"]+)\"(?<attrs>[^>]*)>(?<value>.*?)</｜DSML｜parameter>",
            RegexOptions.Singleline | RegexOptions.Compiled, RegexTimeout);
        private static readonly Regex StringAttrPattern = new(
            "string=\"(?<v>true|false)\"", RegexOptions.Compiled, RegexTimeout);

        private readonly StringBuilder _pending = new();
        private readonly List<ProviderToolCall> _calls = [];
        private int _syntheticId;

        /// <summary>Calls já extraídas de blocos completos.</summary>
        public IReadOnlyList<ProviderToolCall> Calls => _calls;

        /// <summary>Alimenta um pedaço; devolve só o que é seguro exibir.</summary>
        public string Feed(string piece)
        {
            _pending.Append(piece);
            return Drain(flush: false);
        }

        /// <summary>Fim do stream: devolve o resto seguro; bloco truncado
        /// ainda entrega os invokes completos que couberem.</summary>
        public string Flush() => Drain(flush: true);

        /// <summary>Uso one-shot (resposta buffered): limpa o texto e coleta
        /// as calls de uma vez.</summary>
        public static (string Text, List<ProviderToolCall> Calls) Extract(string text)
        {
            var filter = new DsmlToolCallFilter();
            var visible = filter.Feed(text) + filter.Flush();
            return (visible, [.. filter.Calls]);
        }

        private string Drain(bool flush)
        {
            var text = _pending.ToString();
            _pending.Clear();
            var output = new StringBuilder();
            var pos = 0;
            while (pos < text.Length)
            {
                var openIdx = text.IndexOf(OpenMarker, pos, StringComparison.Ordinal);
                if (openIdx < 0)
                {
                    break;
                }
                output.Append(text, pos, openIdx - pos);
                var closeIdx = text.IndexOf(
                    CloseMarker, openIdx + OpenMarker.Length, StringComparison.Ordinal);
                if (closeIdx >= 0)
                {
                    ParseInvokes(text.AsSpan(openIdx, closeIdx + CloseMarker.Length - openIdx));
                    pos = closeIdx + CloseMarker.Length;
                    continue;
                }
                var tail = text.Length - openIdx;
                if (!flush && tail <= MaxBlockLength)
                {
                    _pending.Append(text, openIdx, tail);
                    return output.ToString();
                }
                ParseInvokes(text.AsSpan(openIdx));
                pos = text.Length;
            }
            // Sem bloco aberto restante: emite tudo menos o sufixo que pode
            // ser o começo de um novo marcador.
            var hold = flush ? 0 : HeldSuffixLength(text, pos);
            var safe = text.Length - pos - hold;
            if (safe > 0)
            {
                output.Append(text, pos, safe);
            }
            if (hold > 0)
            {
                _pending.Append(text, text.Length - hold, hold);
            }
            return output.ToString();
        }

        /// <summary>Tamanho do sufixo de <paramref name="text"/> (depois de
        /// <paramref name="pos"/>) que é prefixo do marcador de abertura —
        /// não pode ser emitido ainda porque pode virar um bloco DSML.</summary>
        private static int HeldSuffixLength(string text, int pos)
        {
            var max = Math.Min(OpenMarker.Length - 1, text.Length - pos);
            for (var len = max; len > 0; len--)
            {
                if (text.AsSpan(text.Length - len, len)
                    .SequenceEqual(OpenMarker.AsSpan(0, len)))
                {
                    return len;
                }
            }
            return 0;
        }

        /// <summary>Converte os invokes de um bloco (completo ou truncado)
        /// em calls normalizadas; fragmentos incompletos são descartados.</summary>
        private void ParseInvokes(ReadOnlySpan<char> block)
        {
            foreach (var invokeGroups in InvokePattern.Matches(block.ToString())
                .Select(m => m.Groups))
            {
                var args = new JsonObject();
                foreach (var paramGroups in ParameterPattern.Matches(
                    invokeGroups["body"].Value).Select(m => m.Groups))
                {
                    var name = paramGroups["name"].Value;
                    var raw = paramGroups["value"].Value;
                    var isString = StringAttrPattern.Match(
                        paramGroups["attrs"].Value) is not { Success: true } attr
                        || attr.Groups["v"].Value != "false";
                    if (isString)
                    {
                        args[name] = raw;
                        continue;
                    }
                    JsonNode? parsed = null;
                    try
                    {
                        parsed = JsonNode.Parse(raw.Trim());
                    }
                    catch (JsonException)
                    {
                        // valor não-JSON com string="false" — vai como texto
                    }
                    args[name] = parsed ?? JsonValue.Create(raw);
                }
                _calls.Add(new ProviderToolCall(
                    $"dsml_{_syntheticId++}",
                    invokeGroups["name"].Value,
                    args.ToJsonString(JsonOptions)));
            }
        }
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
        ApplyExtraHeaders(httpRequest, request);

        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var callNodes = new List<JsonNode>();
        // Modelos harmony no Ollama (gpt-oss) também emitem <|…|> inline —
        // mesma sanitização dos caminhos OpenAI (sem DSML: Ollama nativo
        // não produz o markup DeepSeek).
        var markupContent = new ReasoningMarkupFilter();
        var markupReasoning = new ReasoningMarkupFilter(keepInnerText: true);
        ProviderUsage? usage = null;
        string? doneReason = null;
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

            if (Prop(node, ErrorKey) is { } errorNode)
            {
                throw MidStreamProviderError(errorNode);
            }
            var message = Prop(node, MessageKey);
            var piece = CoerceText(Prop(message, ContentKey));
            if (!string.IsNullOrEmpty(piece))
            {
                var visible = markupContent.Feed(piece);
                if (visible.Length > 0)
                {
                    content.Append(visible);
                    if (onDelta is not null)
                    {
                        await onDelta(visible, ct);
                    }
                }
                if (onReasoning is not null
                    && markupContent.TakeReasoning() is { Length: > 0 } lifted)
                {
                    await onReasoning(lifted, ct);
                }
            }

            // Modelos "think" do Ollama emitem message.thinking separado
            // de message.content na mesma linha NDJSON.
            if (onReasoning is not null
                && Str(Prop(message, ThinkingKey)) is { Length: > 0 } thought)
            {
                var clean = markupReasoning.Feed(thought);
                if (clean.Length > 0)
                {
                    await onReasoning(clean, ct);
                }
            }

            // Linha final done:true carrega prompt_eval_count/eval_count
            // (SPEC-20261016-agent-steps-usage RF-004).
            if (ParseUsage(node, openAi: false) is { } lineUsage)
            {
                usage = lineUsage;
            }
            if (Prop(message, ToolCallsKey) is JsonArray { Count: > 0 } calls)
            {
                callNodes.AddRange(calls.Select(c => c!.DeepClone()));
            }
            // done_reason da linha final do Ollama ("stop"/"length") — RF-003
            // do provider-resilience: continuação automática em truncamento.
            if (Str(Prop(node, "done_reason")) is { Length: > 0 } dr)
            {
                doneReason = dr;
            }
        }

        // Fim do NDJSON: solta o que os filtros seguraram.
        var ollamaTail = markupContent.Flush();
        if (ollamaTail.Length > 0)
        {
            content.Append(ollamaTail);
            if (onDelta is not null)
            {
                await onDelta(ollamaTail, ct);
            }
        }
        var ollamaTailReasoning = markupReasoning.Flush()
            + markupContent.TakeReasoning();
        if (ollamaTailReasoning.Length > 0 && onReasoning is not null)
        {
            await onReasoning(ollamaTailReasoning, ct);
        }

        var callsJson = new JsonArray(callNodes.ToArray());
        var parsed = new List<ProviderToolCall>();
        foreach (var call in callNodes)
        {
            var function = Prop(call, FunctionKey);
            var name = Str(Prop(function, "name"));
            if (name is null)
            {
                continue;
            }
            var args = Prop(function, ArgumentsKey);
            var argsJson = Str(args) ?? args?.ToJsonString() ?? "{}";
            parsed.Add(new ProviderToolCall(
                Str(Prop(call, "id")) ?? Guid.NewGuid().ToString("N"),
                name, argsJson));
        }

        return new ProviderCompletion(
            content.ToString(), parsed, callsJson.ToJsonString(), Usage: usage,
            FinishReason: doneReason);
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
    private const string ContentKey = "content";
    private const string MessageKey = "message";
    private const string ErrorKey = "error";
    private const string ChoicesKey = "choices";
    private const string ToolCallsKey = "tool_calls";
    private const string FunctionKey = "function";
    private const string ArgumentsKey = "arguments";
    private const string ThinkingKey = "thinking";
    private const string DeltaKey = "delta";
    private const string IndexKey = "index";
    private const string CandidatesKey = "candidates";
    private const string TextKey = "text";
    private const string PartsKey = "parts";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static ProviderConnection FindTypedConnection(
        ConnectionsConfig connections, string type) =>
        connections.ProvidersOrEmpty.FirstOrDefault(p =>
            string.Equals(p.Type, type, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Nenhuma conexão '{type}' configurada.");

    private static HttpRequestMessage NewAnthropicRequest(
        ProviderConnection connection, JsonObject payload,
        ChatCompletionRequest? request = null)
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
        if (request is not null)
        {
            ApplyExtraHeaders(httpRequest, request);
        }
        return httpRequest;
    }

    /// <summary>
    /// Converte a conversa OpenAI-shaped para o formato da Messages API:
    /// <c>system</c> vira campo top-level, mensagens <c>tool</c> viram blocos
    /// <c>tool_result</c> numa mensagem user consecutiva, <c>tool_calls</c> do
    /// assistant viram blocos <c>tool_use</c> com <c>input</c> objeto e tools
    /// OpenAI viram <c>{name, description, input_schema}</c>.
    /// </summary>
    private static JsonObject BuildAnthropicPayload(
        ChatCompletionRequest request, bool stream, bool cacheAllowed = true)
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
                    [ContentKey] = m.Content,
                };
                if (messages.Count > 0
                    && messages[^1]["role"]?.GetValue<string>() == "user"
                    && messages[^1][ContentKey] is JsonArray prev
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
                        [ContentKey] = new JsonArray(block),
                    });
                }
                continue;
            }

            if (m.Role == "assistant" && m.ToolCallsJson is not null)
            {
                var blocks = new JsonArray();
                if (!string.IsNullOrEmpty(m.Content))
                {
                    blocks.Add(new JsonObject { ["type"] = TextKey, [TextKey] = m.Content });
                }
                foreach (var call in JsonNode.Parse(m.ToolCallsJson)!.AsArray())
                {
                    var name = call?[FunctionKey]?["name"]?.GetValue<string>();
                    if (name is null)
                    {
                        continue;
                    }
                    var argsRaw = call?[FunctionKey]?[ArgumentsKey];
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
                    [ContentKey] = blocks,
                });
                continue;
            }

            // SPEC-20261013-chat-media-attachments RF-006: imagens do anexo
            // viram blocos {type:"image", source:{base64}} na Messages API.
            if (m.Images is { Count: > 0 } images && m.Role == "user")
            {
                var blocks = new JsonArray
                {
                    new JsonObject { ["type"] = TextKey, [TextKey] = m.Content },
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
                    [ContentKey] = blocks,
                });
                continue;
            }

            messages.Add(new JsonObject
            {
                ["role"] = m.Role == "assistant" ? "assistant" : "user",
                [ContentKey] = m.Content,
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
                var fn = JsonNode.Parse(tool.GetRawText())?[FunctionKey];
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

        // SPEC-20261018-model-variants RF-003: cache_format anthropic →
        // cache_control ephemeral em system/tools/última user text; e
        // overlay de body da variant por cima de tudo.
        if (cacheAllowed && string.Equals(request.CacheFormat, "anthropic",
                StringComparison.OrdinalIgnoreCase))
        {
            ApplyAnthropicCacheControl(payload);
        }
        if (request.ExtraBody is { Count: > 0 } overlay)
        {
            ModelVariantService.DeepMerge(payload, overlay);
        }

        return payload;
    }

    /// <summary>
    /// cacheFormat:"anthropic" (RF-003): cache_control {type:"ephemeral"}
    /// no system, em cada tool e no último bloco text da última user msg —
    /// reaproveita o prefixo do prompt/tools entre turnos.
    /// </summary>
    private static void ApplyAnthropicCacheControl(JsonObject payload)
    {
        JsonObject Marker() => new() { ["type"] = "ephemeral" };
        if (payload["system"] is JsonValue sys)
        {
            // System como array de blocos habilita cache_control por bloco.
            payload["system"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = TextKey,
                    [TextKey] = sys.GetValue<string>(),
                    ["cache_control"] = Marker(),
                });
        }
        if (payload["tools"] is JsonArray tools && tools.Count > 0
            && tools[^1] is JsonObject lastTool)
        {
            lastTool["cache_control"] = Marker();
        }
        if (payload["messages"] is JsonArray msgs
            && msgs.LastOrDefault(m =>
                m?["role"]?.GetValue<string>() == "user") is JsonObject lastUser)
        {
            if (lastUser[ContentKey] is JsonArray blocks
                && blocks.LastOrDefault(b =>
                    b?["type"]?.GetValue<string>() == TextKey) is JsonObject textBlock)
            {
                textBlock["cache_control"] = Marker();
            }
            else if (lastUser[ContentKey] is JsonValue)
            {
                lastUser[ContentKey] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = TextKey,
                        [TextKey] = lastUser[ContentKey]!.DeepClone(),
                        ["cache_control"] = Marker(),
                    });
            }
        }
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
        // Indexers defensivos (SPEC-20261019-gemini-response-shape): bloco
        // escalar ou content fora de array não pode derrubar a run.
        foreach (var block in Prop(json, ContentKey) as JsonArray ?? [])
        {
            switch (Str(Prop(block, "type")))
            {
                case TextKey:
                    content.Append(Str(Prop(block, TextKey)));
                    break;
                case ThinkingKey:
                    // Bloco de raciocínio (extended thinking): canal separado —
                    // não entra no Content persistido.
                    reasoning.Append(Str(Prop(block, ThinkingKey)));
                    break;
                case "tool_use":
                    var id = Str(Prop(block, "id"))
                        ?? Guid.NewGuid().ToString("N");
                    var name = Str(Prop(block, "name")) ?? string.Empty;
                    var input = Prop(block, "input")?.ToJsonString() ?? "{}";
                    calls.Add(new ProviderToolCall(id, name, input));
                    callsJson.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = FunctionKey,
                        [FunctionKey] = new JsonObject
                        {
                            ["name"] = name,
                            [ArgumentsKey] = input,
                        },
                    });
                    break;
            }
        }
        // Mesma sanitização dos caminhos OpenAI: markup inline nunca
        // alcança o Content persistido (defesa para gateways compatíveis).
        var (cleanText, lifted) = ReasoningMarkupFilter.Extract(content.ToString());
        var (cleanThinking, _) = ReasoningMarkupFilter.Extract(
            reasoning.ToString(), keepInnerText: true);
        reasoning.Clear().Append(cleanThinking).Append(lifted);
        return new ProviderCompletion(
            cleanText, calls, callsJson.ToJsonString(),
            reasoning.Length > 0 ? reasoning.ToString() : null,
            FinishReason: Str(Prop(json, "stop_reason")));
    }

    private async Task<string> CompleteAnthropicAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var connection = FindTypedConnection(connections, "anthropic");
        var payload = BuildAnthropicPayload(request, stream: false,
            cacheAllowed: !IsOmniroute(connection.BaseUrl));
        using var httpRequest = NewAnthropicRequest(connection, payload, request);
        using var response = await httpClientFactory.CreateClient().SendAsync(httpRequest, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseAnthropicResponse(json).Content;
    }

    private async Task<ProviderCompletion> CompleteAnthropicWithToolsAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var connection = FindTypedConnection(connections, "anthropic");
        var payload = BuildAnthropicPayload(request, stream: false,
            cacheAllowed: !IsOmniroute(connection.BaseUrl));
        using var httpRequest = NewAnthropicRequest(connection, payload, request);
        using var response = await httpClientFactory.CreateClient().SendAsync(httpRequest, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);
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
        var payload = BuildAnthropicPayload(request, stream: true,
            cacheAllowed: !IsOmniroute(connection.BaseUrl));
        using var httpRequest = NewAnthropicRequest(connection, payload, request);
        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        await foreach (var (eventName, data) in ReadSseEventsAsync(reader, ct))
        {
            if (Prop(data, ErrorKey) is { } errorNode)
            {
                throw MidStreamProviderError(errorNode);
            }
            if (eventName == "content_block_delta")
            {
                var deltaType = Str(Prop(Prop(data, DeltaKey), "type"));
                // thinking_delta sai como delta.reasoning (canal separado);
                // signature_delta (metadado criptográfico) é descartada.
                var (field, prop) = deltaType switch
                {
                    "text_delta" => (ContentKey, TextKey),
                    "thinking_delta" => ("reasoning", ThinkingKey),
                    _ => (null, null),
                };
                if (field is not null && prop is not null
                    && Str(Prop(Prop(data, DeltaKey), prop)) is { Length: > 0 } text)
                {
                    var chunk = new JsonObject
                    {
                        ["object"] = "chat.completion.chunk",
                        [ChoicesKey] = new JsonArray(new JsonObject
                        {
                            [IndexKey] = 0,
                            [DeltaKey] = new JsonObject { [field] = text },
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
        var payload = BuildAnthropicPayload(request, stream: true,
            cacheAllowed: !IsOmniroute(connection.BaseUrl));
        using var httpRequest = NewAnthropicRequest(connection, payload, request);
        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var calls = new List<ProviderToolCall>();
        var callsJson = new JsonArray();
        var openTools = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
        // Tags de raciocínio inline / special tokens <|…|>: gateways
        // Anthropic-compatíveis podem repassar markup de modelos harmony.
        var markupContent = new ReasoningMarkupFilter();
        var markupReasoning = new ReasoningMarkupFilter(keepInnerText: true);
        string? stopReason = null;

        await foreach (var (eventName, data) in ReadSseEventsAsync(reader, ct))
        {
            if (Prop(data, ErrorKey) is { } errorNode)
            {
                throw MidStreamProviderError(errorNode);
            }
            var index = IntNum(Prop(data, IndexKey)) ?? 0;
            var block = Prop(data, "content_block");
            if (eventName == "content_block_start"
                && Str(Prop(block, "type")) == "tool_use")
            {
                openTools[index] = (
                    Str(Prop(block, "id")) ?? Guid.NewGuid().ToString("N"),
                    Str(Prop(block, "name")) ?? string.Empty,
                    new StringBuilder());
            }
            else if (eventName == "content_block_delta")
            {
                var delta = Prop(data, DeltaKey);
                var deltaType = Str(Prop(delta, "type"));
                if (deltaType == "text_delta")
                {
                    var text = Str(Prop(delta, TextKey));
                    if (!string.IsNullOrEmpty(text))
                    {
                        var visible = markupContent.Feed(text);
                        if (visible.Length > 0)
                        {
                            content.Append(visible);
                            if (onDelta is not null)
                            {
                                await onDelta(visible, ct);
                            }
                        }
                        if (onReasoning is not null
                            && markupContent.TakeReasoning() is { Length: > 0 } lifted)
                        {
                            await onReasoning(lifted, ct);
                        }
                    }
                }
                else if (deltaType == "thinking_delta")
                {
                    // Canal separado: thinking não entra no content da run.
                    var thinking = Str(Prop(delta, ThinkingKey));
                    if (!string.IsNullOrEmpty(thinking) && onReasoning is not null)
                    {
                        var clean = markupReasoning.Feed(thinking);
                        if (clean.Length > 0)
                        {
                            await onReasoning(clean, ct);
                        }
                    }
                }
                else if (deltaType == "input_json_delta"
                    && openTools.TryGetValue(index, out var tool))
                {
                    tool.Args.Append(Str(Prop(delta, "partial_json")));
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
                    ["type"] = FunctionKey,
                    [FunctionKey] = new JsonObject
                    {
                        ["name"] = closed.Name,
                        [ArgumentsKey] = args,
                    },
                });
            }
            else if (eventName == "message_delta"
                && Str(Prop(Prop(data, DeltaKey), "stop_reason")) is { Length: > 0 } sr)
            {
                // message_delta carrega stop_reason ("end_turn"/"max_tokens") —
                // "max_tokens" sinaliza truncamento para a continuação RF-003.
                stopReason = sr;
            }
        }

        // Fim do SSE: solta o que os filtros de markup seguraram.
        var anthropicTail = markupContent.Flush();
        if (anthropicTail.Length > 0)
        {
            content.Append(anthropicTail);
            if (onDelta is not null)
            {
                await onDelta(anthropicTail, ct);
            }
        }
        var anthropicTailReasoning = markupReasoning.Flush()
            + markupContent.TakeReasoning();
        if (anthropicTailReasoning.Length > 0 && onReasoning is not null)
        {
            await onReasoning(anthropicTailReasoning, ct);
        }

        return new ProviderCompletion(
            content.ToString(), calls, callsJson.ToJsonString(),
            FinishReason: stopReason);
    }

    // ---------------- Google AI Studio (Gemini API) ----------------

    private static HttpRequestMessage NewGoogleRequest(
        ProviderConnection connection, string model, bool stream, JsonObject payload,
        ChatCompletionRequest? request = null)
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
        if (request is not null)
        {
            ApplyExtraHeaders(httpRequest, request);
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
                    && contents[^1][PartsKey] is JsonArray prevParts
                    && prevParts.All(p => p?["functionResponse"] is not null))
                {
                    prevParts.Add(part);
                }
                else
                {
                    contents.Add(new JsonObject
                    {
                        ["role"] = "user",
                        [PartsKey] = new JsonArray(part),
                    });
                }
                continue;
            }

            if (m.Role == "assistant")
            {
                var parts = new JsonArray();
                if (!string.IsNullOrEmpty(m.Content))
                {
                    parts.Add(new JsonObject { [TextKey] = m.Content });
                }
                if (m.ToolCallsJson is not null)
                {
                    foreach (var call in JsonNode.Parse(m.ToolCallsJson)!.AsArray())
                    {
                        var name = call?[FunctionKey]?["name"]?.GetValue<string>();
                        if (name is null)
                        {
                            continue;
                        }
                        var id = call?["id"]?.GetValue<string>();
                        if (id is not null)
                        {
                            toolNames[id] = name;
                        }
                        var argsRaw = call?[FunctionKey]?[ArgumentsKey];
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
                contents.Add(new JsonObject { ["role"] = "model", [PartsKey] = parts });
                continue;
            }

            contents.Add(new JsonObject
            {
                ["role"] = "user",
                [PartsKey] = new JsonArray(new JsonObject { [TextKey] = m.Content }),
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
                [PartsKey] = new JsonArray(
                    new JsonObject { [TextKey] = string.Join("\n", system) }),
            };
        }

        if (request.Tools is { Count: > 0 })
        {
            var declarations = new JsonArray();
            foreach (var tool in request.Tools)
            {
                var fn = JsonNode.Parse(tool.GetRawText())?[FunctionKey];
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
        if (Prop(json, ErrorKey) is { } bufferedError)
        {
            throw MidStreamProviderError(bufferedError);
        }
        var parts = Prop(json, CandidatesKey) is JsonArray { Count: > 0 } c
            ? Prop(Prop(c[0], ContentKey), PartsKey) as JsonArray
            : null;
        foreach (var part in parts ?? [])
        {
            if (Str(Prop(part, TextKey)) is { } text)
            {
                // part.thought=true é raciocínio — não vai para o Content.
                var isThought = Prop(part, "thought") is JsonValue flag
                    && flag.TryGetValue<bool>(out var b) && b;
                if (isThought)
                {
                    reasoning.Append(text);
                }
                else
                {
                    content.Append(text);
                }
            }
            else if (Prop(part, "functionCall") is JsonObject call)
            {
                var id = Guid.NewGuid().ToString("N");
                var name = Str(Prop(call, "name")) ?? string.Empty;
                var args = Prop(call, "args")?.ToJsonString() ?? "{}";
                calls.Add(new ProviderToolCall(id, name, args));
                callsJson.Add(new JsonObject
                {
                    ["id"] = id,
                    ["type"] = FunctionKey,
                    [FunctionKey] = new JsonObject
                    {
                        ["name"] = name,
                        [ArgumentsKey] = args,
                    },
                });
            }
        }
        // Mesma sanitização dos caminhos OpenAI: markup inline nunca
        // alcança o Content persistido.
        var (cleanText, lifted) = ReasoningMarkupFilter.Extract(content.ToString());
        var (cleanThinking, _) = ReasoningMarkupFilter.Extract(
            reasoning.ToString(), keepInnerText: true);
        reasoning.Clear().Append(cleanThinking).Append(lifted);
        return new ProviderCompletion(
            cleanText, calls, callsJson.ToJsonString(),
            reasoning.Length > 0 ? reasoning.ToString() : null,
            FinishReason: Prop(json, CandidatesKey) is JsonArray { Count: > 0 } fc
                ? Str(Prop(fc[0], "finishReason")) : null);
    }

    private async Task<string> CompleteGoogleAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var connection = FindTypedConnection(connections, "google");
        var payload = BuildGooglePayload(request);
        using var httpRequest = NewGoogleRequest(connection, request.Model, stream: false, payload, request);
        using var response = await httpClientFactory.CreateClient().SendAsync(httpRequest, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return ParseGoogleResponse(json).Content;
    }

    private async Task<ProviderCompletion> CompleteGoogleWithToolsAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var connection = FindTypedConnection(connections, "google");
        var payload = BuildGooglePayload(request);
        using var httpRequest = NewGoogleRequest(connection, request.Model, stream: false, payload, request);
        using var response = await httpClientFactory.CreateClient().SendAsync(httpRequest, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);
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
        using var httpRequest = NewGoogleRequest(connection, request.Model, stream: true, payload, request);
        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Mesma sanitização dos demais providers: markup harmony <|…|> e
        // tags de raciocínio inline podem chegar dentro de parts.text.
        var markupContent = new ReasoningMarkupFilter();
        var markupReasoning = new ReasoningMarkupFilter(keepInnerText: true);

        await foreach (var (_, data) in ReadSseEventsAsync(reader, ct))
        {
            if (Prop(data, ErrorKey) is { } errorNode)
            {
                throw MidStreamProviderError(errorNode);
            }
            var parts = Prop(data, CandidatesKey) is JsonArray { Count: > 0 } c
                ? Prop(Prop(c[0], ContentKey), PartsKey) as JsonArray
                : null;
            foreach (var part in parts ?? [])
            {
                var text = Str(Prop(part, TextKey));
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }
                // thought:true = raciocínio do modelo → delta.reasoning.
                var isThought = Prop(part, "thought") is JsonValue flag
                    && flag.TryGetValue<bool>(out var b) && b;
                var filtered = isThought
                    ? markupReasoning.Feed(text)
                    : markupContent.Feed(text);
                var reasoning = markupContent.TakeReasoning();
                var delta = new JsonObject();
                if (isThought)
                {
                    // Thinking + raciocínio extraído do content vão ao
                    // mesmo canal.
                    if (filtered.Length + reasoning.Length > 0)
                    {
                        delta["reasoning"] = filtered + reasoning;
                    }
                }
                else
                {
                    if (filtered.Length > 0)
                    {
                        delta["content"] = filtered;
                    }
                    if (reasoning.Length > 0)
                    {
                        delta["reasoning"] = reasoning;
                    }
                }
                if (delta.Count == 0)
                {
                    continue;
                }
                var chunk = new JsonObject
                {
                    ["object"] = "chat.completion.chunk",
                    [ChoicesKey] = new JsonArray(new JsonObject
                    {
                        [IndexKey] = 0,
                        [DeltaKey] = delta,
                    }),
                };
                yield return $"data: {chunk.ToJsonString()}";
            }
        }
        // Fim do SSE: solta o que os filtros seguraram antes do [DONE].
        var googleBridgeTail = markupContent.Flush();
        var googleBridgeReasoning = markupReasoning.Flush()
            + markupContent.TakeReasoning();
        if (googleBridgeTail.Length > 0 || googleBridgeReasoning.Length > 0)
        {
            var tailDelta = new JsonObject();
            if (googleBridgeTail.Length > 0)
            {
                tailDelta["content"] = googleBridgeTail;
            }
            if (googleBridgeReasoning.Length > 0)
            {
                tailDelta["reasoning"] = googleBridgeReasoning;
            }
            var tailChunk = new JsonObject
            {
                ["object"] = "chat.completion.chunk",
                [ChoicesKey] = new JsonArray(new JsonObject
                {
                    [IndexKey] = 0,
                    [DeltaKey] = tailDelta,
                }),
            };
            yield return $"data: {tailChunk.ToJsonString()}";
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
        using var httpRequest = NewGoogleRequest(connection, request.Model, stream: true, payload, request);
        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var calls = new List<ProviderToolCall>();
        var callsJson = new JsonArray();
        // Gateways Gemini-compatíveis (OmniRoute) podem repassar markup
        // harmony <|…|> e tags de raciocínio inline dentro de parts.text.
        var markupContent = new ReasoningMarkupFilter();
        var markupReasoning = new ReasoningMarkupFilter(keepInnerText: true);
        string? finishReason = null;

        await foreach (var (_, data) in ReadSseEventsAsync(reader, ct))
        {
            if (Prop(data, ErrorKey) is { } errorNode)
            {
                throw MidStreamProviderError(errorNode);
            }
            if (Prop(data, CandidatesKey) is JsonArray { Count: > 0 } cand
                && Str(Prop(cand[0], "finishReason")) is { Length: > 0 } fr)
            {
                finishReason = fr;
            }
            var parts = Prop(data, CandidatesKey) is JsonArray { Count: > 0 } c
                ? Prop(Prop(c[0], ContentKey), PartsKey) as JsonArray
                : null;
            foreach (var part in parts ?? [])
            {
                if (Str(Prop(part, TextKey)) is { } text)
                {
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }
                    // part.thought=true é a cadeia de raciocínio do Gemini —
                    // antes vazava para o content persistido da resposta.
                    var isThought = Prop(part, "thought") is JsonValue flag
                        && flag.TryGetValue<bool>(out var b) && b;
                    if (isThought)
                    {
                        var cleanThought = markupReasoning.Feed(text);
                        if (cleanThought.Length > 0 && onReasoning is not null)
                        {
                            await onReasoning(cleanThought, ct);
                        }
                        continue;
                    }
                    var visible = markupContent.Feed(text);
                    if (visible.Length > 0)
                    {
                        content.Append(visible);
                        if (onDelta is not null)
                        {
                            await onDelta(visible, ct);
                        }
                    }
                    if (onReasoning is not null
                        && markupContent.TakeReasoning() is { Length: > 0 } lifted)
                    {
                        await onReasoning(lifted, ct);
                    }
                }
                else if (Prop(part, "functionCall") is JsonObject call)
                {
                    var id = Guid.NewGuid().ToString("N");
                    var name = Str(Prop(call, "name")) ?? string.Empty;
                    var args = Prop(call, "args")?.ToJsonString() ?? "{}";
                    calls.Add(new ProviderToolCall(id, name, args));
                    callsJson.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = FunctionKey,
                        [FunctionKey] = new JsonObject
                        {
                            ["name"] = name,
                            [ArgumentsKey] = args,
                        },
                    });
                }
            }
        }

        // Fim do SSE: solta o que os filtros de markup seguraram.
        var googleTail = markupContent.Flush();
        if (googleTail.Length > 0)
        {
            content.Append(googleTail);
            if (onDelta is not null)
            {
                await onDelta(googleTail, ct);
            }
        }
        var googleTailReasoning = markupReasoning.Flush()
            + markupContent.TakeReasoning();
        if (googleTailReasoning.Length > 0 && onReasoning is not null)
        {
            await onReasoning(googleTailReasoning, ct);
        }

        return new ProviderCompletion(
            content.ToString(), calls, callsJson.ToJsonString(),
            FinishReason: finishReason);
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
                    // Histórico pode conter markup DSML vazado de respostas
                    // antigas (pré-filtro): devolvê-lo ao modelo induz a
                    // re-emissão do formato — sanitiza o texto do assistant.
                    var content = m.Role == "assistant"
                        && m.Content.Contains("<｜DSML｜", StringComparison.Ordinal)
                        ? DsmlToolCallFilter.Extract(m.Content).Text
                        : m.Content;
                    var node = new JsonObject
                    {
                        ["role"] = m.Role,
                        [ContentKey] = content,
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
                                    ["type"] = TextKey,
                                    [TextKey] = content,
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
                            node[ContentKey] = parts;
                        }
                    }
                    if (m.ToolCallId is not null)
                    {
                        node["tool_call_id"] = m.ToolCallId;
                    }
                    if (m.ToolCallsJson is not null)
                    {
                        node[ToolCallsKey] = JsonNode.Parse(m.ToolCallsJson);
                    }
                    return (JsonNode)node;
                })
                .ToArray()),
            ["stream"] = stream,
        };

        // OpenAI só envia `usage` no stream quando pedido (RF-004).
        if (stream && provider == ProviderTypes.OpenAi)
        {
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

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

        // SPEC-20261018-model-variants: overlay de body resolvido por
        // variant — merge profundo por cima de params/messages/tools.
        if (request.ExtraBody is { Count: > 0 } overlay)
        {
            ModelVariantService.DeepMerge(payload, overlay);
        }

        return payload;
    }

    /// <summary>
    /// Overlay da conexão tipada (SPEC-20261018-model-variants RF-004):
    /// <c>settings</c> semeiam <see cref="ChatCompletionRequest.Params"/>,
    /// <c>headers</c> viram <c>ExtraHeaders</c> e <c>body</c> <c>ExtraBody</c> —
    /// sempre na menor precedência (model/variant/persona vencem o provider).
    /// Sem conexão tipada do provider resolvido → request inalterado.
    /// </summary>
    private static ChatCompletionRequest ApplyProviderOverlay(
        ChatCompletionRequest request, string provider, ConnectionsConfig connections)
    {
        var conn = connections.ProvidersOrEmpty.FirstOrDefault(p =>
            string.Equals(p.Type, provider, StringComparison.OrdinalIgnoreCase));
        if (conn is null)
        {
            return request;
        }

        var merged = request;
        if (conn.Settings is { Count: > 0 } settings)
        {
            var p = new Dictionary<string, object>();
            foreach (var (key, value) in settings)
            {
                if (value is not null)
                {
                    p[key] = JsonSerializer.Deserialize<JsonElement>(value.ToJsonString());
                }
            }
            if (request.Params is not null)
            {
                foreach (var (key, value) in request.Params)
                {
                    p[key] = value;
                }
            }
            merged = merged with { Params = p };
        }
        if (conn.Headers is { Count: > 0 } headers)
        {
            var h = headers.ToDictionary(
                kv => kv.Key,
                kv => kv.Value?.GetValue<string>() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
            if (request.ExtraHeaders is not null)
            {
                foreach (var (key, value) in request.ExtraHeaders)
                {
                    h[key] = value;
                }
            }
            merged = merged with { ExtraHeaders = h };
        }
        if (conn.Body is { Count: > 0 } body)
        {
            var b = (JsonObject)body.DeepClone();
            if (request.ExtraBody is not null)
            {
                ModelVariantService.DeepMerge(b, request.ExtraBody);
            }
            merged = merged with { ExtraBody = b };
        }
        return merged;
    }

    /// <summary>
    /// Headers extras de variant/overlay (RF-004) — nunca sobrescrevem
    /// Authorization nem Content-Type (proteção do canal autenticado).
    /// </summary>
    private static void ApplyExtraHeaders(
        HttpRequestMessage httpRequest, ChatCompletionRequest request)
    {
        if (request.ExtraHeaders is null)
        {
            return;
        }
        foreach (var (name, value) in request.ExtraHeaders)
        {
            if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            httpRequest.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static string? FirstBaseUrl(ConnectionsConfig connections) =>
        connections.OllamaBaseUrls.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

    /// <summary>Heurística OmniRoute (mesma do ModelWarmer): URL aponta para
    /// o gateway, que já gerencia prompt-cache próprio → cache local off.</summary>
    private static bool IsOmniroute(string? baseUrl) =>
        baseUrl?.Contains("omniroute", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<string> CompleteOllamaAsync(
        ChatCompletionRequest request, ConnectionsConfig connections, CancellationToken ct)
    {
        var baseUrl = FirstBaseUrl(connections)
            ?? throw new InvalidOperationException("Nenhuma URL do Ollama configurada.");

        var payload = BuildPayload(request, stream: false, ProviderTypes.Ollama);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{TrimSlash(baseUrl)}/api/chat")
        {
            Content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyExtraHeaders(httpRequest, request);
        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return Str(Prop(Prop(json, MessageKey), ContentKey)) ?? string.Empty;
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
        ApplyExtraHeaders(httpRequest, request);

        using var response = await httpClientFactory.CreateClient().SendAsync(httpRequest, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return Prop(json, ChoicesKey) is JsonArray { Count: > 0 } arr
            ? Str(Prop(Prop(arr[0], MessageKey), ContentKey)) ?? string.Empty
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
        ApplyExtraHeaders(httpRequest, request);

        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Mesma sanitização dos caminhos OpenAI: modelos harmony no Ollama
        // (gpt-oss) emitem <|…|> e tags de raciocínio inline no NDJSON.
        var markupContent = new ReasoningMarkupFilter();
        var markupReasoning = new ReasoningMarkupFilter(keepInnerText: true);

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
            // Linha escalar (heartbeat "keepalive" etc.) ou message fora de
            // objeto — mesmo guarda do stream OpenAI (SPEC-20261019): pular
            // em vez de quebrar o bridge com InvalidOperationException.
            if (node is not JsonObject)
            {
                continue;
            }

            var done = Prop(node, "done") is JsonValue doneValue
                && doneValue.TryGetValue<bool>(out var doneFlag)
                && doneFlag;
            var message = Prop(node, MessageKey);
            var piece = Str(Prop(message, ContentKey));
            var visible = piece is null ? string.Empty : markupContent.Feed(piece);
            // Modelos "think" do Ollama: message.thinking sai separado —
            // soma o raciocínio extraído de tags inline no content.
            var reasoningText =
                (Str(Prop(message, ThinkingKey)) is { Length: > 0 } thinking
                    ? markupReasoning.Feed(thinking) : string.Empty)
                + markupContent.TakeReasoning();
            if (done)
            {
                // Linha final: solta o que os filtros seguraram.
                visible += markupContent.Flush();
                reasoningText += markupReasoning.Flush()
                    + markupContent.TakeReasoning();
            }
            var delta = new JsonObject
            {
                ["role"] = "assistant",
                [ContentKey] = visible,
            };
            if (reasoningText.Length > 0)
            {
                delta["reasoning"] = reasoningText;
            }
            var chunk = new JsonObject
            {
                ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
                ["object"] = "chat.completion.chunk",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = request.Model,
                [ChoicesKey] = new JsonArray(new JsonObject
                {
                    [IndexKey] = 0,
                    [DeltaKey] = delta,
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

        // Stream truncado sem done:true: solta o que os filtros seguraram
        // antes do [DONE] — sufixo visível não pode se perder.
        var truncTail = markupContent.Flush();
        var truncReasoning = markupReasoning.Flush() + markupContent.TakeReasoning();
        if (truncTail.Length > 0 || truncReasoning.Length > 0)
        {
            var tailDelta = new JsonObject
            {
                ["role"] = "assistant",
                [ContentKey] = truncTail,
            };
            if (truncReasoning.Length > 0)
            {
                tailDelta["reasoning"] = truncReasoning;
            }
            var tailChunk = new JsonObject
            {
                ["id"] = $"chatcmpl-{Guid.NewGuid():N}",
                ["object"] = "chat.completion.chunk",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = request.Model,
                [ChoicesKey] = new JsonArray(new JsonObject
                {
                    [IndexKey] = 0,
                    [DeltaKey] = tailDelta,
                    ["finish_reason"] = "stop",
                }),
            };
            yield return $"data: {tailChunk.ToJsonString()}";
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
        ApplyExtraHeaders(httpRequest, request);

        using var response = await httpClientFactory.CreateClient()
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessOrProviderErrorAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Saneamento do texto do provider: DSML (tool calls markup) +
        // tags de raciocínio inline / special tokens <|…|> — no caminho
        // sem tools nada executa, mas o markup não pode poluir a UI.
        var dsmlContent = new DeltaFilterChain();
        var dsmlReasoning = new DeltaFilterChain(forReasoning: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line == "data: [DONE]")
            {
                var tail = EmitDsmlTail(dsmlContent, dsmlReasoning);
                if (tail is not null)
                {
                    yield return tail;
                }
                yield return line;
                yield break;
            }

            yield return FilterOpenAiDeltaLine(line, dsmlContent, dsmlReasoning);
        }

        // Stream encerrado sem [DONE]: solta o que o filtro segurou.
        var orphanTail = EmitDsmlTail(dsmlContent, dsmlReasoning);
        if (orphanTail is not null)
        {
            yield return orphanTail;
        }
        if (dsmlContent.Calls.Count + dsmlReasoning.Calls.Count is > 0 and var dropped)
        {
            logger.LogWarning(
                "{Count} tool call(s) DSML detectadas no caminho sem tools — markup sanitizado, mas as chamadas não são executáveis.",
                dropped);
        }
    }

    /// <summary>
    /// Cadeia de saneamento de um campo de texto do delta: primeiro o
    /// filtro DSML (tool calls markup → <see cref="ProviderToolCall"/>),
    /// depois o <see cref="ReasoningMarkupFilter"/> (tags de raciocínio
    /// inline → canal reasoning; special tokens <c>&lt;|…|&gt;</c> →
    /// removidos). Com <paramref name="forReasoning"/> o canal já é de
    /// thinking — o texto interno das tags permanece na saída.
    /// </summary>
    private sealed class DeltaFilterChain
    {
        public DeltaFilterChain(bool forReasoning = false) =>
            Markup = new ReasoningMarkupFilter(keepInnerText: forReasoning);

        public DsmlToolCallFilter Dsml { get; } = new();
        public ReasoningMarkupFilter Markup { get; }

        /// <summary>Calls DSML extraídas do campo (só faz sentido no path
        /// com tools — sem tools viram apenas markup removido).</summary>
        public IReadOnlyList<ProviderToolCall> Calls => Dsml.Calls;

        public string Feed(string piece) => Markup.Feed(Dsml.Feed(piece));

        public string Flush() => Markup.Feed(Dsml.Flush()) + Markup.Flush();
    }

    /// <summary>Passa content/reasoning de um chunk OpenAI pela cadeia
    /// DSML→markup e reemite a linha reescrita; raciocínio extraído de
    /// tags inline no content é promovido a <c>delta.reasoning</c>;
    /// linhas não-delta passam intactas.</summary>
    private static string FilterOpenAiDeltaLine(
        string line, DeltaFilterChain content, DeltaFilterChain reasoning)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            return line;
        }
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line["data:".Length..]);
        }
        catch (JsonException)
        {
            return line;
        }
        if (node is not JsonObject
            || Prop(node, ChoicesKey) is not JsonArray { Count: > 0 } choices
            || choices[0] is not JsonObject first
            || first[DeltaKey] is not JsonObject delta)
        {
            return line;
        }
        var extracted = string.Empty;
        if (delta[ContentKey] is JsonValue c
            && c.TryGetValue<string>(out var s) && s.Length > 0)
        {
            delta[ContentKey] = content.Feed(s);
            extracted = content.Markup.TakeReasoning();
        }
        // reasoning_details[] é array de segmentos — bloco DSML dividido
        // entre elementos é raro; cobre os campos escalares (deepseek/omniroute).
        foreach (var field in new[] { "reasoning_content", "reasoning" }
            .Where(f => delta[f] is JsonValue r
                && r.TryGetValue<string>(out var rs) && rs.Length > 0))
        {
            delta[field] = reasoning.Feed(delta[field]!.AsValue().GetValue<string>());
        }
        // Raciocínio extraído de tags inline no content sai pelo canal
        // próprio — merge com o reasoning já filtrado sem sobrescrever.
        if (extracted.Length > 0)
        {
            var existing = Str(Prop(delta, "reasoning")) ?? string.Empty;
            delta["reasoning"] = existing + extracted;
        }
        return $"data: {node.ToJsonString(JsonOptions)}";
    }

    /// <summary>Delta sintético final com o que os filtros seguraram no
    /// fim do stream (texto solto emite; raciocínio extraído de tags
    /// inline no content é promovido a <c>delta.reasoning</c>; markup
    /// truncado vira calls descartadas no caminho sem tools).</summary>
    private static string? EmitDsmlTail(
        DeltaFilterChain content, DeltaFilterChain reasoning)
    {
        var tail = content.Flush();
        var tailReasoning = reasoning.Flush() + content.Markup.TakeReasoning();
        if (tail.Length == 0 && tailReasoning.Length == 0)
        {
            return null;
        }
        var delta = new JsonObject();
        if (tail.Length > 0)
        {
            delta[ContentKey] = tail;
        }
        if (tailReasoning.Length > 0)
        {
            delta["reasoning"] = tailReasoning;
        }
        var chunk = new JsonObject
        {
            [ChoicesKey] = new JsonArray(new JsonObject
            {
                [IndexKey] = 0,
                [DeltaKey] = delta,
            }),
        };
        return $"data: {chunk.ToJsonString(JsonOptions)}";
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

    /// <summary>
    /// Garante sucesso ou lança <see cref="ProviderHttpException"/> com
    /// <c>Retry-After</c> e recorte do corpo (SPEC-20261016-provider-
    /// resilience): o motor de retry precisa do header e da assinatura de
    /// context-overflow que o <c>EnsureSuccessStatusCode</c> descartava.
    /// </summary>
    private static async Task EnsureSuccessOrProviderErrorAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length > ProviderHttpException.MaxBodyChars)
            {
                body = body[..ProviderHttpException.MaxBodyChars];
            }
        }
        catch (Exception)
        {
            body = null;
        }

        var ra = response.Headers.RetryAfter;
        TimeSpan? retryAfter = ra?.Delta
            ?? (ra?.Date is { } when && when > DateTimeOffset.UtcNow
                ? when - DateTimeOffset.UtcNow : null);
        throw new ProviderHttpException(response.StatusCode, retryAfter, body);
    }
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
    string? Reasoning = null,
    /// <summary>Uso de tokens reportado pelo provider (RF-004 do SPEC
    /// agent-steps-usage); null quando o provider não informa — nunca
    /// estimado.</param>
    ProviderUsage? Usage = null,
    /// <summary>Motivo de parada reportado pelo provider (OpenAI
    /// <c>finish_reason</c>, Ollama <c>done_reason</c>, Anthropic
    /// <c>stop_reason</c>, Google <c>finishReason</c>) — <c>length</c>/
    /// <c>max_tokens</c>/<c>MAX_TOKENS</c> sinalizam resposta truncada para a
    /// continuação automática (SPEC-20261016-provider-resilience RF-003).</param>
    string? FinishReason = null);

/// <summary>Uso de tokens de uma completion (OpenAI usage / Ollama eval_count).</summary>
/// <param name="PromptTokens">Tokens do prompt (prompt_eval_count no Ollama).</param>
/// <param name="CompletionTokens">Tokens gerados (eval_count no Ollama).</param>
/// <param name="ReasoningTokens">Tokens de reasoning quando discriminados.</param>
public sealed record ProviderUsage(
    int PromptTokens, int CompletionTokens, int? ReasoningTokens = null);

/// <summary>Chamada de função normalizada (Ollama/OpenAI).</summary>
/// <param name="Id">Id da chamada (echo em tool_call_id).</param>
/// <param name="Name">Nome da função.</param>
/// <param name="ArgumentsJson">Argumentos em JSON.</param>
public sealed record ProviderToolCall(string Id, string Name, string ArgumentsJson);
