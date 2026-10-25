using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261018-agent-polish: cobertura dos quatro requisitos —
/// RF-001 warming opt-in (ping por modelo, para após ociosidade, gates
/// billable/OmniRoute), RF-002 catálogo remoto de skills (index+SKILL.md,
/// ETag, TTL 24h, precedência local), RF-003 MCP resources/prompts
/// (resources/list+read materializando read_resource; prompts/list+get
/// virando slash <c>{server}:{prompt}</c>) e RF-004 /compact manual com
/// fallback nativo→summarizer.
/// </summary>
[TestFixture, NonParallelizable]
public class AgentPolishTests
{
    private string _dbPath = null!;
    private string _root = null!;
    private FakeMcpServer _mcp = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _mcp = new FakeMcpServer();
        _mcp.Start();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _mcp.Dispose();

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-polish-{Guid.NewGuid():N}.db");
        _root = Path.Join(Path.GetTempPath(), $"polish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        ModelWarmer.Now = () => DateTimeOffset.UtcNow;
        ModelWarmer.ResetState();
        RemoteSkillCatalog.Now = () => DateTimeOffset.UtcNow;
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // cleanup best-effort.
        }
    }

    private AppDbContext Db() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private async Task<ConfigService> NewConfigAsync()
    {
        var db = Db();
        db.Database.EnsureCreated();
        return new ConfigService(db, TestCache.Create());
    }

    // ---------- RF-001: ModelWarmer ----------

    private sealed class ProviderHandler : HttpMessageHandler
    {
        public int TagsCalls;
        public int ChatCalls;
        public int CompletionsCalls;
        public bool FailCompletions;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("/api/tags", StringComparison.Ordinal))
            {
                TagsCalls++;
                json = """{"models":[{"name":"m-warm"},{"name":"m-warm2"}]}""";
            }
            else if (path.EndsWith("/models", StringComparison.Ordinal))
            {
                TagsCalls++;
                json = """{"data":[{"id":"m-warm"},{"id":"m-warm2"}]}""";
            }
            else if (path.EndsWith("/api/chat", StringComparison.Ordinal))
            {
                ChatCalls++;
                if (FailCompletions)
                {
                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }
                json = """{"model":"m-warm","message":{"role":"assistant","content":"ok"},"done":true}""";
            }
            else
            {
                CompletionsCalls++;
                if (FailCompletions)
                {
                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.InternalServerError));
                }
                json = """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private async Task<(ModelWarmer Warmer, ProviderHandler Handler)> NewWarmerAsync(
        IReadOnlyList<string> ollama, IReadOnlyList<string> openAi,
        string modelId, bool billable)
    {
        var db = Db();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("connections",
            new ConnectionsConfig(ollama, openAi, openAi.Select(_ => "k").ToList()));
        await config.SetAsync(ModelWarmer.ConfigKey,
            new Dictionary<string, ModelWarmer.WarmingEntry>
            {
                [modelId] = new(IntervalMinutes: 5, Billable: billable),
            });
        var handler = new ProviderHandler();
        var providers = new ProviderService(
            new StubFactory(handler), config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());
        return (new ModelWarmer(db, config, providers), handler);
    }

    private async Task SeedRunAsync(string model, long startedAt)
    {
        await using var db = Db();
        db.Database.EnsureCreated();
        await SeedUserAsync(db);
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        db.ChatRuns.Add(new ChatRun
        {
            ChatId = "c1", UserId = "u1", Model = model,
            Status = "done", StartedAt = startedAt,
            CreatedAt = startedAt,
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Warming_ComUsoRecente_PingaERespeitaIntervalo()
    {
        var (warmer, handler) = await NewWarmerAsync(
            ["http://ollama.test"], [], "m-warm", billable: false);
        var now = DateTimeOffset.UtcNow;
        await SeedRunAsync("m-warm", now.ToUnixTimeSeconds() - 60);

        Assert.That(await warmer.RunOnceAsync(default), Is.EqualTo(1));
        Assert.That(handler.ChatCalls, Is.EqualTo(1));

        // Dentro do intervalo — nada novo.
        Assert.That(await warmer.RunOnceAsync(default), Is.EqualTo(0));
        Assert.That(handler.ChatCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task Warming_SemUsoRecente_ParaAposOciosidade()
    {
        var (warmer, handler) = await NewWarmerAsync(
            ["http://ollama.test"], [], "m-warm", billable: false);
        var now = DateTimeOffset.UtcNow;
        // Último uso há 20min — fora da janela de 3×5min.
        await SeedRunAsync("m-warm", now.ToUnixTimeSeconds() - 1200);

        Assert.That(await warmer.RunOnceAsync(default), Is.EqualTo(0));
        Assert.That(handler.ChatCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task Warming_ProviderPagoSemBillable_NaoPinga()
    {
        var (warmer, handler) = await NewWarmerAsync(
            [], ["https://api.openai.test"], "m-warm", billable: false);
        var now = DateTimeOffset.UtcNow;
        await SeedRunAsync("m-warm", now.ToUnixTimeSeconds() - 60);

        Assert.That(await warmer.RunOnceAsync(default), Is.EqualTo(0));
        Assert.That(handler.CompletionsCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task Warming_ProviderPagoComBillable_Pinga()
    {
        var (warmer, handler) = await NewWarmerAsync(
            [], ["https://api.openai.test"], "m-warm", billable: true);
        var now = DateTimeOffset.UtcNow;
        await SeedRunAsync("m-warm", now.ToUnixTimeSeconds() - 60);

        Assert.That(await warmer.RunOnceAsync(default), Is.EqualTo(1));
        Assert.That(handler.CompletionsCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task Warming_OmnirouteEhPulado()
    {
        var (warmer, handler) = await NewWarmerAsync(
            [], ["https://omniroute.internal"], "m-warm", billable: true);
        var now = DateTimeOffset.UtcNow;
        await SeedRunAsync("m-warm", now.ToUnixTimeSeconds() - 60);

        Assert.That(await warmer.RunOnceAsync(default), Is.EqualTo(0));
        Assert.That(handler.CompletionsCalls, Is.EqualTo(0));
    }

    // ---------- RF-002: RemoteSkillCatalog ----------

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public int IndexCalls;
        public int SkillCalls;
        public string? LastIfNoneMatch;
        public HttpStatusCode IndexStatus = HttpStatusCode.OK;
        public string IndexBody =
            """{"skills":[{"name":"alpha","description":"Skill remota"}]}""";
        public HttpStatusCode SkillStatus = HttpStatusCode.OK;
        public string? SkillBody;
        public bool SkillSemContentLength;
        public bool Throw;
        private const string ETag = "\"v1\"";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw)
            {
                throw new HttpRequestException("catálogo fora do ar");
            }
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/index.json", StringComparison.Ordinal))
            {
                IndexCalls++;
                return Task.FromResult(new HttpResponseMessage(IndexStatus)
                {
                    Content = new StringContent(
                        IndexBody, Encoding.UTF8, "application/json"),
                });
            }
            SkillCalls++;
            LastIfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.Tag;
            if (LastIfNoneMatch == ETag)
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.NotModified));
            }
            if (SkillStatus != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(SkillStatus));
            }
            var body = SkillBody ?? """
                ---
                name: alpha
                description: Skill remota
                autoinvoke: true
                ---
                Corpo remoto da alpha.
                """;
            var response = Json(body);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(ETag);
            if (SkillSemContentLength)
            {
                response.Content.Headers.ContentLength = null;
            }
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/markdown"),
            };
    }

    private RemoteSkillCatalog NewCatalog(CatalogHandler handler) =>
        new(new StubFactory(handler));

    private async Task SeedRemoteRootAsync(ConfigService config, string root)
    {
        await config.SetAsync(RemoteSkillCatalog.ConfigKey, new List<string> { root });
    }

    [Test]
    public async Task Remote_IndexParse_SkillComAutoinvoke()
    {
        var handler = new CatalogHandler();
        var catalog = NewCatalog(handler);
        var config = await NewConfigAsync();
        await SeedRemoteRootAsync(config, "https://skills.test");

        Assert.That(await catalog.RefreshIfStaleAsync(config, default), Is.EqualTo(1));
        var entries = catalog.Entries;
        Assert.Multiple(() =>
        {
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Name, Is.EqualTo("alpha"));
            Assert.That(entries[0].Scope, Is.EqualTo("remote"));
            Assert.That(entries[0].Autoinvoke, Is.True);
            Assert.That(entries[0].Body, Does.Contain("Corpo remoto"));
        });
    }

    [Test]
    public async Task Remote_Ttl24h_SoRebuscaDepois()
    {
        var handler = new CatalogHandler();
        var catalog = NewCatalog(handler);
        var config = await NewConfigAsync();
        await SeedRemoteRootAsync(config, "https://skills.test");

        await catalog.RefreshIfStaleAsync(config, default);
        Assert.That(handler.IndexCalls, Is.EqualTo(1));

        // Dentro do TTL — sem novo fetch.
        await catalog.RefreshIfStaleAsync(config, default);
        Assert.That(handler.IndexCalls, Is.EqualTo(1));

        // Após 24h — rebusca (e o SKILL.md via ETag 304 reusa o corpo).
        var start = DateTimeOffset.UtcNow;
        RemoteSkillCatalog.Now = () => start.AddHours(25);
        await catalog.RefreshIfStaleAsync(config, default);
        Assert.That(handler.IndexCalls, Is.EqualTo(2));
        Assert.That(handler.LastIfNoneMatch, Is.EqualTo("\"v1\""));
        Assert.That(catalog.Entries[0].Body, Does.Contain("Corpo remoto"));
    }

    [Test]
    public async Task Remote_LocalVenceSobreRemoto()
    {
        var handler = new CatalogHandler();
        var catalog = NewCatalog(handler);
        var config = await NewConfigAsync();
        await SeedRemoteRootAsync(config, "https://skills.test");
        await catalog.RefreshIfStaleAsync(config, default);

        // Mesmo nome no projeto — a entrada local tem precedência.
        var dir = Path.Join(_root, ".agents", "skills", "alpha");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Join(dir, "SKILL.md"),
            "---\nname: alpha\ndescription: local\n---\nCorpo LOCAL.");
        var scan = new SkillDiscoveryService(catalog).Scan(_root);
        var alpha = scan.Skills.Single(s => s.Name == "alpha");
        Assert.Multiple(() =>
        {
            Assert.That(alpha.Scope, Is.EqualTo("project"));
            Assert.That(alpha.Body, Does.Contain("Corpo LOCAL"));
        });
    }

    [Test]
    public async Task Remote_RaizInvalidaOuNaoHttps_EhIgnorada()
    {
        var handler = new CatalogHandler();
        var catalog = NewCatalog(handler);
        var config = await NewConfigAsync();
        // http:// não é https — ignorada; URL quebrada idem.
        await config.SetAsync(RemoteSkillCatalog.ConfigKey,
            new List<string> { "http://inseguro.test", "not-a-url" });
        Assert.That(await catalog.RefreshIfStaleAsync(config, default), Is.EqualTo(0));
        Assert.That(handler.IndexCalls, Is.EqualTo(0));
    }

    // ---------- RF-003: MCP resources/prompts ----------

    private McpServer HttpServer() => new()
    {
        Name = "fake", Transport = "http", Url = _mcp.Url, Enabled = true,
        HeadersJson = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["X-Token"] = "segredo" }),
    };

    private McpClientService NewMcpDb(AppDbContext db) =>
        new(db, TestCache.Create());

    [Test]
    public async Task Mcp_Resources_MaterializamReadResource()
    {
        await using var db = Db();
        db.Database.EnsureCreated();
        var server = HttpServer();
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        var mcp = NewMcpDb(db);

        await mcp.RefreshToolsAsync(server, default);
        var tool = db.Tools.SingleOrDefault(
            t => t.Url == $"mcp://{server.Id}/read_resource");
        Assert.That(tool, Is.Not.Null);
        Assert.That(tool!.SpecJson, Does.Contain("mcp_fake_read_resource"));
    }

    [Test]
    public async Task Mcp_ReadResource_DevolveTexto()
    {
        await using var db = Db();
        db.Database.EnsureCreated();
        var server = HttpServer();
        var mcp = NewMcpDb(db);

        var text = await mcp.CallToolAsync(
            server, "read_resource", """{"uri":"memo://alpha"}""", default);
        Assert.That(text, Does.Contain("conteudo-do-resource"));
    }

    [Test]
    public async Task Mcp_Prompts_ListaEResolve()
    {
        await using var db = Db();
        db.Database.EnsureCreated();
        var server = HttpServer();
        var mcp = NewMcpDb(db);

        var prompts = await mcp.ListPromptsAsync(server, default);
        Assert.Multiple(() =>
        {
            Assert.That(prompts, Has.Count.EqualTo(1));
            Assert.That(prompts[0].Name, Is.EqualTo("revisao"));
            Assert.That(prompts[0].Arguments, Is.EqualTo(new[] { "arquivo" }));
        });

        var text = await mcp.GetPromptAsync(server, "revisao",
            new Dictionary<string, object?> { ["arquivo"] = "a.cs" }, default);
        Assert.That(text, Is.EqualTo("revise:a.cs"));
    }

    [Test]
    public async Task Mcp_SlashCommand_ExpandePromptDoServer()
    {
        await using var db = Db();
        db.Database.EnsureCreated();
        var server = HttpServer();
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var config = new ConfigService(db, TestCache.Create());
        var providers = new ProviderService(
            new StubFactory(new ProviderHandler()), config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());
        var agents = new AgentDefinitionService(
            new StubEnv(_root), config, NullLogger<AgentDefinitionService>.Instance);
        var svc = new RepoCommandService(
            new SkillDiscoveryService(), agents, providers,
            new ConfigurationBuilder().Build(),
            NewMcpDb(db), db);

        var (result, error) = await svc.ExpandAsync(
            "u1", null, "fake:revisao", "a.cs", default);
        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Null);
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Prompt, Is.EqualTo("revise:a.cs"));
            Assert.That(result.Entry.Scope, Is.EqualTo("mcp"));
        });
    }

    private sealed class StubEnv(string root)
        : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(root);
    }

    // ---------- RF-004: /compact manual + nativo ----------

    private sealed class StubSummarizer(ContextSummary? ret) : IContextSummarizer
    {
        public int Calls;
        public Task<ContextSummary?> SummarizeAsync(
            string model, string transcript, string? userId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ret);
        }
    }

    private sealed class StubNative(string? ret) : INativeContextCompactor
    {
        public int Calls;
        public Task<string?> TryCompactAsync(
            IReadOnlyList<ChatCompletionMessage> history,
            string model, string? userId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ret);
        }
    }

    private async Task<ContextCompactionService> NewCompactionAsync(
        StubSummarizer summarizer, StubNative? native = null,
        ProviderService? providers = null)
    {
        var db = Db();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        var roles = new ModelRoleResolver(db, config,
            _ => Task.FromResult<List<ModelInfo>>([]),
            NullLogger<ModelRoleResolver>.Instance);
        var caps = new ModelCapabilityResolver(config,
            NullLogger<ModelCapabilityResolver>.Instance);
        if (providers is not null)
        {
            await config.SetAsync("models:capability-overrides",
                new Dictionary<string, string[]>
                {
                    ["ollama:m-warm"] = ["compaction"],
                });
        }
        return new ContextCompactionService(db, config, summarizer, roles,
            NullLogger<ContextCompactionService>.Instance,
            native, providers, caps);
    }

    private static List<ChatCompletionMessage> History(int n) =>
        Enumerable.Range(1, n)
            .Select(i => new ChatCompletionMessage(
                i % 2 == 0 ? "assistant" : "user", $"mensagem {i}"))
            .ToList();

    private static async Task SeedUserAsync(AppDbContext db)
    {
        if (!await db.Users.AnyAsync(u => u.Id == "u1"))
        {
            db.Users.Add(new User
            {
                Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user",
            });
        }
    }

    private async Task SeedChatAsync(string chatId, int messages)
    {
        await using var db = Db();
        db.Database.EnsureCreated();
        await SeedUserAsync(db);
        var chat = new Chat { Id = chatId, UserId = "u1", Title = "t" };
        for (var i = 0; i < messages; i++)
        {
            chat.Messages.Add(new ChatMessage
            {
                ChatId = chatId, Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"mensagem {i}", Position = i,
                Timestamp = 1,
            });
        }
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Compact_Manual_ForcaAbaixoDoLimiar()
    {
        var summarizer = new StubSummarizer(
            new ContextSummary("resumo manual", [], [], []));
        var svc = await NewCompactionAsync(summarizer);
        await SeedChatAsync("chat-c", 4);
        var result = await svc.CompactNowAsync(
            "chat-c", History(40), "m-warm", "u1", default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Compacted, Is.True);
            Assert.That(result.Native, Is.False);
        });
        await using var db = Db();
        var marker = db.Chats.Include(c => c.Messages)
            .First(c => c.Id == "chat-c")
            .Messages.FirstOrDefault(m => m.Role == "compaction");
        Assert.That(marker, Is.Not.Null);
    }

    [Test]
    public async Task Compact_NativeSemCapacidade_FallbackComWarning()
    {
        var summarizer = new StubSummarizer(
            new ContextSummary("resumo interno", [], [], []));
        var native = new StubNative("resumo nativo");
        // compaction:native ligado, mas o modelo não anuncia a capacidade.
        var db = Db();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:native", true);
        var roles = new ModelRoleResolver(db, config,
            _ => Task.FromResult<List<ModelInfo>>([]),
            NullLogger<ModelRoleResolver>.Instance);
        var caps = new ModelCapabilityResolver(config,
            NullLogger<ModelCapabilityResolver>.Instance);
        var providers = new ProviderService(
            new StubFactory(new ProviderHandler()), config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());
        await config.SetAsync("connections",
            new ConnectionsConfig(["http://ollama.test"], [], []));
        var svc = new ContextCompactionService(db, config, summarizer, roles,
            NullLogger<ContextCompactionService>.Instance,
            native, providers, caps);
        await SeedChatAsync("chat-n", 4);

        var result = await svc.CompactNowAsync(
            "chat-n", History(40), "m-warm", "u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Compacted, Is.True);
            Assert.That(result.Native, Is.False);
            Assert.That(result.Warning, Does.Contain("summarizer"));
            Assert.That(native.Calls, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Compact_NativeComCapacidade_UsaNativo()
    {
        var summarizer = new StubSummarizer(
            new ContextSummary("resumo interno", [], [], []));
        var native = new StubNative("resumo nativo");
        var db = Db();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:native", true);
        await config.SetAsync("models:capability-overrides",
            new Dictionary<string, string[]> { ["ollama:m-warm"] = ["compaction"] });
        await config.SetAsync("connections",
            new ConnectionsConfig(["http://ollama.test"], [], []));
        var roles = new ModelRoleResolver(db, config,
            _ => Task.FromResult<List<ModelInfo>>([]),
            NullLogger<ModelRoleResolver>.Instance);
        var caps = new ModelCapabilityResolver(config,
            NullLogger<ModelCapabilityResolver>.Instance);
        var providers = new ProviderService(
            new StubFactory(new ProviderHandler()), config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());
        var svc = new ContextCompactionService(db, config, summarizer, roles,
            NullLogger<ContextCompactionService>.Instance,
            native, providers, caps);
        await SeedChatAsync("chat-nc", 4);

        var result = await svc.CompactNowAsync(
            "chat-nc", History(40), "m-warm", "u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Compacted, Is.True);
            Assert.That(result.Native, Is.True);
            Assert.That(native.Calls, Is.EqualTo(1));
            Assert.That(summarizer.Calls, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Compact_NativeFalha_CaiNoSummarizer()
    {
        var summarizer = new StubSummarizer(
            new ContextSummary("resumo interno", [], [], []));
        var native = new StubNative(null);
        var db = Db();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:native", true);
        await config.SetAsync("models:capability-overrides",
            new Dictionary<string, string[]> { ["ollama:m-warm"] = ["compaction"] });
        await config.SetAsync("connections",
            new ConnectionsConfig(["http://ollama.test"], [], []));
        var roles = new ModelRoleResolver(db, config,
            _ => Task.FromResult<List<ModelInfo>>([]),
            NullLogger<ModelRoleResolver>.Instance);
        var caps = new ModelCapabilityResolver(config,
            NullLogger<ModelCapabilityResolver>.Instance);
        var providers = new ProviderService(
            new StubFactory(new ProviderHandler()), config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());
        var svc = new ContextCompactionService(db, config, summarizer, roles,
            NullLogger<ContextCompactionService>.Instance,
            native, providers, caps);
        await SeedChatAsync("chat-nf", 4);

        var result = await svc.CompactNowAsync(
            "chat-nf", History(40), "m-warm", "u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Compacted, Is.True);
            Assert.That(result.Native, Is.False);
            Assert.That(result.Warning, Does.Contain("falhou"));
            Assert.That(native.Calls, Is.EqualTo(1));
            Assert.That(summarizer.Calls, Is.EqualTo(1));
        });
    }

    // ---------- Cobertura extra: contributor + native compactor ----------

    [Test]
    public async Task Autoinvoke_SkillLocal_InjetaBlocoNoSystemPrompt()
    {
        var dir = Path.Join(_root, ".agents", "skills", "auto1");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Join(dir, "SKILL.md"),
            "---\nname: auto1\ndescription: auto\nautoinvoke: true\n---\nCorpo da auto1.");
        var db = Db();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        var overlay = new UserConfigOverlayService(db, config);
        var catalog = new RemoteSkillCatalog(new StubFactory(new CatalogHandler()));
        var contributor = new Completions.Contributors.AutoinvokeSkillsContributor(
            new SkillDiscoveryService(catalog), catalog, overlay, config);
        var ctx = new OpenWebUI.Infrastructure.SystemPrompt.PromptContext
        {
            Request = new ChatCompletionRequest(
                "m", [new ChatCompletionMessage("user", "oi")], Stream: true),
            User = new User { Id = "u1", Name = "U", Email = "u@t.local" },
            LastUserText = "oi", Model = "m", Ct = CancellationToken.None,
            WorkdirFactory = _ => Task.FromResult(_root!),
        };

        var sections = await contributor.ContributeAsync(ctx);
        Assert.Multiple(() =>
        {
            Assert.That(sections, Has.Count.EqualTo(1));
            Assert.That(sections[0], Does.Contain("## Skill: auto1"));
            Assert.That(sections[0], Does.Contain("Corpo da auto1"));
        });
    }

    [Test]
    public async Task NativeCompactor_EnviaContextManagementEDevolveTexto()
    {
        var db = Db();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("connections",
            new ConnectionsConfig(["http://ollama.test"], [], []));
        var providers = new ProviderService(
            new StubFactory(new ProviderHandler()), config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());
        var compactor = new ProviderNativeCompactor(providers);

        var text = await compactor.TryCompactAsync(
            [new ChatCompletionMessage("user", "olá")], "m-warm", null, default);
        Assert.That(text, Is.EqualTo("ok"));
    }

    // ---------- Cobertura: braços de erro e bordas ----------

    private sealed class ThrowingNative : INativeContextCompactor
    {
        public Task<string?> TryCompactAsync(
            IReadOnlyList<ChatCompletionMessage> history,
            string model, string? userId, CancellationToken ct) =>
            throw new InvalidOperationException("endpoint nativo fora");
    }

    [Test]
    public async Task Warming_SemConfig_NadaAFazer()
    {
        var db = Db();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        var providers = new ProviderService(
            new StubFactory(new ProviderHandler()), config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());

        Assert.That(await new ModelWarmer(db, config, providers)
            .RunOnceAsync(default), Is.EqualTo(0));
    }

    [Test]
    public async Task Warming_ModeloForaDoCatalogo_Pula()
    {
        var (warmer, handler) = await NewWarmerAsync(
            ["http://ollama.test"], [], "ghost-model", billable: false);
        var now = DateTimeOffset.UtcNow;
        await SeedRunAsync("ghost-model", now.ToUnixTimeSeconds() - 60);

        // Modelo configurado mas ausente do catálogo do provider → skip.
        Assert.That(await warmer.RunOnceAsync(default), Is.EqualTo(0));
        Assert.That(handler.ChatCalls, Is.EqualTo(0));
    }

    [Test]
    public async Task Warming_PingFalha_ContinuaSemLancar()
    {
        var (warmer, handler) = await NewWarmerAsync(
            ["http://ollama.test"], [], "m-warm", billable: false);
        handler.FailCompletions = true;
        var now = DateTimeOffset.UtcNow;
        await SeedRunAsync("m-warm", now.ToUnixTimeSeconds() - 60);

        // Ping falha → log interno, nunca lança, sent=0.
        Assert.That(await warmer.RunOnceAsync(default), Is.EqualTo(0));
    }

    [Test]
    public async Task Warming_Scheduler_TickSobreviveAFalhaDoWarmer()
    {
        var oldDelay = ModelWarmerScheduler.InitialDelay;
        var oldTick = ModelWarmerScheduler.Tick;
        ModelWarmerScheduler.InitialDelay = TimeSpan.FromMilliseconds(1);
        ModelWarmerScheduler.Tick = TimeSpan.FromMilliseconds(15);
        try
        {
            var db = Db();
            db.Database.EnsureCreated();
            var config = new ConfigService(db, TestCache.Create());
            // Config malformada → RunOnceAsync lança → o scheduler engole
            // e segue o loop (braço catch + while + delay).
            await config.SetAsync(ModelWarmer.ConfigKey, 42);
            var providers = new ProviderService(
                new StubFactory(new ProviderHandler()), config,
                NullLogger<ProviderService>.Instance,
                TestCache.Create(), new ConfigurationBuilder().Build());
            var warmer = new ModelWarmer(db, config, providers);
            var services = new ServiceCollection()
                .AddSingleton(warmer).BuildServiceProvider();
            var scheduler = new ModelWarmerScheduler(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ModelWarmerScheduler>.Instance);

            await scheduler.StartAsync(CancellationToken.None);
            await Task.Delay(120);
            await scheduler.StopAsync(CancellationToken.None);
            Assert.Pass("loop executou, engoliu a falha e cancelou limpo");
        }
        finally
        {
            ModelWarmerScheduler.InitialDelay = oldDelay;
            ModelWarmerScheduler.Tick = oldTick;
        }
    }

    [Test]
    public async Task Warming_Scheduler_CancelaDuranteDelayInicial()
    {
        var oldDelay = ModelWarmerScheduler.InitialDelay;
        ModelWarmerScheduler.InitialDelay = TimeSpan.FromSeconds(30);
        try
        {
            var services = new ServiceCollection().BuildServiceProvider();
            var scheduler = new ModelWarmerScheduler(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ModelWarmerScheduler>.Instance);
            await scheduler.StartAsync(CancellationToken.None);
            await Task.Delay(30);
            await scheduler.StopAsync(CancellationToken.None);
            Assert.Pass("cancelamento durante o delay inicial retorna cedo");
        }
        finally
        {
            ModelWarmerScheduler.InitialDelay = oldDelay;
        }
    }

    [Test]
    public async Task Remote_IndexFalha_NenhumaEntrada()
    {
        var handler = new CatalogHandler { IndexStatus = HttpStatusCode.NotFound };
        var catalog = NewCatalog(handler);
        var config = await NewConfigAsync();
        await SeedRemoteRootAsync(config, "https://skills.test");

        Assert.That(await catalog.RefreshIfStaleAsync(config, default), Is.EqualTo(0));
        Assert.That(catalog.Entries, Is.Empty);
    }

    [Test]
    public async Task Remote_IndexArraySimples_Parsa()
    {
        var handler = new CatalogHandler
        {
            IndexBody = """[{"name":"arr1","description":"forma array"}]""",
            SkillBody = "---\nname: arr1\ndescription: forma array\n---\nCorpo arr1.",
        };
        var catalog = NewCatalog(handler);
        var config = await NewConfigAsync();
        await SeedRemoteRootAsync(config, "https://skills.test");

        Assert.That(await catalog.RefreshIfStaleAsync(config, default), Is.EqualTo(1));
        Assert.That(catalog.Entries.Select(e => e.Name), Does.Contain("arr1"));
    }

    [Test]
    public async Task Remote_Skill404_EhPulada()
    {
        var handler = new CatalogHandler { SkillStatus = HttpStatusCode.NotFound };
        var catalog = NewCatalog(handler);
        var config = await NewConfigAsync();
        await SeedRemoteRootAsync(config, "https://skills.test");

        Assert.That(await catalog.RefreshIfStaleAsync(config, default), Is.EqualTo(1));
        Assert.That(catalog.Entries, Is.Empty);
    }

    [Test]
    public async Task Remote_CorpoDeclaradoGigante_EhPulado()
    {
        // Content-Length > 64KB → skip antes de ler (braço do header).
        var handler = new CatalogHandler { SkillBody = new string('x', 70 * 1024) };
        var catalog = NewCatalog(handler);
        var config = await NewConfigAsync();
        await SeedRemoteRootAsync(config, "https://skills.test");

        await catalog.RefreshIfStaleAsync(config, default);
        Assert.That(catalog.Entries, Is.Empty);
    }

    [Test]
    public async Task Remote_CorpoSemHeader_EhTruncado()
    {
        // Sem Content-Length → lê e trunca no cap de 64KB (braço do body).
        var handler = new CatalogHandler
        {
            SkillBody = new string('x', 70 * 1024), SkillSemContentLength = true,
        };
        var catalog = NewCatalog(handler);
        var config = await NewConfigAsync();
        await SeedRemoteRootAsync(config, "https://skills.test");

        await catalog.RefreshIfStaleAsync(config, default);
        Assert.That(catalog.Entries, Has.Count.EqualTo(1));
        Assert.That(catalog.Entries[0].Body.Length, Is.EqualTo(64 * 1024));
    }

    [Test]
    public async Task Remote_ConfigIlegivel_RetornaZero()
    {
        var handler = new CatalogHandler();
        var catalog = NewCatalog(handler);
        var config = await NewConfigAsync();
        // Tipo errado no kv → GetAsync<List<string>> lança → catálogo ignora.
        await config.SetAsync(RemoteSkillCatalog.ConfigKey, 42);

        Assert.That(await catalog.RefreshIfStaleAsync(config, default), Is.EqualTo(0));
    }

    [Test]
    public async Task Remote_FetchLanca_EhEngolido()
    {
        var handler = new CatalogHandler { Throw = true };
        var catalog = NewCatalog(handler);
        var config = await NewConfigAsync();
        await SeedRemoteRootAsync(config, "https://skills.test");

        Assert.That(await catalog.RefreshIfStaleAsync(config, default), Is.EqualTo(0));
    }

    [Test]
    public async Task Mcp_ListResources_PublicoEDesabilitado()
    {
        await using var db = Db();
        db.Database.EnsureCreated();
        var mcp = NewMcpDb(db);

        var list = await mcp.ListResourcesAsync(HttpServer(), default);
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list[0].Uri, Is.EqualTo("memo://alpha"));

        var disabled = HttpServer();
        disabled.Enabled = false;
        Assert.That(await mcp.ListResourcesAsync(disabled, default), Is.Empty);
    }

    [Test]
    public async Task Mcp_ReadResource_PublicoEDesabilitado()
    {
        await using var db = Db();
        db.Database.EnsureCreated();
        var mcp = NewMcpDb(db);

        var text = await mcp.ReadResourceAsync(HttpServer(), "memo://alpha", default);
        Assert.That(text, Does.Contain("conteudo-do-resource"));

        var disabled = HttpServer();
        disabled.Enabled = false;
        Assert.That(await mcp.ReadResourceAsync(disabled, "memo://alpha", default),
            Does.Contain("desabilitado"));
    }

    [Test]
    public async Task Mcp_ReadResourceTool_SemUri_ErroAmigavel()
    {
        await using var db = Db();
        db.Database.EnsureCreated();
        var mcp = NewMcpDb(db);

        var text = await mcp.CallToolAsync(
            HttpServer(), "read_resource", "{}", default);
        Assert.That(text, Does.Contain("argumento 'uri'"));
    }

    [Test]
    public async Task Mcp_GetPrompt_ServidorMorto_RetornaNull()
    {
        await using var db = Db();
        db.Database.EnsureCreated();
        var mcp = NewMcpDb(db);
        var dead = HttpServer();
        dead.Url = "http://127.0.0.1:1/mcp";

        Assert.That(await mcp.GetPromptAsync(dead, "revisao",
            new Dictionary<string, object?>(), default), Is.Null);

        var disabled = HttpServer();
        disabled.Enabled = false;
        Assert.That(await mcp.GetPromptAsync(disabled, "revisao",
            new Dictionary<string, object?>(), default), Is.Null);
        Assert.That(await mcp.ListPromptsAsync(disabled, default), Is.Empty);
    }

    [Test]
    public async Task Mcp_Refresh_ServerSoTools_SemVirtualNemPrompts()
    {
        using var toolsOnly = new FakeMcpServer(toolsOnly: true);
        toolsOnly.Start();
        await using var db = Db();
        db.Database.EnsureCreated();
        var server = HttpServer();
        server.Url = toolsOnly.Url;
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        var mcp = NewMcpDb(db);

        var count = await mcp.RefreshToolsAsync(server, default);
        var tools = db.Tools.Where(t => (t.Url ?? "")
            .StartsWith($"mcp://{server.Id}/")).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(3), "só as 3 tools reais do fake");
            Assert.That(tools.Any(t => t.Url!.EndsWith("/read_resource")), Is.False,
                "sem resources → sem tool virtual");
        });
        Assert.That(await mcp.ListPromptsAsync(server, default), Is.Empty);
    }

    [Test]
    public async Task ExpandMcp_SemServerOuPrompt_RetornaNull()
    {
        await using var db = Db();
        db.Database.EnsureCreated();
        var server = HttpServer();
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var svc = NewRepoCommandService(db);
        var (r1, e1) = await svc.ExpandAsync("u1", null, "ghost:revisao", null, default);
        Assert.Multiple(() =>
        {
            Assert.That(r1, Is.Null);
            Assert.That(e1, Is.Null, "server desconhecido não é comando — sem erro");
        });
        var (r2, e2) = await svc.ExpandAsync("u1", null, "fake:naoexiste", null, default);
        Assert.Multiple(() =>
        {
            Assert.That(r2, Is.Null);
            Assert.That(e2, Is.Null);
        });
    }

    [Test]
    public async Task ExpandMcp_GetFalha_RetornaErro()
    {
        await using var db = Db();
        db.Database.EnsureCreated();
        var server = HttpServer();
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        var svc = NewRepoCommandService(db);

        // Prime o cache de prompts com o server vivo, depois mata a URL —
        // o get falha mas a listagem sai do cache.
        var (ok, _) = await svc.ExpandAsync("u1", null, "fake:revisao", "a.cs", default);
        Assert.That(ok, Is.Not.Null);
        db.McpServers.Update(server);
        server.Url = "http://127.0.0.1:1/mcp";
        await db.SaveChangesAsync();

        var (result, error) = await svc.ExpandAsync(
            "u1", null, "fake:revisao", "a.cs", default);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(error, Does.Contain("falha"));
        });
    }

    private RepoCommandService NewRepoCommandService(AppDbContext db)
    {
        var config = new ConfigService(db, TestCache.Create());
        var providers = new ProviderService(
            new StubFactory(new ProviderHandler()), config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());
        var agents = new AgentDefinitionService(
            new StubEnv(_root), config, NullLogger<AgentDefinitionService>.Instance);
        return new RepoCommandService(
            new SkillDiscoveryService(), agents, providers,
            new ConfigurationBuilder().Build(),
            NewMcpDb(db), db);
    }

    [Test]
    public async Task Compact_ModeloAusenteDoCatalogo_SemNativo()
    {
        var summarizer = new StubSummarizer(
            new ContextSummary("resumo interno", [], [], []));
        var native = new StubNative("não deve ser chamado");
        var db = Db();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:native", true);
        var roles = new ModelRoleResolver(db, config,
            _ => Task.FromResult<List<ModelInfo>>([]),
            NullLogger<ModelRoleResolver>.Instance);
        var caps = new ModelCapabilityResolver(config,
            NullLogger<ModelCapabilityResolver>.Instance);
        var providers = new ProviderService(
            new StubFactory(new ProviderHandler()), config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());
        await config.SetAsync("connections",
            new ConnectionsConfig(["http://ollama.test"], [], []));
        var svc = new ContextCompactionService(db, config, summarizer, roles,
            NullLogger<ContextCompactionService>.Instance,
            native, providers, caps);
        await SeedChatAsync("chat-out", 4);

        // Modelo fora do catálogo → capacidade não resolve → sem native.
        var result = await svc.CompactNowAsync(
            "chat-out", History(40), "m-desconhecido", "u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Compacted, Is.True);
            Assert.That(native.Calls, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Compact_SemResumo_AvisaNadaParaCompactar()
    {
        var svc = await NewCompactionAsync(new StubSummarizer(null));
        await SeedChatAsync("chat-vazio", 4);
        var result = await svc.CompactNowAsync(
            "chat-vazio", History(10), "m-warm", "u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Compacted, Is.False);
            Assert.That(result.Warning, Does.Contain("Nada para compactar"));
        });
    }

    [Test]
    public async Task Compact_NativeLanca_RetornaFalhou()
    {
        var db = Db();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:native", true);
        await config.SetAsync("models:capability-overrides",
            new Dictionary<string, string[]> { ["ollama:m-warm"] = ["compaction"] });
        await config.SetAsync("connections",
            new ConnectionsConfig(["http://ollama.test"], [], []));
        var roles = new ModelRoleResolver(db, config,
            _ => Task.FromResult<List<ModelInfo>>([]),
            NullLogger<ModelRoleResolver>.Instance);
        var caps = new ModelCapabilityResolver(config,
            NullLogger<ModelCapabilityResolver>.Instance);
        var providers = new ProviderService(
            new StubFactory(new ProviderHandler()), config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());
        var svc = new ContextCompactionService(db, config,
            new StubSummarizer(null), roles,
            NullLogger<ContextCompactionService>.Instance,
            new ThrowingNative(), providers, caps);
        await SeedChatAsync("chat-throw", 4);

        var result = await svc.CompactNowAsync(
            "chat-throw", History(10), "m-warm", "u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Compacted, Is.False);
            Assert.That(result.Warning, Does.Contain("falhou"));
        });
    }

    [Test]
    public async Task Autoinvoke_SkillOculta_NaoInjeta()
    {
        var dir = Path.Join(_root, ".agents", "skills", "auto1");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Join(dir, "SKILL.md"),
            "---\nname: auto1\ndescription: auto\nautoinvoke: true\n---\nCorpo da auto1.");
        var db = Db();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        var overlay = new UserConfigOverlayService(db, config);
        var catalog = new RemoteSkillCatalog(new StubFactory(new CatalogHandler()));
        var contributor = new Completions.Contributors.AutoinvokeSkillsContributor(
            new SkillDiscoveryService(catalog), catalog, overlay, config);
        var ctx = new OpenWebUI.Infrastructure.SystemPrompt.PromptContext
        {
            Request = new ChatCompletionRequest(
                "m", [new ChatCompletionMessage("user", "oi")], Stream: true),
            User = new User { Id = "u1", Name = "U", Email = "u@t.local" },
            LastUserText = "oi", Model = "m", Ct = CancellationToken.None,
            WorkdirFactory = _ => Task.FromResult(_root!),
        };

        // Usuário desligou a skill → não injeta.
        await config.SetAsync("u:u1:skills-disabled", new List<string> { "auto1" });
        Assert.That(await contributor.ContributeAsync(ctx), Is.Empty);

        // Desligada globalmente pelo admin → também não injeta.
        await config.SetAsync("u:u1:skills-disabled", new List<string>());
        await config.SetAsync("skills-disabled", new List<string> { "auto1" });
        Assert.That(await contributor.ContributeAsync(ctx), Is.Empty);
    }

    [Test]
    public async Task NativeCompactor_FalhaNoProvider_RetornaNull()
    {
        var db = Db();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("connections",
            new ConnectionsConfig(["http://ollama.test"], [], []));
        var providers = new ProviderService(
            new StubFactory(new ProviderHandler { FailCompletions = true }), config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());
        var compactor = new ProviderNativeCompactor(providers);

        Assert.That(await compactor.TryCompactAsync(
            [new ChatCompletionMessage("user", "olá")], "m-warm", null, default),
            Is.Null);
    }
}
