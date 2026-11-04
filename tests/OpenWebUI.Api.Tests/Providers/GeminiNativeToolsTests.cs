using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests.Providers;

/// <summary>
/// SPEC-20261103-gemini-native-tools: <c>native_tools</c> → entries no
/// <c>tools[]</c> (google_search/code_execution/url_context — RF-001),
/// grounding → fontes markdown no content (RF-002), executableCode/
/// codeExecutionResult demarcados (RF-003) e <c>thinking_budget</c> →
/// thinkingConfig (RF-004). Sem native_tools → wire idêntico (RF-005).
/// </summary>
[TestFixture, IsolateEnvironment]
public class GeminiNativeToolsTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-gnt-{Guid.NewGuid():N}.db");
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

    private static ChatCompletionRequest Req(
        Dictionary<string, object>? extraParams = null,
        IReadOnlyList<JsonElement>? tools = null) =>
        new("gemini-2.0-flash", [new ChatCompletionMessage("user", "oi")],
            Stream: false, Connection: "google",
            Params: extraParams, Tools: tools);

    private async Task<JsonNode> CaptureGooglePayloadAsync(
        StubHttpHandler http, ChatCompletionRequest req)
    {
        http.Enqueue(HttpStatusCode.OK,
            "{\"candidates\":[{\"content\":{\"role\":\"model\"," +
            "\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}");
        await NewService(http).CompleteAsync(req);
        return JsonNode.Parse(http.Requests[^1].Body)!;
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures", "Providers", name));

    /// <summary>Covers RF-001: native_tools csv → entries google_search +
    /// code_execution no tools[].</summary>
    [Test]
    public async Task NativeTools_EntradasNoToolsArray()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));

        var body = await CaptureGooglePayloadAsync(http,
            Req(new() { ["native_tools"] = "google_search;code_execution" }));

        TestContext.Out.WriteLine(http.Requests[^1].Body);
        var tools = body["tools"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(tools.Any(t => t?["google_search"] is not null), Is.True);
            Assert.That(tools.Any(t => t?["code_execution"] is not null), Is.True);
            Assert.That(tools.Any(t => t?["url_context"] is not null), Is.False);
        });
    }

    /// <summary>Covers RF-001: natives coexistem com functionDeclarations.</summary>
    [Test]
    public async Task NativeTools_CoexistemComFunctionDeclarations()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));
        var toolDef = JsonDocument.Parse(
            "{\"type\":\"function\",\"function\":{\"name\":\"get_time\"," +
            "\"description\":\"hora\",\"parameters\":{\"type\":\"object\"}}}");

        var body = await CaptureGooglePayloadAsync(http,
            Req(new() { ["native_tools"] = "url_context" },
                [toolDef.RootElement.Clone()]));

        var tools = body["tools"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(tools.Any(t => t?["functionDeclarations"] is not null), Is.True);
            Assert.That(tools.Any(t => t?["url_context"] is not null), Is.True);
        });
    }

    /// <summary>Covers RF-005: sem native_tools → nenhuma entry nativa e,
    /// sem Tools, sem chave tools (wire idêntico ao anterior).</summary>
    [Test]
    public async Task SemNativeTools_SemToolsArray()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));

        var body = await CaptureGooglePayloadAsync(http, Req());

        Assert.That(body["tools"], Is.Null);
    }

    /// <summary>Covers RF-004: thinking_budget → generationConfig.
    /// thinkingConfig.thinkingBudget.</summary>
    [Test]
    public async Task ThinkingBudget_ViraThinkingConfig()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));

        var body = await CaptureGooglePayloadAsync(http,
            Req(new() { ["thinking_budget"] = 2048 }));

        Assert.That(
            body["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!
                .GetValue<int>(),
            Is.EqualTo(2048));
    }

    /// <summary>Covers RF-002/RF-003: stream com executableCode +
    /// codeExecutionResult + groundingMetadata → blocos demarcados e
    /// lista de fontes deduplicada no content.</summary>
    [Test]
    public async Task Stream_ExecEGrounding_BlocosEFontes()
    {
        var http = new StubHttpHandler();
        http.Enqueue(HttpStatusCode.OK,
            Fixture("google-stream-exec-grounding.sse"));
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));

        var result = await NewService(http).CompleteWithToolsStreamingAsync(
            new ChatCompletionRequest("gemini-2.0-flash",
                [new ChatCompletionMessage("user", "oi")],
                Connection: "google"),
            null);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            var content = result!.Content;
            Assert.That(content, Does.Contain("```python"));
            Assert.That(content, Does.Contain("print(1+1)"));
            Assert.That(content, Does.Contain("OUTCOME_OK"));
            Assert.That(content, Does.Contain("resultado: 2"));
            Assert.That(content, Does.Contain("**Fontes:**"));
            Assert.That(content, Does.Contain("[Site A](https://ex.com/a)"));
            Assert.That(content, Does.Contain("https://ex.com/b"));
            // dedupe: Site A aparece uma única vez.
            Assert.That(
                content.Split("https://ex.com/a").Length - 1, Is.EqualTo(1));
        });
    }

    /// <summary>Covers RF-002: grounding buffered também vira lista de
    /// fontes no content.</summary>
    [Test]
    public async Task Buffered_Grounding_FontesNoContent()
    {
        var http = new StubHttpHandler();
        http.Enqueue(HttpStatusCode.OK,
            "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":" +
            "[{\"text\":\"achei\"}]},\"finishReason\":\"STOP\"," +
            "\"groundingMetadata\":{\"groundingChunks\":[{\"web\":" +
            "{\"uri\":\"https://f.com\",\"title\":\"Fonte\"}}]}}]}");
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));

        var result = await NewService(http).CompleteWithToolsAsync(
            Req());

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Does.Contain("achei"));
            Assert.That(result.Content, Does.Contain("[Fonte](https://f.com)"));
        });
    }
}
