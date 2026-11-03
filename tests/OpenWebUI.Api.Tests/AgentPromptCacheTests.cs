using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Api.Completions;
using OpenWebUI.Api.Completions.Contributors;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.SystemPrompt;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261103-agent-prompt-cache: split estático/dinâmico do system
/// prompt (RF-001), cache_control no bloco estático (RF-002), prefixo
/// determinístico (RF-003) e flag <c>prompt_cache:enabled</c> (RF-005).
/// </summary>
[TestFixture]
public class AgentPromptCacheTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"owui-apc-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        _db.Database.EnsureCreated();
        _config = new ConfigService(_db, TestCache.Create());
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private sealed class StubContributor(
        int order, string content,
        PromptCacheTier tier = PromptCacheTier.Static) : ISystemPromptContributor
    {
        public int Order => order;
        public PromptCacheTier CacheTier => tier;
        public Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx) =>
            Task.FromResult<IReadOnlyList<string>>([content]);
    }

    private static readonly User Usuario = new() { Id = "u1", Name = "U", Email = "u@x" };

    private ChatPipeline.EnrichDeps Deps(params ISystemPromptContributor[] c) =>
        new(_db, _config, null!, null!, null!, c);

    // Covers RF-001: split produz duas mensagens system — estático primeiro.
    [Test]
    public async Task Split_StaticAntesDoDinamico()
    {
        var request = new ChatCompletionRequest(
            "fake:1", [new ChatCompletionMessage("user", "oi")]);
        var effective = await ChatPipeline.EnrichRequestAsync(
            request, Usuario,
            Deps(new StubContributor(0, "STATIC"),
                 new StubContributor(1, "DYNAMIC", PromptCacheTier.Dynamic)),
            default);

        var systems = effective.Messages.Where(m => m.Role == "system").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(systems, Has.Count.EqualTo(2));
            Assert.That(systems[0].Content, Is.EqualTo("STATIC"));
            Assert.That(systems[1].Content, Is.EqualTo("DYNAMIC"));
        });
    }

    // Covers RF-001: system do request vai ao bloco dinâmico (não invalida o prefixo).
    [Test]
    public async Task Split_RequestSystem_NoBlocoDinamico()
    {
        var request = new ChatCompletionRequest(
            "fake:1",
            [new ChatCompletionMessage("system", "REQ-SYS"),
             new ChatCompletionMessage("user", "oi")]);
        var effective = await ChatPipeline.EnrichRequestAsync(
            request, Usuario,
            Deps(new StubContributor(0, "STATIC"),
                 new StubContributor(1, "DYNAMIC", PromptCacheTier.Dynamic)),
            default);

        var systems = effective.Messages.Where(m => m.Role == "system").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(systems, Has.Count.EqualTo(2));
            Assert.That(systems[0].Content, Is.EqualTo("STATIC"));
            Assert.That(systems[1].Content, Does.Contain("REQ-SYS")
                .And.Contain("DYNAMIC"));
        });
    }

    // Covers RF-003: o bloco estático é prefixo determinístico entre builds.
    [Test]
    public async Task PrefixoEstatico_Deterministico()
    {
        ChatCompletionRequest Req() => new(
            "fake:1", [new ChatCompletionMessage("user", "oi")]);
        ISystemPromptContributor[] c =
        [
            new StubContributor(0, "STATIC-A"),
            new StubContributor(1, "STATIC-B"),
            new StubContributor(2, "DYN", PromptCacheTier.Dynamic),
        ];
        var a = await ChatPipeline.EnrichRequestAsync(Req(), Usuario, Deps(c), default);
        var b = await ChatPipeline.EnrichRequestAsync(Req(), Usuario, Deps(c), default);

        var sa = a.Messages.First(m => m.Role == "system").Content;
        var sb = b.Messages.First(m => m.Role == "system").Content;
        Assert.Multiple(() =>
        {
            Assert.That(sa, Is.EqualTo(sb));
            Assert.That(sa, Does.Contain("STATIC-A").And.Contain("STATIC-B"));
        });
    }

    // Covers RF-006: contribuidor dinâmico (ex.: <context_usage>) fica fora do estático.
    [Test]
    public async Task ContextUsage_ForaDoBlocoEstatico()
    {
        var request = new ChatCompletionRequest(
            "fake:1", [new ChatCompletionMessage("user", "oi")]);
        var effective = await ChatPipeline.EnrichRequestAsync(
            request, Usuario,
            Deps(new StubContributor(0, "STATIC"),
                 new StubContributor(99, "<context_usage>1k</context_usage>",
                     PromptCacheTier.Dynamic)),
            default);

        var systems = effective.Messages.Where(m => m.Role == "system").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(systems[0].Content, Does.Not.Contain("context_usage"));
            Assert.That(systems[^1].Content, Does.Contain("context_usage"));
        });
    }

    // Covers RF-005: prompt_cache:enabled=false zera o CacheFormat.
    [Test]
    public async Task FlagOff_CacheFormatNull()
    {
        await _config.SetAsync("prompt_cache:enabled", false);
        var request = new ChatCompletionRequest(
            "fake:1", [new ChatCompletionMessage("user", "oi")],
            Stream: false, CacheFormat: "anthropic");
        var effective = await ChatPipeline.EnrichRequestAsync(
            request, Usuario, Deps(new StubContributor(0, "S")), default);
        Assert.That(effective.CacheFormat, Is.Null);
    }

    // Covers RF-001: sem contribuidores o prompt base vai ao bloco
    // estático (fallback headless — nunca some).
    [Test]
    public async Task Split_SemContribuidores_PromptBaseNoEstatico()
    {
        var request = new ChatCompletionRequest(
            "fake:1", [new ChatCompletionMessage("user", "oi")]);
        var effective = await ChatPipeline.EnrichRequestAsync(
            request, Usuario, Deps(), default);

        var systems = effective.Messages.Where(m => m.Role == "system").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(systems, Has.Count.EqualTo(1));
            Assert.That(systems[0].Content, Is.Not.Null.And.Not.Empty);
        });
    }

    // Covers RF-001: os contributors por-request reais declaram tier
    // Dynamic — regressão contra tier errado num contribuidor ruidoso.
    [Test]
    public void CacheTier_ContribuidoresPorRequest_SaoDinamicos()
    {
        // Deps null! são seguras: só a propriedade de metadado é lida.
        ISystemPromptContributor[] dinamicos =
        [
            new WebSearchContributor(null!),
            new AgentMemoryContributor(_db, _config),
            new MentionsContributor(),
            new GenContextContributor(_db),
        ];
        foreach (var c in dinamicos)
        {
            Assert.That(c.CacheTier, Is.EqualTo(PromptCacheTier.Dynamic),
                c.GetType().Name);
        }
    }

    // ---------- provider: cache_control por bloco ----------

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null
                ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h);
    }

    private async Task<(ProviderService Svc, CaptureHandler H)> NewAnthropicAsync()
    {
        await _config.SetAsync("connections", new ConnectionsConfig(
            [], [], [], Providers:
            [new ProviderConnection("anthropic", "http://as.test", "k")]));
        var h = new CaptureHandler();
        return (new ProviderService(new StubFactory(h), _config,
            NullLogger<ProviderService>.Instance, TestCache.Create(),
            new ConfigurationBuilder().Build()), h);
    }

    // Covers RF-002: dois blocos system → só o estático (bloco 0) recebe cache_control.
    [Test]
    public async Task Anthropic_SoBlocoEstatico_Cacheado()
    {
        var (svc, h) = await NewAnthropicAsync();
        await svc.CompleteAsync(
            new ChatCompletionRequest("claude",
                [new ChatCompletionMessage("system", "STATIC"),
                 new ChatCompletionMessage("system", "DYNAMIC"),
                 new ChatCompletionMessage("user", "oi")],
                Stream: false, Connection: "anthropic", CacheFormat: "anthropic"),
            ct: CancellationToken.None);

        var system = JsonNode.Parse(h.LastBody!)!["system"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(system, Has.Count.EqualTo(2));
            Assert.That(system[0]!["cache_control"]!["type"]!.GetValue<string>(),
                Is.EqualTo("ephemeral"));
            Assert.That(system[0]!["text"]!.GetValue<string>(), Is.EqualTo("STATIC"));
            Assert.That(system[1]!["text"]!.GetValue<string>(), Is.EqualTo("DYNAMIC"));
            Assert.That(system[1]!["cache_control"], Is.Null);
        });
    }

    // Covers RF-002 (regressão): um único bloco system → marcado como antes.
    [Test]
    public async Task Anthropic_BlocoUnico_Marcado()
    {
        var (svc, h) = await NewAnthropicAsync();
        await svc.CompleteAsync(
            new ChatCompletionRequest("claude",
                [new ChatCompletionMessage("system", "STATIC"),
                 new ChatCompletionMessage("user", "oi")],
                Stream: false, Connection: "anthropic", CacheFormat: "anthropic"),
            ct: CancellationToken.None);

        var system = JsonNode.Parse(h.LastBody!)!["system"]!.AsArray();
        Assert.That(system[0]!["cache_control"]!["type"]!.GetValue<string>(),
            Is.EqualTo("ephemeral"));
    }
}
