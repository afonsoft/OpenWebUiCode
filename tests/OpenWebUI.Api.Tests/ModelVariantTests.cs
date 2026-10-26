using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Api.Completions.Contributors;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.SystemPrompt;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261018-model-variants: resolução <c>model#variant</c>
/// (RF-001), variants sintéticas por capability (RF-002), bloco
/// compatibility + cacheFormat anthropic (RF-003) e aliases +
/// overlays de provider (RF-004).
/// </summary>
[TestFixture]
public class ModelVariantTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp() =>
        _dbPath = Path.Join(Path.GetTempPath(), $"owui-mvar-{Guid.NewGuid():N}.db");

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    // ---------- Split ----------

    [Test]
    public void Split_ModelComVariant_Separa()
    {
        var (baseRef, variant) = ModelVariantService.Split("claude-3#high");
        Assert.Multiple(() =>
        {
            Assert.That(baseRef, Is.EqualTo("claude-3"));
            Assert.That(variant, Is.EqualTo("high"));
        });
    }

    [Test]
    public void Split_SemVariant_ou_Invalida_DevolveNull()
    {
        Assert.That(ModelVariantService.Split("claude-3").VariantId, Is.Null);
        Assert.That(ModelVariantService.Split("claude-3#").VariantId, Is.Null);
        Assert.That(ModelVariantService.Split("claude-3#UP!").VariantId, Is.Null);
        Assert.That(ModelVariantService.Split("#solo").VariantId, Is.Null);
    }

    // ---------- RF-001: variants declaradas ----------

    [Test]
    public void Resolve_Declarada_DevolveSettingsHeadersBody()
    {
        const string meta = """
            {
              "variants": [
                { "id": "high", "settings": { "reasoning_effort": "high" },
                  "headers": { "x-team": "a" }, "body": { "top_k": 5 } },
                { "id": "low", "settings": { "reasoning_effort": "low" } }
              ],
              "compatibility": { "cacheFormat": "anthropic" }
            }
            """;

        var high = ModelVariantService.Resolve(meta, "high");
        var low = ModelVariantService.Resolve(meta, "low");

        Assert.Multiple(() =>
        {
            Assert.That(high, Is.Not.Null);
            Assert.That(high!.Settings?["reasoning_effort"]?.GetValue<string>(),
                Is.EqualTo("high"));
            Assert.That(high.Headers?["x-team"]?.GetValue<string>(), Is.EqualTo("a"));
            Assert.That(high.Body?["top_k"]?.GetValue<int>(), Is.EqualTo(5));
            Assert.That(high.CacheFormat, Is.EqualTo("anthropic"));
            Assert.That(low!.Settings?["reasoning_effort"]?.GetValue<string>(),
                Is.EqualTo("low"));
            Assert.That(low.Headers, Is.Null);
        });
    }

    [Test]
    public void Resolve_Inexistente_DevolveNull()
    {
        const string meta = """{"variants":[{"id":"high","settings":{}}]}""";
        Assert.That(ModelVariantService.Resolve(meta, "zzz"), Is.Null);
        Assert.That(ModelVariantService.Resolve(null, "zzz"), Is.Null);
        Assert.That(ModelVariantService.Resolve("lixo{", "zzz"), Is.Null);
    }

    // ---------- RF-002: variants sintéticas ----------

    [Test]
    public void Resolve_Sintetica_UsaReasoningEffort()
    {
        const string meta = """
            { "capabilities": { "reasoning": { "effort": ["low","medium","high"] } } }
            """;

        var v = ModelVariantService.Resolve(meta, "high");

        Assert.That(v, Is.Not.Null);
        Assert.That(v!.Settings?["reasoning_effort"]?.GetValue<string>(),
            Is.EqualTo("high"));
    }

    [Test]
    public void Resolve_Sintetica_RespeitaReasoningFieldCustom()
    {
        const string meta = """
            {
              "capabilities": { "reasoning": { "effort": ["low","high"] } },
              "compatibility": { "reasoningField": "thinking" }
            }
            """;

        var v = ModelVariantService.Resolve(meta, "high");

        Assert.That(v!.Settings?["thinking"]?.GetValue<string>(), Is.EqualTo("high"));
        Assert.That(v.ReasoningField, Is.EqualTo("thinking"));
    }

    [Test]
    public void Resolve_Declaradas_GanhamDaSintetica()
    {
        // Quando variants[] existe, effort[] NÃO gera sintéticas.
        const string meta = """
            {
              "variants": [{"id":"fast","settings":{}}],
              "capabilities": { "reasoning": { "effort": ["high"] } }
            }
            """;

        Assert.That(ModelVariantService.Resolve(meta, "high"), Is.Null);
        Assert.That(ModelVariantService.Resolve(meta, "fast"), Is.Not.Null);
    }

    // ---------- RF-003: compatibility ----------

    [Test]
    public void Resolve_AnthropicCacheFalse_DesligaCacheFormat()
    {
        const string meta = """
            {
              "variants": [{"id":"high","settings":{}}],
              "compatibility": { "cacheFormat": "anthropic", "anthropic_cache": false }
            }
            """;

        Assert.That(ModelVariantService.Resolve(meta, "high")!.CacheFormat, Is.Null);
    }

    [Test]
    public void ListVariantIds_DeclaradasESinteticas()
    {
        Assert.That(
            ModelVariantService.ListVariantIds(
                """{"variants":[{"id":"a"},{"id":"b"},{"id":"INV"}]}"""),
            Is.EqualTo(new[] { "a", "b" }));
        Assert.That(
            ModelVariantService.ListVariantIds(
                """{"capabilities":{"reasoning":{"effort":["low","high"]}}}"""),
            Is.EqualTo(new[] { "low", "high" }));
        Assert.That(ModelVariantService.ListVariantIds("{}"), Is.Empty);
        Assert.That(ModelVariantService.ListVariantIds(null), Is.Empty);
    }

    // ---------- RF-004: aliases ----------

    [Test]
    public void Aliases_MatchEListagem()
    {
        const string meta = """{"aliases":["gpt5","flagship"]}""";
        Assert.Multiple(() =>
        {
            Assert.That(ModelVariantService.MatchesAlias(meta, "gpt5"), Is.True);
            Assert.That(ModelVariantService.MatchesAlias(meta, "GPT5"), Is.True);
            Assert.That(ModelVariantService.MatchesAlias(meta, "outro"), Is.False);
            Assert.That(ModelVariantService.ListAliases(meta),
                Is.EqualTo(new[] { "gpt5", "flagship" }));
            Assert.That(ModelVariantService.ListAliases("{}"), Is.Empty);
        });
    }

    // ---------- DeepMerge ----------

    [Test]
    public void DeepMerge_Aninhado_MesclaSemSubstituir()
    {
        var target = JsonNode.Parse("""{"a":{"x":1,"y":2},"b":1}""")!.AsObject();
        var overlay = JsonNode.Parse("""{"a":{"y":9,"z":3},"c":4}""")!.AsObject();

        ModelVariantService.DeepMerge(target, overlay);

        Assert.Multiple(() =>
        {
            Assert.That(target["a"]!["x"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(target["a"]!["y"]!.GetValue<int>(), Is.EqualTo(9));
            Assert.That(target["a"]!["z"]!.GetValue<int>(), Is.EqualTo(3));
            Assert.That(target["b"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(target["c"]!.GetValue<int>(), Is.EqualTo(4));
        });
    }

    // ---------- Contributor: resolução end-to-end ----------

    private async Task<(PromptContext Ctx, CustomModelContributor C)> SeedAsync(
        string? metaJson, string? paramsJson = null, string? baseModelId = null)
    {
        var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U", Email = "u@c.local" });
        db.ModelEntries.Add(new ModelEntry
        {
            Id = "m1", UserId = "u1", Name = "Claude",
            BaseModelId = baseModelId, MetaJson = metaJson,
            ParamsJson = paramsJson, IsActive = true,
        });
        await db.SaveChangesAsync();
        var ctx = new PromptContext
        {
            Request = new ChatCompletionRequest(
                "m1", [new ChatCompletionMessage("user", "oi")], Stream: true),
            User = db.Users.Local.First(),
            LastUserText = "oi",
            Model = "m1",
            Ct = CancellationToken.None,
        };
        return (ctx, new CustomModelContributor(db));
    }

    [Test]
    public async Task Contributor_VariantDeclarada_AplicaSettings()
    {
        var (ctx, c) = await SeedAsync(
            """{"variants":[{"id":"high","settings":{"reasoning_effort":"high","temperature":0.9}}]}""",
            paramsJson: """{"temperature":0.5}""");
        ctx.Model = "m1#high";

        await c.ContributeAsync(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.Parameters!["reasoning_effort"].ToString(), Is.EqualTo("high"));
            // Variant vence params do modelo (TryAdd posterior perde).
            Assert.That(ctx.Parameters["temperature"].ToString(), Is.EqualTo("0.9"));
        });
    }

    [Test]
    public async Task Contributor_RequestParam_VenceVariant()
    {
        var (ctx, c) = await SeedAsync(
            """{"variants":[{"id":"high","settings":{"reasoning_effort":"high"}}]}""");
        ctx.Model = "m1#high";
        ctx.Parameters = new() { ["reasoning_effort"] = "low" };

        await c.ContributeAsync(ctx);

        Assert.That(ctx.Parameters["reasoning_effort"].ToString(), Is.EqualTo("low"));
    }

    [Test]
    public async Task Contributor_VariantDesconhecida_Lanca()
    {
        var (ctx, c) = await SeedAsync("""{"variants":[{"id":"high"}]}""");
        ctx.Model = "m1#zzz";

        var ex = await Assert.ThrowsAsync<ModelVariantException>(
            () => c.ContributeAsync(ctx));
        Assert.That(ex!.Message, Does.Contain("unknown variant"));
    }

    [Test]
    public async Task Contributor_SemBaseModelId_StripDoSufixo()
    {
        var (ctx, c) = await SeedAsync("""{"variants":[{"id":"high","settings":{}}]}""");
        ctx.Model = "m1#high";

        await c.ContributeAsync(ctx);

        Assert.That(ctx.Model, Is.EqualTo("m1"));
    }

    [Test]
    public async Task Contributor_ComBaseModelId_VaiParaBase()
    {
        var (ctx, c) = await SeedAsync(
            """{"variants":[{"id":"high","settings":{}}]}""", baseModelId: "claude-real");
        ctx.Model = "m1#high";

        await c.ContributeAsync(ctx);

        Assert.That(ctx.Model, Is.EqualTo("claude-real"));
    }

    [Test]
    public async Task Contributor_ModeloProviderSemEntry_StripSufixo()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U", Email = "u@c.local" });
        await db.SaveChangesAsync();
        var ctx = new PromptContext
        {
            Request = new ChatCompletionRequest(
                "gpt#high", [new ChatCompletionMessage("user", "oi")], Stream: true),
            User = db.Users.Local.First(),
            LastUserText = "oi",
            Model = "gpt#high",
            Ct = CancellationToken.None,
        };

        await new CustomModelContributor(db).ContributeAsync(ctx);

        Assert.That(ctx.Model, Is.EqualTo("gpt"));
    }

    [Test]
    public async Task Contributor_Alias_ResolveEntryReal()
    {
        var (ctx, c) = await SeedAsync(
            """{"aliases":["flagship"],"variants":[{"id":"v","settings":{}}]}""");
        ctx.Model = "flagship#v";

        await c.ContributeAsync(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.CustomModel?.Id, Is.EqualTo("m1"));
            Assert.That(ctx.Model, Is.EqualTo("m1"));
        });
    }

    [Test]
    public async Task Contributor_VariantHeadersBody_ChegamAoContexto()
    {
        var (ctx, c) = await SeedAsync("""
            {
              "variants": [{"id":"v","settings":{},
                "headers":{"x-team":"a"},"body":{"top_k":5}}],
              "compatibility": {"cacheFormat":"anthropic"}
            }
            """);
        ctx.Model = "m1#v";

        await c.ContributeAsync(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(ctx.ExtraHeaders?["x-team"], Is.EqualTo("a"));
            Assert.That(ctx.ExtraBody?["top_k"]?.GetValue<int>(), Is.EqualTo(5));
            Assert.That(ctx.CacheFormat, Is.EqualTo("anthropic"));
        });
    }

    // ---------- Provider: overlays + cache format ----------

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(ct);
            var json = request.RequestUri!.AbsolutePath.Contains("/v1/messages")
                ? """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}"""
                : """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private async Task<(ProviderService Svc, CaptureHandler H)> NewProvidersAsync(
        ConnectionsConfig connections)
    {
        var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("connections", connections);
        var handler = new CaptureHandler();
        return (new ProviderService(
            new StubFactory(handler), config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build()), handler);
    }

    private static ChatCompletionRequest Req(string model, string connection) =>
        new(model,
            [new ChatCompletionMessage("system", "sys"),
             new ChatCompletionMessage("user", "oi")],
            Stream: false, Connection: connection);

    [Test]
    public async Task ProviderOverlay_HeadersBodySettings_Aplicados()
    {
        var conn = new ProviderConnection(
            "openai", "http://unused.test", "k",
            Headers: new JsonObject { ["x-team"] = "a", ["Authorization"] = "evil" },
            Body: new JsonObject { ["store"] = true, ["nested"] = new JsonObject { ["p"] = 1 } },
            Settings: new JsonObject { ["temperature"] = 0.1, ["max_tokens"] = 7 });
        var (svc, h) = await NewProvidersAsync(new ConnectionsConfig(
            [], ["http://openai.test"], ["key"], Providers: [conn]));

        await svc.CompleteAsync(Req("gpt", "openai"), ct: CancellationToken.None);

        var body = JsonNode.Parse(h.LastBody!)!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That(h.Last!.Headers.GetValues("x-team"), Is.EqualTo(new[] { "a" }));
            Assert.That(h.Last.Headers.Authorization?.Parameter, Is.EqualTo("key"),
                "Authorization do canal nunca é sobrescrito");
            Assert.That(body["store"]!.GetValue<bool>(), Is.True);
            Assert.That(body["nested"]!["p"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(body["temperature"]!.GetValue<double>(), Is.EqualTo(0.1));
            Assert.That(body["max_tokens"]!.GetValue<int>(), Is.EqualTo(7));
        });
    }

    [Test]
    public async Task ProviderOverlay_RequestParams_VencemSettings()
    {
        var conn = new ProviderConnection(
            "openai", "http://unused.test", "k",
            Settings: new JsonObject { ["temperature"] = 0.1 });
        var (svc, h) = await NewProvidersAsync(new ConnectionsConfig(
            [], ["http://openai.test"], ["key"], Providers: [conn]));

        await svc.CompleteAsync(
            Req("gpt", "openai") with
            {
                Params = new Dictionary<string, object> { ["temperature"] = 0.7 },
            }, ct: CancellationToken.None);

        var body = JsonNode.Parse(h.LastBody!)!.AsObject();
        Assert.That(body["temperature"]!.GetValue<double>(), Is.EqualTo(0.7));
    }

    [Test]
    public async Task ProviderOverlay_VariantBody_VenceProviderBody()
    {
        var conn = new ProviderConnection(
            "openai", "http://unused.test", "k",
            Body: new JsonObject { ["nested"] = new JsonObject { ["p"] = 1, ["q"] = 2 } });
        var (svc, h) = await NewProvidersAsync(new ConnectionsConfig(
            [], ["http://openai.test"], ["key"], Providers: [conn]));

        await svc.CompleteAsync(
            Req("gpt", "openai") with
            {
                ExtraBody = new JsonObject { ["nested"] = new JsonObject { ["q"] = 9 } },
            }, ct: CancellationToken.None);

        var body = JsonNode.Parse(h.LastBody!)!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That(body["nested"]!["p"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(body["nested"]!["q"]!.GetValue<int>(), Is.EqualTo(9));
        });
    }

    // ---------- RF-003: cacheFormat anthropic ----------

    private static ConnectionsConfig AnthropicConns(string baseUrl) =>
        new([], [], [], Providers:
        [
            new ProviderConnection("anthropic", baseUrl, "k"),
        ]);

    [Test]
    public async Task CacheFormat_Anthropic_InjetaCacheControl()
    {
        var (svc, h) = await NewProvidersAsync(AnthropicConns("http://as.test"));
        var tool = JsonDocument.Parse(
            """{"type":"function","function":{"name":"f","description":"d","parameters":{"type":"object"}}}""")
            .RootElement;

        await svc.CompleteAsync(
            Req("claude", "anthropic") with
            {
                CacheFormat = "anthropic",
                Messages =
                [
                    new ChatCompletionMessage("system", "sys"),
                    new ChatCompletionMessage("user", "o1"),
                    new ChatCompletionMessage("user", "o2"),
                ],
                Tools = [tool],
            }, ct: CancellationToken.None);

        var body = JsonNode.Parse(h.LastBody!)!.AsObject();
        var system = body["system"]!.AsArray();
        var tools = body["tools"]!.AsArray();
        var users = body["messages"]!.AsArray()
            .Where(m => m!["role"]!.GetValue<string>() == "user").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(
                system[0]!["cache_control"]!["type"]!.GetValue<string>(),
                Is.EqualTo("ephemeral"));
            Assert.That(
                tools[^1]!["cache_control"]!["type"]!.GetValue<string>(),
                Is.EqualTo("ephemeral"));
            var last = users[^1]!["content"]!;
            var lastText = last is JsonArray arr
                ? arr.Last(b => b!["type"]!.GetValue<string>() == "text")
                : null;
            Assert.That(
                (lastText ?? last)!["cache_control"]!["type"]!.GetValue<string>(),
                Is.EqualTo("ephemeral"));
            // Mensagens user anteriores não recebem marcador.
            Assert.That(users[0]!["content"]!.GetValue<string>(), Is.EqualTo("o1"));
        });
    }

    [Test]
    public async Task CacheFormat_Omniroute_NaoInjeta()
    {
        var (svc, h) = await NewProvidersAsync(
            AnthropicConns("http://omniroute.internal"));

        await svc.CompleteAsync(
            Req("claude", "anthropic") with { CacheFormat = "anthropic" },
            ct: CancellationToken.None);

        var body = JsonNode.Parse(h.LastBody!)!.AsObject();
        Assert.That(body["system"]!.GetValue<string>(), Is.EqualTo("sys"),
            "system permanece string simples quando OmniRoute gerencia o cache");
    }
}

/// <summary>
/// RF-001 end-to-end: <c>model#variant</c> inexistente no chat
/// completions → HTTP 400 com a mensagem da exceção.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ModelVariantEndpointTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"owui-mvarep-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    [Test]
    public async Task ChatCompletions_VariantDesconhecida_400()
    {
        var signup = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup",
            new SignUpRequest("Admin", "mv@e.local", "senha123"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await signup.Content.ReadAsStringAsync());
        var auth = (await signup.Content.ReadFromJsonAsync<AuthResponse>())!;
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);

        // Entry sem variants — qualquer #id é desconhecida.
        var created = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest(
                "Modelo X", "claude-real", null, null, null,
                MetaJson: """{"variants":[{"id":"high","settings":{}}]}"""));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var entry = (await created.Content
            .ReadFromJsonAsync<ModelEntryResponse>())!;

        var resp = await _client.PostAsJsonAsync("/api/chat/completions",
            new ChatCompletionRequest(
                $"{entry.Id}#zzz",
                [new ChatCompletionMessage("user", "oi")], Stream: true));

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var sse = await resp.Content.ReadAsStringAsync();
        Assert.That(sse, Does.Contain("unknown variant"));
    }
}
