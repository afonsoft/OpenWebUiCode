using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Web search via engine configurada em <c>retrieval.config</c>:
/// searxng (self-hosted), duckduckgo (instant answer), tavily, brave,
/// google_pse, jina, exa, kagi, perplexity (API key) — ou
/// <c>auto</c> (SPEC-20261016-websearch-failover): tenta os providers
/// com credencial na ordem estável tavily→brave→exa→google_pse→kagi e
/// duckduckgo por último (sem chave), com cooldown por provider em
/// 429/5xx/timeout e failover automático para o próximo.
/// Chaves nunca são retornadas — apenas usadas no request à engine.
/// </summary>
public class WebSearchService(IHttpClientFactory httpFactory, ConfigService config)
{
    private const string BraveEngine = "brave";
    private const string DuckDuckGoEngine = "duckduckgo";
    private const string GooglePseEngine = "google_pse";
    private const string TavilyEngine = "tavily";
    private const string ResultsKey = "results";
    private const string TitleKey = "title";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Cooldown quando o provider devolve 429 sem Retry-After.</summary>
    public static readonly TimeSpan RateLimitCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Cooldown por timeout/5xx/demais falhas de transporte.</summary>
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Teto do Retry-After respeitado (RF-002).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>Ordem estável do modo auto: credenciados primeiro, duckduckgo (sem key) por último.</summary>
    private static readonly string[] AutoOrder =
        [TavilyEngine, BraveEngine, "exa", GooglePseEngine, "kagi", DuckDuckGoEngine];

    private static readonly ConcurrentDictionary<string, DateTimeOffset> Cooldowns = new();

    /// <summary>Relógio injetável para testes de cooldown.</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Limpa cooldowns — uso exclusivo de testes.</summary>
    internal static void ResetCooldowns() => Cooldowns.Clear();

