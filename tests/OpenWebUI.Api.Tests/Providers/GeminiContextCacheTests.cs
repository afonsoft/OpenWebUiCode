using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests.Providers;

/// <summary>
/// SPEC-20261103-gemini-context-caching: CRUD de <c>cachedContents</c>
/// (RF-001), request cache-aware com hash estável + reuse (RF-002),
/// threshold + degrade graceful em erro (RF-003) e delete best-effort
/// dos caches criados no dispose (RF-004). Flag <c>context_caching</c>
/// em <c>ProviderConnection.Settings</c>; sem flag → wire idêntico.
/// </summary>
[TestFixture, IsolateEnvironment]
public class GeminiContextCacheTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-gcc-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(_db);
        _config = new ConfigService(_db, TestCache.Create());
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private ProviderService NewService(
        StubHttpHandler handler, GeminiContextCacheService? cache) =>
        new(new StubHttpFactory(handler), _config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build(),
            contextCache: cache);

    private GeminiContextCacheService NewCache(StubHttpHandler handler) =>
        new(new StubHttpFactory(handler),
            NullLogger<GeminiContextCacheService>.Instance);

    private Task SetProvidersAsync(params ProviderConnection[] providers) =>
        _config.SetAsync("connections",
            new ConnectionsConfig([], [], [], Providers: providers));

    private static ChatCompletionRequest Req(string system, string model = "gemini-2.0-flash") =>
        new(model,
            [new ChatCompletionMessage("system", system),
             new ChatCompletionMessage("user", "oi")],
            Stream: false, Connection: "google");

    private static JsonObject CachingSettings() => new()
    {
        ["context_caching"] = true,
        ["context_caching_min_tokens"] = 4,
    };

    private static string LongSystem() =>
        string.Concat(Enumerable.Repeat("contexto estático extenso ", 200));

    private const string OkResponse =
        "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":" +
        "[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}";

    /// <summary>Covers RF-001/RF-002: flag on + system ≥ threshold →
    /// 1ª run cria cachedContents e envia cachedContent (sem
    /// systemInstruction); 2ª run reusa via PATCH de TTL.</summary>
    [Test]
    public async Task FlagOn_CriaEReusaCache()
    {
        var http = new StubHttpHandler();
        var cache = NewCache(http);
        await SetProvidersAsync(new ProviderConnection(
            "google", "https://goog.test", "gk",
            Settings: CachingSettings()));
        http.Enqueue(HttpStatusCode.OK,
            "{\"name\":\"cachedContents/xyz\",\"model\":\"models/gemini-2.0-flash\"," +
            "\"expireTime\":\"2099-01-01T00:00:00Z\"}");
        http.Enqueue(HttpStatusCode.OK, OkResponse);
        http.Enqueue(HttpStatusCode.OK, "{\"name\":\"cachedContents/xyz\"}");
        http.Enqueue(HttpStatusCode.OK, OkResponse);

        var svc = NewService(http, cache);
        var req = Req(LongSystem());
        await svc.CompleteAsync(req);
        await svc.CompleteAsync(req);

        var reqs = http.Requests;
        Assert.Multiple(() =>
        {
            Assert.That(reqs, Has.Count.EqualTo(4));
            Assert.That(reqs[0].Method, Is.EqualTo("POST"));
            Assert.That(reqs[0].Url,
                Is.EqualTo("https://goog.test/cachedContents"));
            var create = JsonNode.Parse(reqs[0].Body)!;
            Assert.That(create["model"]!.GetValue<string>(),
                Is.EqualTo("models/gemini-2.0-flash"));
            Assert.That(
                create["systemInstruction"]!["parts"]![0]!["text"]!
                    .GetValue<string>(),
                Does.Contain("contexto estático"));
            Assert.That(create["ttl"], Is.Not.Null);

            Assert.That(reqs[1].Url, Does.Contain("generateContent"));
            var gen1 = JsonNode.Parse(reqs[1].Body)!;
            Assert.That(gen1["cachedContent"]!.GetValue<string>(),
                Is.EqualTo("cachedContents/xyz"));
            Assert.That(gen1["systemInstruction"], Is.Null);

            Assert.That(reqs[2].Method, Is.EqualTo("PATCH"));
            Assert.That(reqs[2].Url,
                Is.EqualTo("https://goog.test/cachedContents/xyz"));
            Assert.That(reqs[3].Url, Does.Contain("generateContent"));
            var gen2 = JsonNode.Parse(reqs[3].Body)!;
            Assert.That(gen2["cachedContent"]!.GetValue<string>(),
                Is.EqualTo("cachedContents/xyz"));
        });
    }

    /// <summary>Covers RF-002/AC: flag off → wire idêntico (system
    /// inline, sem cachedContent, zero requests ao CRUD).</summary>
    [Test]
    public async Task FlagOff_WireIdentico()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));
        http.Enqueue(HttpStatusCode.OK, OkResponse);

        await NewService(http, NewCache(http)).CompleteAsync(Req(LongSystem()));

        Assert.Multiple(() =>
        {
            Assert.That(http.Requests, Has.Count.EqualTo(1));
            var body = JsonNode.Parse(http.Requests[0].Body)!;
            Assert.That(body["systemInstruction"], Is.Not.Null);
            Assert.That(body["cachedContent"], Is.Null);
        });
    }

    /// <summary>Covers RF-003: system abaixo do threshold estimado →
    /// nenhuma chamada ao cachedContents.</summary>
    [Test]
    public async Task AbaixoDoThreshold_SemCache()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(new ProviderConnection(
            "google", "https://goog.test", "gk",
            Settings: new JsonObject { ["context_caching"] = true }));
        http.Enqueue(HttpStatusCode.OK, OkResponse);

        await NewService(http, NewCache(http)).CompleteAsync(Req("curto"));

        Assert.Multiple(() =>
        {
            Assert.That(http.Requests, Has.Count.EqualTo(1));
            Assert.That(http.Requests[0].Url, Does.Contain("generateContent"));
            Assert.That(JsonNode.Parse(http.Requests[0].Body)!["cachedContent"],
                Is.Null);
        });
    }

    /// <summary>Covers RF-003: erro 4xx no create → run segue sem cache,
    /// systemInstruction preservado inline (degrade, nunca falha a run).</summary>
    [Test]
    public async Task CreateFalha_DegradeSemCache()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(new ProviderConnection(
            "google", "https://goog.test", "gk",
            Settings: CachingSettings()));
        http.Enqueue(HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":400,\"message\":\"cache too small\"}}");
        http.Enqueue(HttpStatusCode.OK, OkResponse);

        var content = await NewService(http, NewCache(http))
            .CompleteAsync(Req(LongSystem()));

        Assert.Multiple(() =>
        {
            Assert.That(content, Is.EqualTo("ok"));
            Assert.That(http.Requests, Has.Count.EqualTo(2));
            var gen = JsonNode.Parse(http.Requests[1].Body)!;
            Assert.That(gen["cachedContent"], Is.Null);
            Assert.That(gen["systemInstruction"], Is.Not.Null);
        });
    }

    /// <summary>Covers RF-004: dispose do serviço deleta best-effort os
    /// caches que ele criou nesta instância.</summary>
    [Test]
    public async Task DisposeAsync_DeletaCachesCriados()
    {
        var http = new StubHttpHandler();
        var cache = NewCache(http);
        await SetProvidersAsync(new ProviderConnection(
            "google", "https://goog.test", "gk",
            Settings: CachingSettings()));
        http.Enqueue(HttpStatusCode.OK,
            "{\"name\":\"cachedContents/xyz\",\"expireTime\":\"2099-01-01T00:00:00Z\"}");
        http.Enqueue(HttpStatusCode.OK, OkResponse);

        await NewService(http, cache).CompleteAsync(Req(LongSystem()));
        await cache.DisposeAsync();

        var last = http.Requests[^1];
        Assert.Multiple(() =>
        {
            Assert.That(last.Method, Is.EqualTo("DELETE"));
            Assert.That(last.Url,
                Is.EqualTo("https://goog.test/cachedContents/xyz"));
        });
    }
}
