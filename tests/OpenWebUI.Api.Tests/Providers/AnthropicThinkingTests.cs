using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests.Providers;

/// <summary>
/// SPEC-20261103-anthropic-thinking-config: knob first-class
/// <c>thinking_budget</c> → <c>thinking:{type:"enabled",budget_tokens}</c>
/// no payload da Messages API (RF-001), clamps de budget/max_tokens
/// (RF-002) e beta header opt-in via <c>connection.Headers</c> (RF-003 —
/// merge de headers da conexão já existente).
/// </summary>
[TestFixture, IsolateEnvironment]
public class AnthropicThinkingTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-ath-{Guid.NewGuid():N}.db");
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
        Dictionary<string, object>? extraParams = null) =>
        new("claude-sonnet-4", [new ChatCompletionMessage("user", "oi")],
            Stream: false, Connection: "anthropic", Params: extraParams);

    private async Task<JsonNode> CaptureAnthropicPayloadAsync(
        StubHttpHandler http, ChatCompletionRequest req)
    {
        http.Enqueue(System.Net.HttpStatusCode.OK,
            "{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\"," +
            "\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]," +
            "\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":1," +
            "\"output_tokens\":1}}");
        await NewService(http).CompleteAsync(req);
        return JsonNode.Parse(http.Requests[^1].Body)!;
    }

    /// <summary>Covers RF-001: thinking_budget=4096 vira
    /// thinking:{enabled,4096} no payload.</summary>
    [Test]
    public async Task ThinkingBudget4096_ViraThinkingNoPayload()
    {
        var http = new StubHttpHandler();
        var body = await CaptureAnthropicPayloadAsync(http,
            Req(new() { ["thinking_budget"] = 4096 }));

        Assert.Multiple(() =>
        {
            Assert.That(body["thinking"]?["type"]?.GetValue<string>(),
                Is.EqualTo("enabled"));
            Assert.That(body["thinking"]?["budget_tokens"]?.GetValue<int>(),
                Is.EqualTo(4096));
        });
    }

    /// <summary>Covers RF-002: budget abaixo do mínimo da API clampa em
    /// 1024.</summary>
    [Test]
    public async Task ThinkingBudget512_ClampMinimo1024()
    {
        var http = new StubHttpHandler();
        var body = await CaptureAnthropicPayloadAsync(http,
            Req(new() { ["thinking_budget"] = 512 }));

        Assert.That(body["thinking"]?["budget_tokens"]?.GetValue<int>(),
            Is.EqualTo(1024));
    }

    /// <summary>Covers RF-002: budget acima do max_tokens default (4096)
    /// eleva max_tokens para budget+1024.</summary>
    [Test]
    public async Task ThinkingBudget8192_ElevaMaxTokens()
    {
        var http = new StubHttpHandler();
        var body = await CaptureAnthropicPayloadAsync(http,
            Req(new() { ["thinking_budget"] = 8192 }));

        Assert.Multiple(() =>
        {
            Assert.That(body["thinking"]?["budget_tokens"]?.GetValue<int>(),
                Is.EqualTo(8192));
            Assert.That(body["max_tokens"]?.GetValue<int>(),
                Is.EqualTo(9216));
        });
    }

    /// <summary>Covers RF-002: max_tokens explícito acima do budget não é
    /// tocado.</summary>
    [Test]
    public async Task ThinkingBudget_MaxExplicitoVence()
    {
        var http = new StubHttpHandler();
        var body = await CaptureAnthropicPayloadAsync(http,
            Req(new()
            {
                ["thinking_budget"] = 4096,
                ["max_tokens"] = 64000,
            }));

        Assert.Multiple(() =>
        {
            Assert.That(body["thinking"]?["budget_tokens"]?.GetValue<int>(),
                Is.EqualTo(4096));
            Assert.That(body["max_tokens"]?.GetValue<int>(),
                Is.EqualTo(64000));
        });
    }

    /// <summary>Covers RF-001: thinking_budget<=0 desliga explicitamente —
    /// payload sem chave thinking.</summary>
    [Test]
    public async Task ThinkingBudgetZero_NaoEmite()
    {
        var http = new StubHttpHandler();
        var body = await CaptureAnthropicPayloadAsync(http,
            Req(new() { ["thinking_budget"] = 0 }));

        Assert.That(body["thinking"], Is.Null);
    }

    /// <summary>Covers RF-003: anthropic-beta em connection.Headers chega
    /// ao wire (opt-in já suportado pelo merge de headers — sem código
    /// novo).</summary>
    [Test]
    public async Task BetaHeader_ConexaoPropagaAoWire()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(new ProviderConnection(
            "anthropic", "https://ant.test", "k",
            Headers: JsonNode.Parse(
                "{\"anthropic-beta\":\"interleaved-thinking-2025-05-14\"}")!
                .AsObject()));

        _ = await CaptureAnthropicPayloadAsync(http, Req());

        Assert.That(
            http.Requests[^1].Headers["anthropic-beta"],
            Is.EqualTo("interleaved-thinking-2025-05-14"));
    }
}