    /// <summary>
    /// Executa a busca na engine configurada. Retorna null quando web search
    /// está desabilitada (engine "none") — o endpoint converte em 503.
    /// </summary>
    /// <param name="query">Termo de busca.</param>
    /// <param name="count">Máximo de resultados (default 5).</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<List<WebSearchResult>?> SearchAsync(
        string query, int count = 5, CancellationToken ct = default)
        => (await SearchDetailedAsync(query, count, ct))?.Results;

    /// <summary>
    /// Busca com metadados de provider/failover (RF-003/RF-004): usada por
    /// <c>builtin:web_search</c> para registrar <c>[via provider]</c> e
    /// devolver <c>all-providers-cooling</c> quando tudo está em cooldown.
    /// </summary>
    public async Task<WebSearchOutcome?> SearchDetailedAsync(
        string query, int count = 5, CancellationToken ct = default)
    {
        var cfg = await config.GetAsync("retrieval.config", RetrievalConfig.Default, ct);
        var client = httpFactory.CreateClient(nameof(WebSearchService));
        client.Timeout = TimeSpan.FromSeconds(15);

        if (cfg.Engine == "auto")
        {
            return await SearchAutoAsync(client, cfg, query, count, ct);
        }
        if (cfg.Engine == "none" || string.IsNullOrWhiteSpace(cfg.Engine))
        {
            return null;
        }

        var results = await DispatchAsync(cfg.Engine, client, cfg, query, count, ct);
        return new WebSearchOutcome(results, cfg.Engine, FailoverChain: null);
    }

    /// <summary>
    /// Modo auto (RF-001/RF-002/RF-004): tenta providers com credencial na
    /// ordem estável, pulando quem está em cooldown; falha marca cooldown
    /// e tenta o próximo. Todos cooling/falhos → outcome de erro
    /// <c>all-providers-cooling</c> com retry até o cooldown mais próximo.
    /// </summary>
    private static async Task<WebSearchOutcome> SearchAutoAsync(
        HttpClient client, RetrievalConfig cfg, string query, int count, CancellationToken ct)
    {
        var attempted = new List<string>();
        foreach (var provider in AutoOrder)
        {
            if (!HasCredentials(provider, cfg) || InCooldown(provider))
            {
                continue;
            }

            if (await TryProviderAsync(provider, client, cfg, query, count, attempted, ct)
                    is { } outcome)
            {
                return outcome;
            }
        }

        // Todos falharam agora ou já estavam em cooldown (RF-004).
        return AllCoolingOutcome(attempted);
    }

    /// <summary>Tenta um provider do pool — sucesso retorna o outcome; falha
    /// marca cooldown, anota a tentativa e devolve null.</summary>
    private static async Task<WebSearchOutcome?> TryProviderAsync(
        string provider, HttpClient client, RetrievalConfig cfg,
        string query, int count, List<string> attempted, CancellationToken ct)
    {
        try
        {
            var results = await DispatchAsync(provider, client, cfg, query, count, ct);
            var chain = attempted.Count > 0
                ? string.Join("→", attempted.Append(provider))
                : null;
            return new WebSearchOutcome(results, provider, chain);
        }
        catch (WebSearchProviderException ex)
        {
            SetCooldown(provider, ex.RetryAfter);
            attempted.Add(provider);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout do HttpClient (15s) — cooldown curto e próximo.
            SetCooldown(provider, null);
            attempted.Add(provider);
        }
        return null;
    }

    /// <summary>Outcome agregado quando todos falham ou já estavam em
    /// cooldown — erro all-providers-cooling + retry mais próximo.</summary>
    private static WebSearchOutcome AllCoolingOutcome(List<string> attempted)
    {
        var next = AutoOrder.Select(CooldownUntil).Where(u => u is not null).Min();
        var retryAfter = next is { } until
            ? Math.Max(1, (int)Math.Ceiling((until - Now()).TotalSeconds))
            : (int)FailureCooldown.TotalSeconds;
        return new WebSearchOutcome([], attempted.LastOrDefault() ?? "auto",
            attempted.Count > 0 ? string.Join("→", attempted) : null,
            Error: "all-providers-cooling", RetryAfterSeconds: retryAfter);
    }

    /// <summary>Provider tem credencial configurada para entrar no pool do auto.</summary>
    private static bool HasCredentials(string provider, RetrievalConfig cfg) => provider switch
    {
        TavilyEngine => !string.IsNullOrEmpty(cfg.TavilyApiKey),
        BraveEngine => !string.IsNullOrEmpty(cfg.BraveApiKey),
        "exa" => !string.IsNullOrEmpty(cfg.ExaApiKey),
        GooglePseEngine => !string.IsNullOrEmpty(cfg.GooglePseApiKey)
            && !string.IsNullOrEmpty(cfg.GooglePseEngineId),
        "kagi" => !string.IsNullOrEmpty(cfg.KagiApiKey),
        DuckDuckGoEngine => true,
        _ => false,
    };

    private static bool InCooldown(string provider)
        => CooldownUntil(provider) is { } until && until > Now();

    private static DateTimeOffset? CooldownUntil(string provider)
    {
        if (Cooldowns.TryGetValue(provider, out var until))
        {
            return until;
        }
        return null;
    }

    private static string StrProp(JsonElement item, string key)
    {
        if (item.TryGetProperty(key, out var v))
        {
            return v.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>
    /// Marca cooldown do provider (RF-002): 429 honra Retry-After (teto de
    /// 5min) ou cai no default de 60s; demais falhas → 30s.
    /// </summary>
    private static void SetCooldown(string provider, TimeSpan? retryAfter)
    {
        var span = retryAfter is { } r
            ? r <= TimeSpan.Zero ? RateLimitCooldown : r
            : FailureCooldown;
        if (span > MaxRetryAfter)
        {
            span = MaxRetryAfter;
        }
        Cooldowns[provider] = Now() + span;
    }

    /// <summary>Despacha para a implementação do provider.</summary>
    private static Task<List<WebSearchResult>> DispatchAsync(
        string provider, HttpClient client, RetrievalConfig cfg,
        string query, int count, CancellationToken ct) => provider switch
    {
        "searxng" => SearxngAsync(client, cfg, query, count, ct),
        DuckDuckGoEngine => DuckDuckGoAsync(client, query, count, ct),
        TavilyEngine => TavilyAsync(client, cfg, query, count, ct),
        BraveEngine => BraveAsync(client, cfg, query, count, ct),
        GooglePseEngine => GooglePseAsync(client, cfg, query, count, ct),
        "jina" => JinaAsync(client, cfg, query, count, ct),
        "exa" => ExaAsync(client, cfg, query, count, ct),
        "kagi" => KagiAsync(client, cfg, query, count, ct),
        "perplexity" => PerplexityAsync(client, cfg, query, count, ct),
        _ => Task.FromResult(new List<WebSearchResult>()),
    };

    /// <summary>
    /// GET/POST com checagem de status: não-2xx →
    /// <see cref="WebSearchProviderException"/> carregando Retry-After
    /// (antes os SendAsync liam o corpo de erro como JSON vazio).
    /// </summary>
    private static async Task<JsonElement> SendJsonAsync(
        HttpClient client, string provider, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var retry = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } d
                    ? d - DateTimeOffset.UtcNow
                    : null);
            throw new WebSearchProviderException(provider, response.StatusCode, retry);
        }
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
    }

    private static async Task<JsonElement> GetJsonAsync(
        HttpClient client, string provider, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendJsonAsync(client, provider, request, ct);
    }

    private static async Task<List<WebSearchResult>> SearxngAsync(
        HttpClient client, RetrievalConfig cfg, string query, int count, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.SearxngBaseUrl))
        {
            return [];
        }

        var url = $"{cfg.SearxngBaseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json";
        var doc = await GetJsonAsync(client, "searxng", url, ct);
        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty(ResultsKey, out var items))
        {
            foreach (var item in items.EnumerateArray().Take(count))
            {
                results.Add(new WebSearchResult(
                    StrProp(item, TitleKey), StrProp(item, "url"), StrProp(item, "content")));
            }
        }
        return results;
    }

    private static async Task<List<WebSearchResult>> DuckDuckGoAsync(
        HttpClient client, string query, int count, CancellationToken ct)
    {
        var url = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1";
        var doc = await GetJsonAsync(client, DuckDuckGoEngine, url, ct);
        var results = new List<WebSearchResult>();
        AddDdgAbstract(doc, query, results);
        if (doc.TryGetProperty("RelatedTopics", out var topics))
        {
            foreach (var topic in topics.EnumerateArray())
            {
                AddDdgTopic(topic, results);
            }
        }
        return results.Take(count).ToList();
    }

    /// <summary>Resposta direta (AbstractText) vira o primeiro resultado.</summary>
    private static void AddDdgAbstract(
        JsonElement doc, string query, List<WebSearchResult> results)
    {
        if (doc.TryGetProperty("AbstractText", out var abs)
            && !string.IsNullOrWhiteSpace(abs.GetString()))
        {
            results.Add(new WebSearchResult(
                doc.TryGetProperty("Heading", out var h) ? h.GetString() ?? query : query,
                doc.TryGetProperty("AbstractURL", out var au) ? au.GetString() ?? string.Empty : string.Empty,
                abs.GetString()!));
        }
    }

    /// <summary>RelatedTopics aninhados — folhas com FirstURL viram resultados.</summary>
    private static void AddDdgTopic(JsonElement topic, List<WebSearchResult> results)
    {
        if (topic.TryGetProperty("Topics", out var nested))
        {
            foreach (var n in nested.EnumerateArray())
            {
                AddDdgTopic(n, results);
            }
            return;
        }
        var text = topic.TryGetProperty("Text", out var t) ? t.GetString() : null;
        var link = topic.TryGetProperty("FirstURL", out var u) ? u.GetString() : null;
        if (!string.IsNullOrEmpty(link))
        {
            results.Add(new WebSearchResult(text ?? link, link, text ?? string.Empty));
        }
    }

    private static async Task<List<WebSearchResult>> TavilyAsync(
        HttpClient client, RetrievalConfig cfg, string query, int count, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cfg.TavilyApiKey))
        {
            return [];
        }

        var payload = new { api_key = cfg.TavilyApiKey, query, max_results = count };
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = JsonContent.Create(payload, options: JsonOptions),
        };
        var doc = await SendJsonAsync(client, TavilyEngine, request, ct);
        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty(ResultsKey, out var items))
        {
            foreach (var item in items.EnumerateArray().Take(count))
            {
                results.Add(new WebSearchResult(
                    StrProp(item, TitleKey), StrProp(item, "url"), StrProp(item, "content")));
            }
        }
        return results;
    }

    private static async Task<List<WebSearchResult>> BraveAsync(
        HttpClient client, RetrievalConfig cfg, string query, int count, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cfg.BraveApiKey))
        {
            return [];
        }

        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={count}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Subscription-Token", cfg.BraveApiKey);
        var doc = await SendJsonAsync(client, BraveEngine, request, ct);

        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty("web", out var web)
            && web.TryGetProperty(ResultsKey, out var items))
        {
            foreach (var item in items.EnumerateArray().Take(count))
            {
                results.Add(new WebSearchResult(
                    StrProp(item, TitleKey), StrProp(item, "url"), StrProp(item, "description")));
            }
        }
        return results;
    }

    private static async Task<List<WebSearchResult>> GooglePseAsync(
        HttpClient client, RetrievalConfig cfg, string query, int count, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cfg.GooglePseApiKey) || string.IsNullOrEmpty(cfg.GooglePseEngineId))
        {
            return [];
        }

        var baseUrl = (cfg.GooglePseBaseUrl ?? "https://www.googleapis.com").TrimEnd('/');
        var url = $"{baseUrl}/customsearch/v1?key={Uri.EscapeDataString(cfg.GooglePseApiKey)}"
            + $"&cx={Uri.EscapeDataString(cfg.GooglePseEngineId)}"
            + $"&q={Uri.EscapeDataString(query)}&num={count}";
        var doc = await GetJsonAsync(client, GooglePseEngine, url, ct);
        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray().Take(count))
            {
                results.Add(new WebSearchResult(
                    StrProp(item, TitleKey), StrProp(item, "link"), StrProp(item, "snippet")));
            }
        }
        return results;
    }

    private static async Task<List<WebSearchResult>> JinaAsync(
        HttpClient client, RetrievalConfig cfg, string query, int count, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cfg.JinaApiKey))
        {
            return [];
        }

        var baseUrl = (cfg.JinaBaseUrl ?? "https://s.jina.ai").TrimEnd('/');
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}?q={Uri.EscapeDataString(query)}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {cfg.JinaApiKey}");
        var doc = await SendJsonAsync(client, "jina", request, ct);

        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty("data", out var items))
        {
            foreach (var item in items.EnumerateArray().Take(count))
            {
                results.Add(MapJinaItem(item));
            }
        }
        return results;
    }

    /// <summary>Mapeia um item Jina — description ausente cai para content.</summary>
    private static WebSearchResult MapJinaItem(JsonElement item)
    {
        var desc = item.TryGetProperty("description", out var d)
            ? d.GetString() ?? string.Empty
            : StrProp(item, "content");
        return new WebSearchResult(
            StrProp(item, TitleKey), StrProp(item, "url"), desc);
    }

    private static async Task<List<WebSearchResult>> ExaAsync(
        HttpClient client, RetrievalConfig cfg, string query, int count, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cfg.ExaApiKey))
        {
            return [];
        }

        var baseUrl = (cfg.ExaBaseUrl ?? "https://api.exa.ai").TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/search");
        request.Headers.TryAddWithoutValidation("x-api-key", cfg.ExaApiKey);
        request.Content = JsonContent.Create(
            new { query, numResults = count, contents = new { text = true } }, options: JsonOptions);
        var doc = await SendJsonAsync(client, "exa", request, ct);

        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty(ResultsKey, out var items))
        {
            foreach (var item in items.EnumerateArray().Take(count))
            {
                results.Add(MapExaItem(item));
            }
        }
        return results;
    }

    /// <summary>Mapeia um item Exa — text ausente cai para summary, cap 500.</summary>
    private static WebSearchResult MapExaItem(JsonElement item)
    {
        var snippet = item.TryGetProperty("text", out var tx)
            ? tx.GetString() ?? string.Empty
            : StrProp(item, "summary");
        return new WebSearchResult(
            StrProp(item, TitleKey), StrProp(item, "url"),
            snippet.Length > 500 ? snippet[..500] : snippet);
    }

    private static async Task<List<WebSearchResult>> KagiAsync(
        HttpClient client, RetrievalConfig cfg, string query, int count, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cfg.KagiApiKey))
        {
            return [];
        }

        var baseUrl = (cfg.KagiBaseUrl ?? "https://kagi.com").TrimEnd('/');
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{baseUrl}/api/v0/search?q={Uri.EscapeDataString(query)}&limit={count}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bot {cfg.KagiApiKey}");
        var doc = await SendJsonAsync(client, "kagi", request, ct);

        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty("data", out var items))
        {
            // t=0 é resultado de busca; outros tipos (related) são ignorados.
            foreach (var item in items.EnumerateArray()
                         .Where(i => i.TryGetProperty("t", out var t) && t.GetInt32() == 0))
            {
                results.Add(new WebSearchResult(
                    StrProp(item, TitleKey), StrProp(item, "url"), StrProp(item, "snippet")));
            }
        }
        return results.Take(count).ToList();
    }

    private static async Task<List<WebSearchResult>> PerplexityAsync(
        HttpClient client, RetrievalConfig cfg, string query, int count, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cfg.PerplexityApiKey))
        {
            return [];
        }

        var baseUrl = (cfg.PerplexityBaseUrl ?? "https://api.perplexity.ai").TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {cfg.PerplexityApiKey}");
        request.Content = JsonContent.Create(
            new
            {
                model = "sonar",
                messages = new[] { new { role = "user", content = query } },
            }, options: JsonOptions);
        var doc = await SendJsonAsync(client, "perplexity", request, ct);

        // Upstream: perplexity devolve citações — cada URL vira um resultado.
        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty("citations", out var citations))
        {
            foreach (var link in citations.EnumerateArray().Take(count)
                         .Select(c => c.GetString())
                         .Where(l => !string.IsNullOrEmpty(l)))
            {
                var title = Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.Host : link;
                results.Add(new WebSearchResult(title ?? link!, link!, string.Empty));
            }
        }
        return results;
    }
}

