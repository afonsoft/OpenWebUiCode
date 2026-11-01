using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Detecção automática de capacidades de conexões OpenAI-compatíveis:
/// lista <c>GET /models</c>, classifica por <c>output_modalities</c> +
/// heurística de id e <b>prova</b> o endpoint de cada recurso
/// (<c>/audio/speech</c>, <c>/images/generations</c>, <c>/videos</c>,
/// <c>/embeddings</c>) — o catálogo pode anunciar modelos sem credencial
/// upstream, então só o probe confirma. Preenche <c>audio.config</c>,
/// <c>images.config</c>, <c>video.config</c> e <c>rag.embedding.model</c>
/// <b>somente quando a chave ainda não existe</b>: config explícita do
/// admin nunca é sobrescrita.
/// </summary>
public class ProviderCapabilityService(
    IHttpClientFactory httpFactory, ConfigService config,
    ILogger<ProviderCapabilityService> logger)
{
    private const string ModelKey = "model";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Heurísticas de id quando o catálogo não traz output_modalities.
    private static readonly Regex TtsHint = new(
        @"tts|speech|melotts|bark|xtts|kokoro|vibevoice|openvoice|eleven",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexDefaults.MatchTimeout);
    private static readonly Regex SttHint = new(
        @"whisper|distil-whisper|transcri|parakeet|canary|sensevoice|voxtral|stt|asr",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexDefaults.MatchTimeout);
    private static readonly Regex ImageHint = new(
        @"dall-e|gpt-image|flux|seedream|imagen|sdxl|stable-diffusion|banana|"
            + @"recraft|ideogram|kolors|qwen-image|wan-image|playground",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexDefaults.MatchTimeout);
    private static readonly Regex VideoHint = new(
        @"sora|veo|seedance|hailuo|minimax-h|wan[0-9]|ltx|kling|pika|runway|"
            + @"mochi|cogvideo|hunyuan-video|grok-imagine-video|p-video|-video\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexDefaults.MatchTimeout);
    private static readonly Regex EmbedHint = new(
        @"embed|bge-|e5-|gte-|nomic-embed|uae-",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexDefaults.MatchTimeout);

    private sealed record CatalogEntry(
        string Id, HashSet<string> Outputs, HashSet<string> Inputs);

    /// <summary>Detecta e aplica capacidades em todas as conexões OpenAI cadastradas.</summary>
    public async Task AutoConfigureAsync(CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        for (var i = 0; i < connections.OpenAiBaseUrls.Count; i++)
        {
            var baseUrl = connections.OpenAiBaseUrls[i];
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                continue;
            }

            try
            {
                await DetectAsync(
                    baseUrl, connections.OpenAiApiKeys.ElementAtOrDefault(i), ct, i);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex,
                    "Auto-config de capacidades falhou para {BaseUrl}.", baseUrl);
            }
        }
    }

    /// <summary>
    /// Classifica o catálogo de uma conexão sem probas (GET /models +
    /// heurística) — caminho rápido para o endpoint de capabilities.
    /// </summary>
    public async Task<DetectedCapabilities?> DetectCandidatesAsync(
        string baseUrl, string? apiKey, CancellationToken ct = default)
    {
        var catalog = await FetchCatalogAsync(baseUrl, apiKey, ct);
        if (catalog.Count == 0)
        {
            return null;
        }
        return new DetectedCapabilities(
            ImageCandidates(catalog), VideoCandidates(catalog),
            TtsCandidates(catalog), SttCandidates(catalog),
            EmbedCandidates(catalog), baseUrl.TrimEnd('/'),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>Chave da conexão no mapa persistido (<c>{type}:{index}</c>).</summary>
    public static string ConnectionKey(string type, int index) => $"{type}:{index}";

    /// <summary>
    /// Detecta as capacidades de uma conexão cadastrada (rápido: /models +
    /// heurística, sem probes) e persiste no mapa por conexão — alimenta os
    /// combos do admin filtrados por provider (STT/TTS/imagem/vídeo).
    /// </summary>
    /// <param name="type">Tipo da conexão (<c>openai</c> ou <c>ollama</c>).</param>
    /// <param name="index">Índice na lista de conexões.</param>
    /// <param name="ct">Cancelamento.</param>
    /// <returns>Capacidades detectadas, ou null quando a conexão não responde.</returns>
    public async Task<DetectedCapabilities?> DetectConnectionAsync(
        string type, int index, CancellationToken ct = default)
    {
        var connections = await config.GetConnectionsAsync(ct);
        var urls = type == "ollama" ? connections.OllamaBaseUrls : connections.OpenAiBaseUrls;
        if (index < 0 || index >= urls.Count || string.IsNullOrWhiteSpace(urls[index]))
        {
            return null;
        }

        var detected = type == "ollama"
            ? await DetectOllamaAsync(urls[index], ct)
            : await DetectCandidatesAsync(
                urls[index], connections.OpenAiApiKeys.ElementAtOrDefault(index), ct);
        var map = await GetConnectionMapAsync(ct);
        var key = ConnectionKey(type, index);
        if (detected is null)
        {
            map.Remove(key);
        }
        else
        {
            map[key] = detected;
        }
        await config.SetAsync(DetectedConnectionsKey, map, ct);
        return detected;
    }

    /// <summary>Chave do kv com o mapa de capacidades por conexão.</summary>
    public const string DetectedConnectionsKey = "capabilities.detected.connections";

    /// <summary>Lê o mapa persistido de capacidades por conexão.</summary>
    public async Task<Dictionary<string, DetectedCapabilities>> GetConnectionMapAsync(
        CancellationToken ct = default) =>
        await config.GetAsync<Dictionary<string, DetectedCapabilities>?>(
            DetectedConnectionsKey, null, ct) ?? new();

    /// <summary>
    /// Agrega o mapa por conexão num único <see cref="DetectedCapabilities"/> —
    /// compat com o formato antigo do kv <c>capabilities.detected</c>.
    /// </summary>
    public async Task<DetectedCapabilities?> GetAggregatedAsync(CancellationToken ct = default)
    {
        var map = await GetConnectionMapAsync(ct);
        if (map.Count == 0)
        {
            return null;
        }

        static List<string> Union(
            IEnumerable<DetectedCapabilities> all,
            Func<DetectedCapabilities, IReadOnlyList<string>> pick) =>
            all.SelectMany(pick).Where(m => !string.IsNullOrEmpty(m))
               .Distinct().ToList();

        var entries = map.Values.ToList();
        return new DetectedCapabilities(
            Union(entries, c => c.Image), Union(entries, c => c.Video),
            Union(entries, c => c.Tts), Union(entries, c => c.Stt),
            Union(entries, c => c.Embed),
            entries.Select(e => e.BaseUrl).FirstOrDefault(u => u is not null),
            entries.Max(e => e.DetectedAt));
    }

    private async Task PersistDetectedAsync(
        string type, int index, DetectedCapabilities detected, CancellationToken ct)
    {
        var map = await GetConnectionMapAsync(ct);
        map[ConnectionKey(type, index)] = detected;
        await config.SetAsync(DetectedConnectionsKey, map, ct);
    }

    private async Task DetectAsync(
        string baseUrl, string? apiKey, CancellationToken ct, int index)
    {
        var catalog = await FetchCatalogAsync(baseUrl, apiKey, ct);
        if (catalog.Count == 0)
        {
            return;
        }

        var url = baseUrl.TrimEnd('/');
        var report = new List<string>();

        // Persiste os candidatos detectados (combos do admin) — mapa por
        // conexão para que cada combo filtre os modelos do provider certo.
        await PersistDetectedAsync("openai", index, new DetectedCapabilities(
            ImageCandidates(catalog), VideoCandidates(catalog),
            TtsCandidates(catalog), SttCandidates(catalog),
            EmbedCandidates(catalog), url,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ct);

        // ---- Áudio (TTS + STT) ----
        if (await config.GetAsync<AudioConfig?>("audio.config", null, ct) is null)
        {
            var tts = await PickTtsAsync(url, apiKey, catalog, ct);
            // STT = modelo que ACEITA áudio: input_modalities contém "audio".
            var stt = SttCandidates(catalog).FirstOrDefault();
            if (tts is not null || stt is not null)
            {
                await config.SetAsync("audio.config", AudioConfig.Default with
                {
                    TtsEngine = tts is not null ? "provider" : "none",
                    TtsProvider = tts is not null ? url : null,
                    TtsModel = tts,
                    TtsVoice = tts is not null ? "alloy" : null,
                    SttEngine = stt is not null ? "provider" : "none",
                    SttProvider = stt is not null ? url : null,
                    SttModel = stt,
                }, ct);
                report.Add($"audio(tts={tts ?? "-"},stt={stt ?? "-"})");
            }
        }

        // ---- Imagens ----
        if (await config.GetAsync<ImagesConfig?>("images.config", null, ct) is null)
        {
            var image = await PickImageAsync(url, apiKey, catalog, ct);
            if (image is not null)
            {
                await config.SetAsync("images.config", ImagesConfig.Default with
                {
                    Enabled = true,
                    Engine = "openai",
                    BaseUrl = url,
                    ApiKey = apiKey ?? string.Empty,
                    Model = image,
                }, ct);
                report.Add($"images({image})");
            }
        }

        // ---- Vídeo (Sora-compatible: POST /videos + polling) ----
        if (await config.GetAsync<VideoConfig?>("video.config", null, ct) is null
            && await RouteExistsAsync(url, apiKey, "videos", ct))
        {
            var video = await PickVideoAsync(url, apiKey, catalog, ct);
            if (video is not null)
            {
                await config.SetAsync("video.config", VideoConfig.Default with
                {
                    Enabled = true,
                    Engine = "openai",
                    BaseUrl = url,
                    ApiKey = apiKey ?? string.Empty,
                    Model = video,
                }, ct);
                report.Add($"video({video})");
            }
        }

        // ---- Embeddings (RAG) ----
        if (Environment.GetEnvironmentVariable("RAG_EMBEDDING_MODEL_OPENAI") is null
            && await config.GetAsync<string?>("rag.embedding.model", null, ct) is null)
        {
            var embed = await PickEmbeddingAsync(url, apiKey, catalog, ct);
            if (embed is not null)
            {
                await config.SetAsync("rag.embedding.model", embed, ct);
                report.Add($"embed({embed})");
            }
        }

        if (report.Count > 0)
        {
            logger.LogInformation(
                "Auto-config de capacidades em {BaseUrl}: {Applied}", url, string.Join(", ", report));
        }
    }

    // ---------------- Catálogo ----------------

    private async Task<List<CatalogEntry>> FetchCatalogAsync(
        string baseUrl, string? apiKey, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(nameof(ProviderCapabilityService));
        http.Timeout = TimeSpan.FromSeconds(20);
        using var request = NewRequest(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models", apiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var entries = new List<CatalogEntry>();
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var item in node?["data"]?.AsArray() ?? [])
        {
            var id = item?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            entries.Add(new CatalogEntry(
                id,
                Modalities(item?["output_modalities"]),
                Modalities(item?["input_modalities"])));
        }
        return entries;
    }

    private static HashSet<string> Modalities(JsonNode? node) =>
        (node as JsonArray ?? [])
            .Select(m => m?.GetValue<string>() ?? string.Empty)
            .Where(m => m.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Detecção para conexão Ollama: tenta o catálogo OpenAI-compatível
    /// (<c>/v1/models</c>) e cai no nativo <c>/api/tags</c> — a URL
    /// cadastrada costuma ser a raiz (<c>http://host:11434</c>), então
    /// <c>GET /models</c> daria 404 e o combo ficaria vazio.
    /// </summary>
    private async Task<DetectedCapabilities?> DetectOllamaAsync(
        string baseUrl, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/');
        // Ollama também expõe /v1/models — traz ids canônicos quando existe.
        var catalog = await FetchCatalogAsync(
            url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url : url + "/v1",
            null, ct);
        if (catalog.Count == 0)
        {
            catalog = await FetchOllamaTagsAsync(url, ct);
        }
        if (catalog.Count == 0)
        {
            return null;
        }

        return new DetectedCapabilities(
            ImageCandidates(catalog), VideoCandidates(catalog),
            TtsCandidates(catalog), SttCandidates(catalog),
            EmbedCandidates(catalog), url,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>Catálogo nativo do Ollama (<c>GET /api/tags</c>) → ids dos modelos.</summary>
    private async Task<List<CatalogEntry>> FetchOllamaTagsAsync(
        string baseUrl, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(nameof(ProviderCapabilityService));
            http.Timeout = TimeSpan.FromSeconds(15);
            using var response = await http.GetAsync($"{baseUrl}/api/tags", ct);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var entries = (node?["models"]?.AsArray() ?? [])
                .Select(item => item?["name"]?.GetValue<string>())
                .OfType<string>()
                .Where(id => id.Length > 0)
                .Select(id => new CatalogEntry(id, [], []))
                .ToList();
            return entries;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or OperationCanceledException)
        {
            return [];
        }
    }

    // ---------------- Seleção + probes ----------------

    // Candidatos por capacidade — modalidade primeiro, heurística de id depois.
    private static List<string> TtsCandidates(List<CatalogEntry> catalog) =>
        catalog
            .Where(m => m.Outputs.Contains("audio") || TtsHint.IsMatch(m.Id))
            .Where(m => !SttHint.IsMatch(m.Id) && !m.Id.Contains("realtime"))
            .Select(m => m.Id)
            .OrderByDescending(id => TtsHint.IsMatch(id))
            .ThenBy(id => id.Length)
            .ToList();

    private static List<string> SttCandidates(List<CatalogEntry> catalog) =>
        catalog
            .Where(m => m.Inputs.Contains("audio") || SttHint.IsMatch(m.Id))
            .Select(m => m.Id)
            .OrderBy(id => id.Length)
            .ToList();

    private static List<string> ImageCandidates(List<CatalogEntry> catalog) =>
        catalog
            .Where(m => m.Outputs.Contains("image") || ImageHint.IsMatch(m.Id))
            .Select(m => m.Id)
            .OrderByDescending(id => ImageHint.IsMatch(id))
            .ThenBy(id => id.Length)
            .ToList();

    private static List<string> VideoCandidates(List<CatalogEntry> catalog) =>
        catalog
            .Where(m => m.Outputs.Contains("video") || VideoHint.IsMatch(m.Id))
            .Select(m => m.Id)
            .OrderBy(id => id.Length)
            .ToList();

    private static List<string> EmbedCandidates(List<CatalogEntry> catalog) =>
        catalog
            .Where(m => EmbedHint.IsMatch(m.Id))
            .Select(m => m.Id)
            .OrderBy(id => id.Length)
            .ToList();

    /// <summary>TTS: prova POST /audio/speech até achar um modelo que funcione.</summary>
    private async Task<string?> PickTtsAsync(
        string url, string? apiKey, List<CatalogEntry> catalog, CancellationToken ct)
    {
        // Modelos com "tts" explícito primeiro — gpt-audio é chat com saída
        // de áudio, não serve a /audio/speech.
        var candidates = TtsCandidates(catalog).Take(4);

        foreach (var model in candidates)
        {
            var ok = await ProbeJsonAsync(
                url, apiKey, "audio/speech",
                new JsonObject
                {
                    [ModelKey] = model, ["voice"] = "alloy", ["input"] = "a",
                },
                25, ct,
                accept: r => r.Content.Headers.ContentType?.MediaType
                    ?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true);
            if (ok)
            {
                return model;
            }
        }
        return null;
    }

    /// <summary>Imagem: prova POST /images/generations (1 imagem 1024x1024).</summary>
    private async Task<string?> PickImageAsync(
        string url, string? apiKey, List<CatalogEntry> catalog, CancellationToken ct)
    {
        var candidates = ImageCandidates(catalog).Take(2);

        foreach (var model in candidates)
        {
            var ok = await ProbeJsonAsync(
                url, apiKey, "images/generations",
                new JsonObject
                {
                    [ModelKey] = model, ["prompt"] = "a red dot",
                    ["n"] = 1, ["size"] = "1024x1024",
                },
                60, ct);
            if (ok)
            {
                return model;
            }
        }
        return null;
    }

    /// <summary>Vídeo: POST /videos com o primeiro candidato (cria job real).</summary>
    private async Task<string?> PickVideoAsync(
        string url, string? apiKey, List<CatalogEntry> catalog, CancellationToken ct)
    {
        var model = VideoCandidates(catalog).FirstOrDefault();
        if (model is null)
        {
            return null;
        }

        // Job real: qualquer resposta que não seja "rota inexistente" ativa.
        return await ProbeJsonAsync(
            url, apiKey, "videos",
            new JsonObject { [ModelKey] = model, ["prompt"] = "a red dot" },
            30, ct)
            ? model
            : null;
    }

    /// <summary>Embedding: prova POST /embeddings e exige vetor no retorno.</summary>
    private async Task<string?> PickEmbeddingAsync(
        string url, string? apiKey, List<CatalogEntry> catalog, CancellationToken ct)
    {
        foreach (var model in EmbedCandidates(catalog).Take(3))
        {
            var ok = await ProbeJsonAsync(
                url, apiKey, "embeddings",
                new JsonObject { [ModelKey] = model, ["input"] = "a" },
                25, ct,
                accept: r => true);
            if (ok)
            {
                return model;
            }
        }
        return null;
    }

    /// <summary>A rota existe quando a resposta não é 404/501 (rota desconhecida).</summary>
    private async Task<bool> RouteExistsAsync(
        string url, string? apiKey, string path, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(nameof(ProviderCapabilityService));
            http.Timeout = TimeSpan.FromSeconds(15);
            using var request = NewRequest(HttpMethod.Post, $"{url}/{path}", apiKey);
            request.Content = JsonContent.Create(new JsonObject());
            using var response = await http.SendAsync(request, ct);
            return (int)response.StatusCode is not 404 and not 501;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>POST JSON de prova; true quando 2xx e <paramref name="accept"/> confirma.</summary>
    private async Task<bool> ProbeJsonAsync(
        string url, string? apiKey, string path, JsonObject body,
        int timeoutSeconds, CancellationToken ct,
        Func<HttpResponseMessage, bool>? accept = null)
    {
        try
        {
            var http = httpFactory.CreateClient(nameof(ProviderCapabilityService));
            http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            using var request = NewRequest(HttpMethod.Post, $"{url}/{path}", apiKey);
            request.Content = JsonContent.Create(body, options: JsonOptions);
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode && (accept?.Invoke(response) ?? true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or OperationCanceledException or JsonException)
        {
            return false;
        }
    }

    private static HttpRequestMessage NewRequest(
        HttpMethod method, string url, string? apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        return request;
    }
}

/// <summary>
/// Roda a detecção de capacidades uma vez no boot, em background — cobre
/// conexões semeadas por env (<c>OPENAI_API_BASE_URL</c> etc.) que nunca
/// passam pelo endpoint de cadastro.
/// </summary>
public class ProviderCapabilityBootstrapper(IServiceScopeFactory scopeFactory)
    : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ProviderCapabilityService>()
                .AutoConfigureAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown antes/durante a detecção — normal.
        }
        catch (Exception)
        {
            // Detecção é best-effort; nunca derruba a aplicação.
        }
    }
}
