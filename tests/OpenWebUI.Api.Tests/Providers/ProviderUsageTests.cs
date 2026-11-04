using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests.Providers;

/// <summary>
/// SPEC-20261103-providers-usage-tokens (RF-001 a RF-004): contabilização
/// de usage Anthropic (<c>input_tokens</c>/<c>output_tokens</c>/cache)
/// e Gemini (<c>usageMetadata</c>) em <see cref="ProviderUsage"/> —
/// completo e streamed. Ausência de usage → null, nunca estimado.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ProviderUsageTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-put-{Guid.NewGuid():N}.db");
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

    private static ChatCompletionRequest Req(string model, string connection) =>
        new(model, [new ChatCompletionMessage("user", "oi")],
            Connection: connection);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures", "Providers", name));

    /// <summary>Covers RF-001/RF-004: usage buffered do Claude — input,
    /// output e campos de cache mapeados.</summary>
    [Test]
    public async Task Anthropic_Complete_UsageComCache_Mapeado()
    {
        var http = new StubHttpHandler();
        http.Enqueue(System.Net.HttpStatusCode.OK,
            Fixture("anthropic-complete-usage.json"));
        await SetProvidersAsync(
            new ProviderConnection("anthropic", "https://ant.test", "k"));

        var result = await NewService(http).CompleteWithToolsAsync(
            Req("claude-sonnet-4", "anthropic"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Usage, Is.Not.Null);
            Assert.That(result.Usage!.PromptTokens, Is.EqualTo(25));
            Assert.That(result.Usage.CompletionTokens, Is.EqualTo(10));
            Assert.That(result.Usage.CachedTokens, Is.EqualTo(100));
            Assert.That(result.Usage.CacheWriteTokens, Is.EqualTo(40));
        });
    }

    /// <summary>Covers RF-001/RF-004: stream do Claude — input/cache do
    /// message_start + output acumulado do último message_delta.</summary>
    [Test]
    public async Task Anthropic_Stream_Usage_StartMaisDelta()
    {
        var http = new StubHttpHandler();
        http.Enqueue(System.Net.HttpStatusCode.OK,
            Fixture("anthropic-stream-usage.sse"));
        await SetProvidersAsync(
            new ProviderConnection("anthropic", "https://ant.test", "k"));

        var result = await NewService(http).CompleteWithToolsStreamingAsync(
            Req("claude-sonnet-4", "anthropic"), null);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Usage, Is.Not.Null);
            Assert.That(result.Usage!.PromptTokens, Is.EqualTo(25));
            Assert.That(result.Usage.CompletionTokens, Is.EqualTo(12));
            Assert.That(result.Usage.CachedTokens, Is.EqualTo(100));
            Assert.That(result.Usage.CacheWriteTokens, Is.EqualTo(40));
        });
    }

    /// <summary>Covers RF-002/RF-004: usageMetadata buffered do Gemini —
    /// prompt, candidates, thoughts e cached mapeados.</summary>
    [Test]
    public async Task Google_Complete_UsageMetadata_Mapeado()
    {
        var http = new StubHttpHandler();
        http.Enqueue(System.Net.HttpStatusCode.OK,
            Fixture("google-complete-usage.json"));
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));

        var result = await NewService(http).CompleteWithToolsAsync(
            Req("gemini-2.0-flash", "google"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Usage, Is.Not.Null);
            Assert.That(result.Usage!.PromptTokens, Is.EqualTo(5));
            Assert.That(result.Usage.CompletionTokens, Is.EqualTo(3));
            Assert.That(result.Usage.ReasoningTokens, Is.EqualTo(2));
            Assert.That(result.Usage.CachedTokens, Is.EqualTo(7));
        });
    }

    /// <summary>Covers RF-002/RF-004: usageMetadata no último chunk do
    /// stream Gemini alimenta o Usage do ProviderCompletion.</summary>
    [Test]
    public async Task Google_Stream_UsageMetadata_UltimoChunkVence()
    {
        var http = new StubHttpHandler();
        http.Enqueue(System.Net.HttpStatusCode.OK,
            Fixture("google-stream-usage.sse"));
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));

        var result = await NewService(http).CompleteWithToolsStreamingAsync(
            Req("gemini-2.0-flash", "google"), null);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Usage, Is.Not.Null);
            Assert.That(result.Usage!.PromptTokens, Is.EqualTo(5));
            Assert.That(result.Usage.CompletionTokens, Is.EqualTo(3));
            Assert.That(result.Usage.ReasoningTokens, Is.EqualTo(2));
            Assert.That(result.Usage.CachedTokens, Is.EqualTo(7));
        });
    }

    /// <summary>Covers RF-003: resposta sem usage → Usage null (nunca
    /// estimado, contrato do SPEC agent-steps-usage).</summary>
    [Test]
    public async Task Anthropic_Complete_SemUsage_Null()
    {
        var http = new StubHttpHandler();
        http.Enqueue(System.Net.HttpStatusCode.OK,
            "{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\"," +
            "\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]," +
            "\"stop_reason\":\"end_turn\"}");
        await SetProvidersAsync(
            new ProviderConnection("anthropic", "https://ant.test", "k"));

        var result = await NewService(http).CompleteWithToolsAsync(
            Req("claude-sonnet-4", "anthropic"));

        Assert.That(result.Usage, Is.Null);
    }
}