/// <summary>
/// Resultado detalhado de uma busca (SPEC-20261016-websearch-failover):
/// provider efetivo, cadeia de failover ("tavily→brave") e erro agregado
/// <c>all-providers-cooling</c> com segundos até o próximo retry quando
/// todos os candidatos estão em cooldown.
/// </summary>
/// <param name="Results">Resultados (vazio em erro).</param>
/// <param name="Provider">Provider que respondeu (ou o último tentado).</param>
/// <param name="FailoverChain">Cadeia percorrida quando houve failover.</param>
/// <param name="Error">Código de erro agregado (ex.: all-providers-cooling).</param>
/// <param name="RetryAfterSeconds">Segundos até o cooldown mais próximo expirar.</param>
public sealed record WebSearchOutcome(
    List<WebSearchResult> Results,
    string Provider,
    string? FailoverChain,
    string? Error = null,
    int? RetryAfterSeconds = null);

/// <summary>
/// Falha HTTP de um provider de busca — carrega o status e o Retry-After
/// para o cooldown do modo auto. Herda de <see cref="HttpRequestException"/>
/// para o modo fixo manter o comportamento atual (endpoint → 502).
/// </summary>
public sealed class WebSearchProviderException(
    string provider, HttpStatusCode? status, TimeSpan? retryAfter)
    : HttpRequestException($"web search provider '{provider}' falhou ({status})",
        inner: null, statusCode: status)
{
    /// <summary>Provider que falhou.</summary>
    public string Provider { get; } = provider;

    /// <summary>Retry-After devolvido pelo provider (quando presente).</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
