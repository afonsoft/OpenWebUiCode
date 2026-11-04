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
/// SPEC-20261103-anthropic-messages-v2: blocos <c>document</c> para PDF
/// com citations habilitado (RF-001), <c>metadata.user_id</c> (RF-003) e
/// classificação retry 429/529/anthropic-error (RF-004 — já coberta por
/// <see cref="ProviderRetryPolicy"/>, provada aqui por contrato).
/// </summary>
[TestFixture, IsolateEnvironment]
public class AnthropicMessagesV2Tests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-amv-{Guid.NewGuid():N}.db");
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

    private ProviderService NewService(StubHttpHandler handler) =>
        new(new StubHttpFactory(handler), _config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());

    private Task SetProvidersAsync(params ProviderConnection[] providers) =>
        _config.SetAsync("connections",
            new ConnectionsConfig([], [], [], Providers: providers));

    private async Task<JsonNode> CaptureAnthropicPayloadAsync(
        StubHttpHandler http, ChatCompletionRequest req, string? userId = null)
    {
        http.Enqueue(HttpStatusCode.OK,
            "{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\"," +
            "\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]," +
            "\"stop_reason\":\"end_turn\"}");
        await NewService(http).CompleteAsync(req, userId);
        return JsonNode.Parse(http.Requests[^1].Body)!;
    }

    /// <summary>Covers RF-001: anexo application/pdf vira bloco document
    /// base64 com citations habilitado.</summary>
    [Test]
    public async Task PdfAttachment_ViraDocumentBlockComCitations()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(
            new ProviderConnection("anthropic", "https://ant.test", "k"));
        var req = new ChatCompletionRequest("claude-sonnet-4",
            [new ChatCompletionMessage("user", "leia",
                Images: [new ChatImageData("application/pdf", "QUJD")])],
            Stream: false, Connection: "anthropic");

        var body = await CaptureAnthropicPayloadAsync(http, req);

        var blocks = body["messages"]![0]!["content"]!.AsArray();
        var doc = blocks.Last()!;
        Assert.Multiple(() =>
        {
            Assert.That(doc["type"]!.GetValue<string>(), Is.EqualTo("document"));
            Assert.That(doc["source"]!["type"]!.GetValue<string>(),
                Is.EqualTo("base64"));
            Assert.That(doc["source"]!["media_type"]!.GetValue<string>(),
                Is.EqualTo("application/pdf"));
            Assert.That(doc["source"]!["data"]!.GetValue<string>(),
                Is.EqualTo("QUJD"));
            Assert.That(doc["citations"]!["enabled"]!.GetValue<bool>(),
                Is.True);
        });
    }

    /// <summary>Covers RF-001: imagem comum segue como bloco image —
    /// sem mudança de wire para não-PDF.</summary>
    [Test]
    public async Task PngAttachment_SegueImageBlock()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(
            new ProviderConnection("anthropic", "https://ant.test", "k"));
        var req = new ChatCompletionRequest("claude-sonnet-4",
            [new ChatCompletionMessage("user", "olha",
                Images: [new ChatImageData("image/png", "iVBOR")])],
            Stream: false, Connection: "anthropic");

        var body = await CaptureAnthropicPayloadAsync(http, req);

        var doc = body["messages"]![0]!["content"]!.AsArray().Last()!;
        Assert.Multiple(() =>
        {
            Assert.That(doc["type"]!.GetValue<string>(), Is.EqualTo("image"));
            Assert.That(doc["source"]!["media_type"]!.GetValue<string>(),
                Is.EqualTo("image/png"));
            Assert.That(doc["citations"], Is.Null);
        });
    }

    /// <summary>Covers RF-003: userId da run vira metadata.user_id.</summary>
    [Test]
    public async Task UserId_ViraMetadataUserId()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(
            new ProviderConnection("anthropic", "https://ant.test", "k"));
        var req = new ChatCompletionRequest("claude-sonnet-4",
            [new ChatCompletionMessage("user", "oi")],
            Stream: false, Connection: "anthropic");

        var body = await CaptureAnthropicPayloadAsync(http, req, "user-abc-1");

        Assert.That(body["metadata"]!["user_id"]!.GetValue<string>(),
            Is.EqualTo("user-abc-1"));
    }

    /// <summary>Covers RF-003: sem userId não há chave metadata no wire.</summary>
    [Test]
    public async Task SemUserId_SemMetadata()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(
            new ProviderConnection("anthropic", "https://ant.test", "k"));
        var req = new ChatCompletionRequest("claude-sonnet-4",
            [new ChatCompletionMessage("user", "oi")],
            Stream: false, Connection: "anthropic");

        var body = await CaptureAnthropicPayloadAsync(http, req);

        Assert.That(body["metadata"], Is.Null);
    }

    /// <summary>Covers RF-004: 529 overloaded classifica ServerError
    /// (retryable) e 429 classifica RateLimit (retryable, honra
    /// Retry-After) — contrato da retry policy sobre exceções já
    /// produzidas pelo pipeline Anthropic.</summary>
    [Test]
    public void RetryPolicy_529E429_ClassificamRetryable()
    {
        var overloaded = new ProviderHttpException(
            (HttpStatusCode)529, null, "{\"error\":{\"type\":\"overloaded_error\"}}");
        var rateLimit = new ProviderHttpException(
            HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30), null);
        var midStream = new ProviderHttpException(
            HttpStatusCode.BadGateway, null, "{}", midStream: true);

        Assert.Multiple(() =>
        {
            Assert.That(ProviderRetryPolicy.Classify(overloaded),
                Is.EqualTo(ProviderRetryReason.ServerError));
            Assert.That(ProviderRetryPolicy.Classify(rateLimit),
                Is.EqualTo(ProviderRetryReason.RateLimit));
            Assert.That(ProviderRetryPolicy.Classify(midStream),
                Is.EqualTo(ProviderRetryReason.ServerError));
            Assert.That(ProviderRetryPolicy.Decide(
                overloaded, attempt: 0, consecutiveTimeouts: 0,
                jitter: false).Retry, Is.True);
            Assert.That(ProviderRetryPolicy.Decide(
                rateLimit, attempt: 0, consecutiveTimeouts: 0,
                jitter: false).Delay,
                Is.EqualTo(TimeSpan.FromSeconds(30)));
        });
    }
}
