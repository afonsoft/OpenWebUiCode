using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Agentes declarativos <c>*.agent.md</c> (SPEC-20261014-agent-definitions):
/// parse, precedência built-in&lt;admin&lt;user&lt;workspace, opt-in de
/// workspace, <c>delegate_task(agent:…)</c> e <c>agent_catalog</c>.
/// </summary>
[TestFixture]
public class AgentDefinitionsTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private ServiceProvider _provider = null!;
    private RecordingDispatcher _dispatcher = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-agents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "test.db");

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddHybridCache();
        services.AddSingleton<IHostEnvironment>(new StubEnv(_root));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory());
        services.AddScoped<ConfigService>();
        services.AddScoped<WorkspaceRepoService>();
        services.AddScoped<ProviderService>();
        services.AddScoped<UserConfigOverlayService>();
        services.AddScoped<AgentDefinitionService>();
        services.AddScoped(sp => new ModelRoleResolver(
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<ConfigService>(),
            ct => sp.GetRequiredService<ProviderService>().ListModelsAsync(ct),
            sp.GetRequiredService<ILogger<ModelRoleResolver>>()));
        // Registry só com as tools que o teste de allowlist consulta.
        services.AddScoped<IBuiltinChatTool>(sp =>
            new FileReadBuiltinTool(
                sp.GetRequiredService<HistoryResourceService>()));
        services.AddScoped<IBuiltinChatTool>(_ => new FileWriteBuiltinTool());
        services.AddScoped<IBuiltinChatTool>(_ => new DelegateTaskBuiltinTool(
            _.GetRequiredService<IServiceScopeFactory>(),
            _.GetRequiredService<IChatRunDispatcher>()));
        services.AddScoped<IBuiltinChatTool>(sp =>
            new AgentCatalogBuiltinTool(sp.GetRequiredService<AgentDefinitionService>()));
        services.AddScoped<BuiltinToolRegistry>();
        services.AddScoped<HistoryResourceService>();
        _dispatcher = new RecordingDispatcher();
        services.AddSingleton<IChatRunDispatcher>(_dispatcher);
        _provider = services.BuildServiceProvider();

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private ConfigService Config() => _provider.GetRequiredService<ConfigService>();

    private string DataRoot => Path.Join(_root, "data");

    private void WriteAgent(string layerDir, string fileName, string content)
    {
        Directory.CreateDirectory(layerDir);
        File.WriteAllText(Path.Join(layerDir, fileName), content);
        // O catálogo é cacheado por 60s — cada escrita invalida o cache.
        _provider.GetRequiredService<AgentDefinitionService>().Invalidate("u1");
    }

    // ---------------- Parse (RF-001) ----------------

    [Test]
    public async Task Parse_Valido_RetornaDefinicao()
    {
        var svc = _provider.GetRequiredService<AgentDefinitionService>();
        var def = svc.TryParse("""
            ---
            name: revisor
            description: Revê diffs com foco em segurança
            model: role:advisor
            tools: [file_read, file_grep]
            spawns: researcher
            maxDepth: 2
            skills: [revisao]
            blocking: false
            ---
            Você é um revisor meticuloso.
            """, "x.agent.md", "user");

        Assert.Multiple(() =>
        {
            Assert.That(def, Is.Not.Null);
            Assert.That(def!.Name, Is.EqualTo("revisor"));
            Assert.That(def.Model, Is.EqualTo("role:advisor"));
            Assert.That(def.Tools, Is.EqualTo(new[] { "file_read", "file_grep" }));
            Assert.That(def.Spawns, Is.EqualTo(new[] { "researcher" }));
            Assert.That(def.MaxDepth, Is.EqualTo(2));
            Assert.That(def.Skills, Is.EqualTo(new[] { "revisao" }));
            Assert.That(def.Blocking, Is.False);
            Assert.That(def.SystemPrompt, Does.Contain("revisor meticuloso"));
            Assert.That(def.Source, Is.EqualTo("user"));
        });
        await Task.CompletedTask;
    }

    [Test]
    public async Task Parse_SemName_IgnoradoSemDerrubar()
    {
        var svc = _provider.GetRequiredService<AgentDefinitionService>();
        Assert.That(svc.TryParse("---\ndescription: órfão\n---\ncorpo", "x", "user"), Is.Null);
        Assert.That(svc.TryParse("sem frontmatter", "y", "user"), Is.Null);
        await Task.CompletedTask;
    }

    // ---------------- Discovery + precedência (RF-002) ----------------

    [Test]
    public async Task Discovery_Precedencia_UserVenceAdminVenceBuiltin()
    {
        var svc = _provider.GetRequiredService<AgentDefinitionService>();
        // Builtin "code-reviewer" existe no assembly.
        var baseCatalog = await svc.ListAsync("u1", null, default);
        Assert.That(baseCatalog.ContainsKey("code-reviewer"), Is.True);
        Assert.That(baseCatalog["code-reviewer"].Source, Is.EqualTo("builtin"));

        WriteAgent(Path.Join(DataRoot, "agents"), "x.agent.md", """
            ---
            name: code-reviewer
            description: versão do admin
            ---
            prompt admin
            """);
        WriteAgent(Path.Join(DataRoot, "users", "u1", "agents"), "x.agent.md", """
            ---
            name: code-reviewer
            description: versão do usuário
            ---
            prompt usuário
            """);
        svc.Invalidate("u1");
        var catalog = await svc.ListAsync("u1", null, default);
        Assert.Multiple(() =>
        {
            Assert.That(catalog["code-reviewer"].Description, Is.EqualTo("versão do usuário"));
            Assert.That(catalog["code-reviewer"].Source, Is.EqualTo("user"));
        });
    }

    [Test]
    public async Task Discovery_Workspace_GatedPorOptIn()
    {
        var svc = _provider.GetRequiredService<AgentDefinitionService>();
        var workdir = Path.Join(_root, "workdir");
        WriteAgent(Path.Join(workdir, ".openwebui", "agents"), "w.agent.md", """
            ---
            name: ws-agent
            description: agente do repo
            tools: *
            ---
            prompt do workspace
            """);

        // Sem opt-in: agente de workspace não aparece.
        var catalog = await svc.ListAsync("u1", workdir, default);
        Assert.That(catalog.ContainsKey("ws-agent"), Is.False);
        Assert.That(AgentDefinitionService.HasWorkspaceAgents(workdir), Is.True);

        // Com opt-in: aparece com source workspace.
        await Config().SetAsync("u:u1:agents.workspace.enabled", true, default);
        svc.Invalidate("u1");
        var after = await svc.ListAsync("u1", workdir, default);
        Assert.Multiple(() =>
        {
            Assert.That(after["ws-agent"].Source, Is.EqualTo("workspace"));
            Assert.That(after["ws-agent"].Tools, Is.EqualTo(new[] { "*" }));
        });
    }

    // ---------------- agent_catalog (RF-004) ----------------

    [Test]
    public async Task AgentCatalog_ListaSoVisiveis()
    {
        WriteAgent(Path.Join(DataRoot, "users", "u1", "agents"), "m.agent.md", """
            ---
            name: meu-agente
            description: só do u1
            ---
            corpo
            """);
        _provider.GetRequiredService<AgentDefinitionService>().Invalidate("u1");
        var tool = _provider.GetServices<IBuiltinChatTool>()
            .OfType<AgentCatalogBuiltinTool>().Single();
        var ctx = new BuiltinToolContext("u1", null, null, _root, _root);

        var result = await tool.ExecuteAsync(
            System.Text.Json.JsonDocument.Parse("{}").RootElement, ctx, default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("meu-agente"));
            Assert.That(result.Text, Does.Contain("[user]"));
            Assert.That(result.Text, Does.Contain("code-reviewer"));
            Assert.That(result.Text, Does.Contain("[builtin]"));
        });
    }

    // ---------------- delegate_task agent: (RF-003) ----------------

    [Test]
    public async Task Delegate_Agent_MontaFilhoComSystemEToolsFiltradas()
    {
        WriteAgent(Path.Join(DataRoot, "users", "u1", "agents"), "r.agent.md", """
            ---
            name: revisor
            description: revê código
            model: fake:agent
            tools: [file_read]
            ---
            system do revisor
            """);
        var (parentRun, tool) = await SeedParentRunAsync();
        var ctx = new BuiltinToolContext("u1", parentRun.ChatId, parentRun.Id, _root, _root);
        var args = System.Text.Json.JsonDocument.Parse(
            """{"agent":"revisor","prompt":"revise o diff","wait":false}""").RootElement;

        var result = await tool.ExecuteAsync(args, ctx, default);

        Assert.That(result.Text, Does.Contain("Subtarefa delegada"));
        await using var db = NewDb();
        var child = await db.Chats.AsNoTracking()
            .Include(c => c.Messages)
            .Where(c => c.ParentChatId == parentRun.ChatId).SingleAsync();
        var childRun = await db.ChatRuns.AsNoTracking()
            .Where(r => r.ChatId == child.Id).SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(child.Title, Does.StartWith("agent:revisor:"));
            Assert.That(childRun.Model, Is.EqualTo("fake:agent"));
            var systemMsg = child.Messages.FirstOrDefault(m => m.Role == "system");
            Assert.That(systemMsg?.Content, Is.EqualTo("system do revisor"));
            var request = System.Text.Json.JsonSerializer
                .Deserialize<ChatCompletionRequest>(childRun.RequestJson,
                    new System.Text.Json.JsonSerializerOptions(
                        System.Text.Json.JsonSerializerDefaults.Web));
            // allowlist [file_read] ∩ pai [file_read, file_write] → só read;
            // spawns vazio → sem delegate_task.
            Assert.That(request!.ToolIds, Is.EqualTo(new[] { "builtin:file_read" }));
        });
    }

    [Test]
    public async Task Delegate_AgentTools_NuncaAmpliaConjuntoDoPai()
    {
        // Agente pede tools que o pai não tem → ficam fora do filho.
        WriteAgent(Path.Join(DataRoot, "users", "u1", "agents"), "r.agent.md", """
            ---
            name: escalador
            description: pede shell_exec
            tools: [file_read, shell_exec, file_write]
            ---
            corpo
            """);
        var (parentRun, tool) = await SeedParentRunAsync(
            toolIds: ["builtin:file_read"]);
        var ctx = new BuiltinToolContext("u1", parentRun.ChatId, parentRun.Id, _root, _root);
        var args = System.Text.Json.JsonDocument.Parse(
            """{"agent":"escalador","prompt":"x","wait":false}""").RootElement;

        await tool.ExecuteAsync(args, ctx, default);

        await using var db = NewDb();
        var childRun = await db.ChatRuns.AsNoTracking()
            .Where(r => r.ParentRunId == parentRun.Id).SingleAsync();
        var request = System.Text.Json.JsonSerializer
            .Deserialize<ChatCompletionRequest>(childRun.RequestJson,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));
        // shell_exec/file_write não existem no pai → só file_read passa.
        Assert.That(request!.ToolIds, Is.EqualTo(new[] { "builtin:file_read" }));
    }

    [Test]
    public async Task Delegate_AgentInexistente_ErroComCatalogo()
    {
        var (parentRun, tool) = await SeedParentRunAsync();
        var ctx = new BuiltinToolContext("u1", parentRun.ChatId, parentRun.Id, _root, _root);
        var args = System.Text.Json.JsonDocument.Parse(
            """{"agent":"fantasma","prompt":"x","wait":false}""").RootElement;

        var result = await tool.ExecuteAsync(args, ctx, default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("fantasma"));
            Assert.That(result.Text, Does.Contain("code-reviewer"));
        });
    }

    [Test]
    public async Task Delegate_RoleModel_ResolveCadeiaDoPapel()
    {
        WriteAgent(Path.Join(DataRoot, "users", "u1", "agents"), "r.agent.md", """
            ---
            name: revisor
            description: revê
            model: role:advisor
            ---
            corpo
            """);
        // Catálogo do provider precisa conter o id da cadeia — senão o
        // resolver cai no fallback (modelo do pai).
        await Config().SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []), default);
        var resolver = _provider.GetRequiredService<ModelRoleResolver>();
        await resolver.SetRolesAsync("u1",
            new Dictionary<string, List<string>> { ["advisor"] = ["fake:mem"] },
            default);
        var (parentRun, tool) = await SeedParentRunAsync();
        var ctx = new BuiltinToolContext("u1", parentRun.ChatId, parentRun.Id, _root, _root);
        var args = System.Text.Json.JsonDocument.Parse(
            """{"agent":"revisor","prompt":"x","wait":false}""").RootElement;

        await tool.ExecuteAsync(args, ctx, default);

        await using var db = NewDb();
        var childRun = await db.ChatRuns.AsNoTracking()
            .Where(r => r.ParentRunId == parentRun.Id).SingleAsync();
        Assert.That(childRun.Model, Is.EqualTo("fake:mem"));
    }

    [Test]
    public async Task Delegate_SpawnsAllowlist_BloqueiaNomeForaDaLista()
    {
        // A run atual tem allowlist [revisor] — delegar "fantasma-real" falha.
        var (parentRun, tool) = await SeedParentRunAsync();
        await Config().SetAsync($"r:{parentRun.Id}:agent.spawns", "revisor", default);
        var ctx = new BuiltinToolContext("u1", parentRun.ChatId, parentRun.Id, _root, _root);
        var args = System.Text.Json.JsonDocument.Parse(
            """{"agent":"researcher","prompt":"x","wait":false}""").RootElement;

        var result = await tool.ExecuteAsync(args, ctx, default);

        Assert.That(result.Text, Does.Contain("revisor"));
        Assert.That(result.Text, Does.Contain("allowlist"));
    }

    [Test]
    public async Task Delegate_DepthZero_BloqueiaQualquerDelegacao()
    {
        var (parentRun, tool) = await SeedParentRunAsync();
        await Config().SetAsync($"r:{parentRun.Id}:agent.depth", 0, default);
        var ctx = new BuiltinToolContext("u1", parentRun.ChatId, parentRun.Id, _root, _root);
        var args = System.Text.Json.JsonDocument.Parse(
            """{"prompt":"x","wait":false}""").RootElement;

        var result = await tool.ExecuteAsync(args, ctx, default);

        Assert.That(result.Text, Does.Contain("maxDepth"));
    }

    [Test]
    public async Task Delegate_FilhoComSpawns_GravaConstraintsEmKv()
    {
        WriteAgent(Path.Join(DataRoot, "users", "u1", "agents"), "r.agent.md", """
            ---
            name: orquestrador
            description: orquestra
            spawns: [researcher, code-reviewer]
            maxDepth: 1
            ---
            corpo
            """);
        var (parentRun, tool) = await SeedParentRunAsync();
        var ctx = new BuiltinToolContext("u1", parentRun.ChatId, parentRun.Id, _root, _root);
        var args = System.Text.Json.JsonDocument.Parse(
            """{"agent":"orquestrador","prompt":"x","wait":false}""").RootElement;

        await tool.ExecuteAsync(args, ctx, default);

        await using var db = NewDb();
        var childRun = await db.ChatRuns.AsNoTracking()
            .Where(r => r.ParentRunId == parentRun.Id).SingleAsync();
        var request = System.Text.Json.JsonSerializer
            .Deserialize<ChatCompletionRequest>(childRun.RequestJson,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));
        var spawns = await Config().GetAsync<string?>(
            $"r:{childRun.Id}:agent.spawns", null, default);
        var depth = await Config().GetAsync<int?>(
            $"r:{childRun.Id}:agent.depth", null, default);
        Assert.Multiple(() =>
        {
            // Com spawns + maxDepth>0: o filho mantém delegate_task.
            Assert.That(request!.ToolIds,
                Does.Contain("builtin:delegate_task"));
            Assert.That(spawns, Is.EqualTo("researcher,code-reviewer"));
            Assert.That(depth, Is.EqualTo(1));
        });
    }

    // ---------------- helpers ----------------

    private async Task<(ChatRun Run, DelegateTaskBuiltinTool Tool)> SeedParentRunAsync(
        string[]? toolIds = null)
    {
        await using var db = NewDb();
        var chat = new Chat
        {
            Id = $"chat-{Guid.NewGuid():N}", UserId = "u1", Title = "pai",
            ModelsJson = """["fake:1"]""",
            CreatedAt = 1, UpdatedAt = 1,
        };
        db.Chats.Add(chat);
        var request = new ChatCompletionRequest("fake:1",
            [new ChatCompletionMessage("user", "vá")],
            Stream: true,
            ToolIds: toolIds?.ToList()
                ?? ["builtin:file_read", "builtin:file_write", "builtin:delegate_task"]);
        var run = new ChatRun
        {
            Id = $"run-{Guid.NewGuid():N}", ChatId = chat.Id, UserId = "u1",
            Model = "fake:1",
            RequestJson = System.Text.Json.JsonSerializer.Serialize(request,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web)),
            CreatedAt = 1,
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync();
        var tool = _provider.GetServices<IBuiltinChatTool>()
            .OfType<DelegateTaskBuiltinTool>().Single();
        return (run, tool);
    }

    private sealed class RecordingDispatcher : IChatRunDispatcher
    {
        public List<string> Enqueued { get; } = [];
        public void Enqueue(string runId) => Enqueued.Add(runId);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new OllamaStub()) { BaseAddress = new Uri("http://stub.local") };
    }

    /// <summary>Ollama stub: /api/tags devolve os modelos do catálogo de teste.</summary>
    private sealed class OllamaStub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath == "/api/tags"
                ? """{"models":[{"model":"fake:1","name":"fake:1"},{"model":"fake:mem","name":"fake:mem"},{"model":"fake:agent","name":"fake:agent"}]}"""
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8,
                    "application/json"),
            });
        }
    }

    private sealed class StubEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
