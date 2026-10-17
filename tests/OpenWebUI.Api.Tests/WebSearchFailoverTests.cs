using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do modo <c>engine=auto</c> de web search
/// (SPEC-20261016-websearch-failover, slice #406): failover 429→outro
/// provider, cooldown pulando provider falho, esgotamento
/// <c>all-providers-cooling</c> e comportamento inalterado no modo fixo.
/// </summary>
[TestFixture]
[NonParallelizable] // mexe nos cooldowns estáticos do WebSearchService.
public class WebSearchFailoverTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        WebSearchService.ResetCooldowns();
        WebSearchService.Now = () => DateTimeOffset.UtcNow;
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-wsf-{Guid.NewGuid():N}.db");
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        WebSearchService.ResetCooldowns();
        WebSearchService.Now = () => DateTimeOffset.UtcNow;
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    /// <summary>Configura retrieval.config com engine + chaves.</summary>
    private async Task<ConfigService> ConfigureAsync(AppDbContext db, object cfg)
    {
        var config = new ConfigService(db, TestCache.Create());
        db.ConfigEntries.Add(new ConfigEntry
        {
            Key = "retrieval.config",
            ValueJson = JsonSerializer.Serialize(cfg),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync();
        return config;
    }

    private static readonly object AutoTavilyBrave = new
    {
        engine = "auto",
        tavilyApiKey = "key-tavily",
        braveApiKey = "key-brave",
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body) };

    private static HttpResponseMessage RateLimited(int retryAfterSeconds = 0)
    {
        var r = Json(HttpStatusCode.TooManyRequests, "{}");
        if (retryAfterSeconds > 0)
        {
            r.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(retryAfterSeconds));
        }
        return r;
    }

    private const string BraveJson = "{\"web\":{\"results\":[{\"title\":\"B1\",\"url\":\"http://b1.example\",\"description\":\"trecho b1\"}]}}";
    private const string DdgJson = "{\"AbstractText\":\"resposta ddg\",\"AbstractURL\":\"http://ddg.example\",\"Heading\":\"DDG\"}";

    /// <summary>Handler que roteia por host e conta chamadas por host.</summary>
    private sealed class RouterHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();
        public ConcurrentDictionary<string, int> Hits { get; } = new();

        public void Route(string host, Func<HttpRequestMessage, HttpResponseMessage> handler)
            => _routes[host] = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            Hits.AddOrUpdate(host, 1, (_, n) => n + 1);
            return Task.FromResult(_routes.TryGetValue(host, out var h)
                ? h(request)
                : Json(HttpStatusCode.NotFound, "{}"));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>AC1: auto com tavily 429 → brave responde com cadeia registrada.</summary>
    [Test]
    public async Task Auto_Tavily429_FailoverParaBrave()
    {
        await using var db = NewDb();
        var config = await ConfigureAsync(db, AutoTavilyBrave);
        var router = new RouterHandler();
        router.Route("api.tavily.com", _ => RateLimited(retryAfterSeconds: 5));
        router.Route("api.search.brave.com", _ => Json(HttpStatusCode.OK, BraveJson));
        var search = new WebSearchService(new Factory(router), config);

        var outcome = await search.SearchDetailedAsync("devin", 5);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.Not.Null);
            Assert.That(outcome!.Error, Is.Null);
            Assert.That(outcome.Provider, Is.EqualTo("brave"));
            Assert.That(outcome.FailoverChain, Is.EqualTo("tavily→brave"));
            Assert.That(outcome.Results, Has.Count.EqualTo(1));
            Assert.That(outcome.Results[0].Title, Is.EqualTo("B1"));
        });
    }

    /// <summary>AC2: provider em cooldown nem é tentado na busca seguinte.</summary>
    [Test]
    public async Task Auto_ProviderEmCooldown_EhPulado()
    {
        await using var db = NewDb();
        var config = await ConfigureAsync(db, AutoTavilyBrave);
        var router = new RouterHandler();
        router.Route("api.tavily.com", _ => RateLimited());
        router.Route("api.search.brave.com", _ => Json(HttpStatusCode.OK, BraveJson));
        var search = new WebSearchService(new Factory(router), config);

        await search.SearchDetailedAsync("q1", 5);
        await search.SearchDetailedAsync("q2", 5);

        Assert.Multiple(() =>
        {
            Assert.That(router.Hits["api.tavily.com"], Is.EqualTo(1),
                "tavily em cooldown não pode ser chamado de novo");
            Assert.That(router.Hits["api.search.brave.com"], Is.EqualTo(2));
        });
    }

    /// <summary>AC3: todos os candidatos falhando → all-providers-cooling.</summary>
    [Test]
    public async Task Auto_TodosFalham_RetornaAllProvidersCooling()
    {
        await using var db = NewDb();
        var config = await ConfigureAsync(db, AutoTavilyBrave);
        var router = new RouterHandler();
        router.Route("api.tavily.com", _ => RateLimited(retryAfterSeconds: 7));
        router.Route("api.search.brave.com", _ => Json(HttpStatusCode.InternalServerError, "{}"));
        router.Route("api.duckduckgo.com", _ => Json(HttpStatusCode.InternalServerError, "{}"));
        var search = new WebSearchService(new Factory(router), config);

        var outcome = await search.SearchDetailedAsync("devin", 5);

        Assert.Multiple(() =>
        {
            Assert.That(outcome!.Error, Is.EqualTo("all-providers-cooling"));
            Assert.That(outcome.RetryAfterSeconds, Is.InRange(1, 60));
            Assert.That(outcome.Results, Is.Empty);
        });
    }

    /// <summary>Auto sem chaves → só duckduckgo é candidato.</summary>
    [Test]
    public async Task Auto_SemChaves_SoUsaDuckDuckGo()
    {
        await using var db = NewDb();
        var config = await ConfigureAsync(db, new { engine = "auto" });
        var router = new RouterHandler();
        router.Route("api.duckduckgo.com", _ => Json(HttpStatusCode.OK, DdgJson));
        var search = new WebSearchService(new Factory(router), config);

        var outcome = await search.SearchDetailedAsync("devin", 5);

        Assert.Multiple(() =>
        {
            Assert.That(outcome!.Provider, Is.EqualTo("duckduckgo"));
            Assert.That(outcome.Results, Has.Count.EqualTo(1));
            Assert.That(router.Hits.Keys, Is.EquivalentTo(new[] { "api.duckduckgo.com" }));
        });
    }

    /// <summary>AC4: engine fixa falhando propaga o erro (sem failover).</summary>
    [Test]
    public async Task Fixo_Tavily429_PropagaExcecao()
    {
        using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        db.ConfigEntries.Add(new ConfigEntry
        {
            Key = "retrieval.config",
            ValueJson = JsonSerializer.Serialize(
                new { engine = "tavily", tavilyApiKey = "key" }),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        db.SaveChanges();
        var router = new RouterHandler();
        router.Route("api.tavily.com", _ => RateLimited());
        var search = new WebSearchService(new Factory(router), config);

        var ex = await Assert.ThrowsAsync<WebSearchProviderException>(
            () => search.SearchAsync("devin", 5));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Provider, Is.EqualTo("tavily"));
            Assert.That(ex.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        });
    }

    /// <summary>RF-003: tool registra provider efetivo no output.</summary>
    [Test]
    public async Task Tool_Auto_MarcaCadeiaNoTexto()
    {
        await using var db = NewDb();
        var config = await ConfigureAsync(db, AutoTavilyBrave);
        var router = new RouterHandler();
        router.Route("api.tavily.com", _ => RateLimited());
        router.Route("api.search.brave.com", _ => Json(HttpStatusCode.OK, BraveJson));
        var tool = new WebSearchBuiltinTool(
            new WebSearchService(new Factory(router), config));

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("{\"query\":\"devin\"}").RootElement,
            new BuiltinToolContext("u1", "c1", null, "/tmp", "/tmp"), default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.StartWith("[via tavily→brave]"));
            Assert.That(result.Text, Does.Contain("B1"));
        });
    }

    /// <summary>Cooldown expira — provider volta a ser tentado.</summary>
    [Test]
    public async Task Auto_CooldownExpira_ProviderVolta()
    {
        await using var db = NewDb();
        var config = await ConfigureAsync(db, AutoTavilyBrave);
        var router = new RouterHandler();
        var tavilyOk = false;
        router.Route("api.tavily.com",
            _ => tavilyOk ? Json(HttpStatusCode.OK,
                "{\"results\":[{\"title\":\"T1\",\"url\":\"http://t1.example\",\"content\":\"t\"}]}")
                : RateLimited());
        router.Route("api.search.brave.com", _ => Json(HttpStatusCode.OK, BraveJson));
        var search = new WebSearchService(new Factory(router), config);

        var now = DateTimeOffset.UtcNow;
        WebSearchService.Now = () => now;

        // Falha inicial (429 sem Retry-After → 60s) → brave atende.
        var first = await search.SearchDetailedAsync("q1", 5);
        Assert.That(first!.Provider, Is.EqualTo("brave"));

        // Avança o relógio além do cooldown e "conserta" o tavily.
        now = now.AddSeconds(61);
        tavilyOk = true;
        var second = await search.SearchDetailedAsync("q2", 5);
        Assert.Multiple(() =>
        {
            Assert.That(second!.Provider, Is.EqualTo("tavily"));
            Assert.That(second.FailoverChain, Is.Null);
        });
    }
}
