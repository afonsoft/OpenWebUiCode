using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Web search via engine configurada em <c>retrieval.config</c>:
/// searxng (self-hosted), duckduckgo (instant answer), tavily, brave,
/// google_pse, jina, exa, kagi e perplexity (API key).
/// Chaves nunca são retornadas — apenas usadas no request à engine.
/// </summary>
public class WebSearchService(IHttpClientFactory httpFactory, ConfigService config)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Executa a busca na engine configurada. Retorna null quando web search
    /// está desabilitada (engine "none") — o endpoint converte em 503.
    /// </summary>
    /// <param name="query">Termo de busca.</param>
    /// <param name="count">Máximo de resultados (default 5).</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<List<WebSearchResult>?> SearchAsync(
        string query, int count = 5, CancellationToken ct = default)
    {
        var cfg = await config.GetAsync("retrieval.config", RetrievalConfig.Default, ct);
        var client = httpFactory.CreateClient(nameof(WebSearchService));
        client.Timeout = TimeSpan.FromSeconds(15);

        return cfg.Engine switch
        {
            "searxng" => await SearxngAsync(client, cfg, query, count, ct),
            "duckduckgo" => await DuckDuckGoAsync(client, query, count, ct),
            "tavily" => await TavilyAsync(client, cfg, query, count, ct),
            "brave" => await BraveAsync(client, cfg, query, count, ct),
            "google_pse" => await GooglePseAsync(client, cfg, query, count, ct),
            "jina" => await JinaAsync(client, cfg, query, count, ct),
            "exa" => await ExaAsync(client, cfg, query, count, ct),
            "kagi" => await KagiAsync(client, cfg, query, count, ct),
            "perplexity" => await PerplexityAsync(client, cfg, query, count, ct),
            _ => null,
        };
    }

    private static async Task<List<WebSearchResult>> SearxngAsync(
        HttpClient client, RetrievalConfig cfg, string query, int count, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.SearxngBaseUrl))
        {
            return [];
        }

        var url = $"{cfg.SearxngBaseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json";
        var doc = await client.GetFromJsonAsync<JsonElement>(url, JsonOptions, ct);
        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty("results", out var items))
        {
            foreach (var item in items.EnumerateArray().Take(count))
            {
                results.Add(new WebSearchResult(
                    item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty));
            }
        }
        return results;
    }

    private static async Task<List<WebSearchResult>> DuckDuckGoAsync(
        HttpClient client, string query, int count, CancellationToken ct)
    {
        var url = $"https://api.duckduckgo.com/?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1";
        var doc = await client.GetFromJsonAsync<JsonElement>(url, JsonOptions, ct);
        var results = new List<WebSearchResult>();

        void AddTopic(JsonElement topic)
        {
            if (topic.TryGetProperty("Topics", out var nested))
            {
                foreach (var n in nested.EnumerateArray())
                {
                    AddTopic(n);
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

        if (doc.TryGetProperty("AbstractText", out var abs)
            && !string.IsNullOrWhiteSpace(abs.GetString()))
        {
            results.Add(new WebSearchResult(
                doc.TryGetProperty("Heading", out var h) ? h.GetString() ?? query : query,
                doc.TryGetProperty("AbstractURL", out var au) ? au.GetString() ?? string.Empty : string.Empty,
                abs.GetString()!));
        }
        if (doc.TryGetProperty("RelatedTopics", out var topics))
        {
            foreach (var topic in topics.EnumerateArray())
            {
                AddTopic(topic);
            }
        }
        return results.Take(count).ToList();
    }

    private static async Task<List<WebSearchResult>> TavilyAsync(
        HttpClient client, RetrievalConfig cfg, string query, int count, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(cfg.TavilyApiKey))
        {
            return [];
        }

        var payload = new { api_key = cfg.TavilyApiKey, query, max_results = count };
        var doc = await client.PostAsJsonAsync(
            "https://api.tavily.com/search", payload, JsonOptions, ct)
            .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct), ct);
        var results = new List<WebSearchResult>();
        if (doc.Result.TryGetProperty("results", out var items))
        {
            foreach (var item in items.EnumerateArray().Take(count))
            {
                results.Add(new WebSearchResult(
                    item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty));
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
        var response = await client.SendAsync(request, ct);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);

        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty("web", out var web)
            && web.TryGetProperty("results", out var items))
        {
            foreach (var item in items.EnumerateArray().Take(count))
            {
                results.Add(new WebSearchResult(
                    item.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty));
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
        var doc = await client.GetFromJsonAsync<JsonElement>(url, JsonOptions, ct);
        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray().Take(count))
            {
                results.Add(new WebSearchResult(
                    item.TryGetProperty("title", out var ti) ? ti.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("link", out var l) ? l.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("snippet", out var s) ? s.GetString() ?? string.Empty : string.Empty));
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
        var response = await client.SendAsync(request, ct);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);

        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty("data", out var items))
        {
            foreach (var item in items.EnumerateArray().Take(count))
            {
                results.Add(new WebSearchResult(
                    item.TryGetProperty("title", out var ti) ? ti.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty
                        : item.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty));
            }
        }
        return results;
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
        var response = await client.SendAsync(request, ct);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);

        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty("results", out var items))
        {
            foreach (var item in items.EnumerateArray().Take(count))
            {
                var snippet = item.TryGetProperty("text", out var tx) ? tx.GetString() ?? string.Empty
                    : item.TryGetProperty("summary", out var su) ? su.GetString() ?? string.Empty : string.Empty;
                results.Add(new WebSearchResult(
                    item.TryGetProperty("title", out var ti) ? ti.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                    snippet.Length > 500 ? snippet[..500] : snippet));
            }
        }
        return results;
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
        var response = await client.SendAsync(request, ct);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);

        var results = new List<WebSearchResult>();
        if (doc.TryGetProperty("data", out var items))
        {
            // t=0 é resultado de busca; outros tipos (related) são ignorados.
            foreach (var item in items.EnumerateArray()
                         .Where(i => i.TryGetProperty("t", out var t) && t.GetInt32() == 0))
            {
                results.Add(new WebSearchResult(
                    item.TryGetProperty("title", out var ti) ? ti.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("snippet", out var s) ? s.GetString() ?? string.Empty : string.Empty));
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
        var response = await client.SendAsync(request, ct);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);

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
