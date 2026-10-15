using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261013-model-roles: resolução de papel com cadeia de fallback,
/// persistência em SettingsJson preservando chaves, precedência de
/// capacidades (override admin → reportado → catálogo) e roteamento do
/// papel "memory" na compactação.
/// </summary>
[TestFixture]
public class ModelRolesTests
{
    private string _dir = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Join(Path.GetTempPath(), $"owui-roles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Join(_dir, "t.db");
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_dir)) { Directory.Delete(_dir, true); } }
        catch { /* ignore */ }
    }

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static User NewUser(string id, string settingsJson = "{}") =>
        new() { Id = id, Name = "T", Email = $"{id}@t.dev", SettingsJson = settingsJson };

    private static ModelRoleResolver NewResolver(AppDbContext db, params string[] catalogIds) =>
        new(db,
            new ConfigService(db, TestCache.Create()),
            _ => Task.FromResult(catalogIds
                .Select(id => new ModelInfo(id, id, "ollama", "x")).ToList()),
            NullLogger<ModelRoleResolver>.Instance);

    // ---------------- ModelRoleResolver ----------------

    [Test]
    public async Task Resolve_PapelConfigurado_UsaModeloDoPapel()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("u1",
            """{"modelRoles":{"tasks":["tiny"]}}"""));
        await db.SaveChangesAsync();

        var roles = NewResolver(db, "tiny", "chat-model");
        Assert.That(await roles.ResolveAsync("tasks", "u1", "chat-model",
            CancellationToken.None), Is.EqualTo("tiny"));
    }

    [Test]
    public async Task Resolve_CadeiaFallback_PulaIdDesconhecido()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("u1",
            """{"modelRoles":{"tasks":["sumiu","tiny"]}}"""));
        await db.SaveChangesAsync();

        var roles = NewResolver(db, "tiny");
        Assert.That(await roles.ResolveAsync("tasks", "u1", "chat-model",
            CancellationToken.None), Is.EqualTo("tiny"));
    }

    [Test]
    public async Task Resolve_PapelVazio_VoltaModeloDoChat()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("u1"));
        await db.SaveChangesAsync();

        var roles = NewResolver(db, "tiny");
        Assert.That(await roles.ResolveAsync("tasks", "u1", "chat-model",
            CancellationToken.None), Is.EqualTo("chat-model"));
    }

    [Test]
    public async Task Resolve_NenhumDaCadeiaConhecido_VoltaFallback()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("u1",
            """{"modelRoles":{"memory":["sumiu1","sumiu2"]}}"""));
        await db.SaveChangesAsync();

        var roles = NewResolver(db, "tiny");
        Assert.That(await roles.ResolveAsync("memory", "u1", "chat-model",
            CancellationToken.None), Is.EqualTo("chat-model"));
    }

    [Test]
    public async Task SetRoles_PreservaOutrasChavesDoSettings()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("u1", """{"theme":"dark","disabledModels":["a:b"]}"""));
        await db.SaveChangesAsync();

        var roles = NewResolver(db);
        await roles.SetRolesAsync("u1",
            new Dictionary<string, List<string>> { ["tasks"] = ["tiny"] },
            CancellationToken.None);

        var user = await db.Users.FindAsync("u1");
        Assert.That(user!.SettingsJson, Does.Contain("\"theme\":\"dark\""));
        Assert.That(user.SettingsJson, Does.Contain("disabledModels"));
        Assert.That(user.SettingsJson, Does.Contain("tiny"));
    }

    [Test]
    public async Task SetRoles_CadeiaVaziaRemovePapel()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("u1", """{"modelRoles":{"tasks":["tiny"]}}"""));
        await db.SaveChangesAsync();

        var roles = NewResolver(db);
        await roles.SetRolesAsync("u1",
            new Dictionary<string, List<string>> { ["tasks"] = [] },
            CancellationToken.None);

        var stored = await roles.GetRolesAsync("u1", CancellationToken.None);
        Assert.That(stored.ContainsKey("tasks"), Is.False);
    }

    [Test]
    public async Task SetRoles_PapelDesconhecidoEIgnorado()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("u1"));
        await db.SaveChangesAsync();

        var roles = NewResolver(db);
        await roles.SetRolesAsync("u1",
            new Dictionary<string, List<string>>
            { ["hacker"] = ["x"], ["tasks"] = ["tiny"] },
            CancellationToken.None);

        var stored = await roles.GetRolesAsync("u1", CancellationToken.None);
        Assert.That(stored.Keys, Is.EquivalentTo(new[] { "tasks" }));
    }

    // ---------------- ModelCapabilityResolver ----------------

    private ModelCapabilityResolver NewCaps(AppDbContext db) =>
        new(new ConfigService(db, TestCache.Create()),
            NullLogger<ModelCapabilityResolver>.Instance);

    [Test]
    public async Task Caps_OverrideAdmin_VenceTudo()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("models:capability-overrides",
            new Dictionary<string, string[]> { ["ollama:llama3.1"] = ["vision"] },
            CancellationToken.None);
        var caps = new ModelCapabilityResolver(config,
            NullLogger<ModelCapabilityResolver>.Instance);

        var result = await caps.ResolveAsync("ollama", "llama3.1",
            ["tools"], CancellationToken.None);
        Assert.That(result, Is.EquivalentTo(new[] { "vision" }));
    }

    [Test]
    public async Task Caps_ReportadoPeloProvider_VenceCatalogo()
    {
        await using var db = NewDb();
        var caps = NewCaps(db);
        // catálogo diria só ["vision","tools"] para qwen*vl*; reportado vence.
        var result = await caps.ResolveAsync("ollama", "qwen2-vl",
            ["vision"], CancellationToken.None);
        Assert.That(result, Is.EquivalentTo(new[] { "vision" }));
    }

    [Test]
    public async Task Caps_CatalogoGlob_ResolvePorProvider()
    {
        await using var db = NewDb();
        var caps = NewCaps(db);
        Assert.That(await caps.ResolveAsync("openai", "gpt-4o-mini", null,
                CancellationToken.None), Does.Contain("vision"));
        Assert.That(await caps.ResolveAsync("ollama", "qwen3-32b", null,
                CancellationToken.None), Does.Contain("thinking"));
    }

    [Test]
    public async Task Caps_PadraoMaisEspecifico_Vence()
    {
        await using var db = NewDb();
        var caps = NewCaps(db);
        // "gpt-4o-mini" casa "gpt-4o*" (vision+tools) e "gpt-4*" (só tools)
        // — o padrão mais longo (específico) vence.
        var result = await caps.ResolveAsync("openai", "gpt-4o-mini", null,
            CancellationToken.None);
        Assert.That(result, Does.Contain("vision"));
        Assert.That(result, Does.Contain("tools"));
    }

    [Test]
    public async Task Caps_ModeloDesconhecido_SemCapacidades()
    {
        await using var db = NewDb();
        var caps = NewCaps(db);
        Assert.That(await caps.ResolveAsync("openai", "modelo-qualquer-x", null,
            CancellationToken.None), Is.Empty);
        Assert.That(await caps.ResolveAsync("custom", "gpt-4o", null,
            CancellationToken.None), Is.Empty); // provider errado não casa
    }

    [Test]
    public async Task Caps_Enrich_PreencheSomenteResolvidos()
    {
        await using var db = NewDb();
        var caps = NewCaps(db);
        var models = new List<ModelInfo>
        {
            new("gpt-4o", "gpt-4o", "openai", "o"),
            new("desconhecido-x", "x", "openai", "o"),
        };
        var enriched = await caps.EnrichAsync(models, CancellationToken.None);
        Assert.That(enriched[0].Capabilities, Does.Contain("vision"));
        Assert.That(enriched[1].Capabilities, Is.Null);
    }

    // ---------------- Compaction + papel "memory" ----------------

    [Test]
    public async Task MaybeCompact_PapelMemory_RoteiaModeloDoPapel()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("u1", """{"modelRoles":{"memory":["tiny"]}}"""));
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1" });
        await db.SaveChangesAsync();

        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 1, CancellationToken.None);
        await config.SetAsync("compaction:tail_messages", 1, CancellationToken.None);
        await config.SetAsync("compaction:tail_chars", 1, CancellationToken.None);

        var summarizer = new RecordingSummarizer("resumo");
        var resolver = new ModelRoleResolver(db,
            new ConfigService(db, TestCache.Create()),
            _ => Task.FromResult(new List<ModelInfo> { new("tiny", "t", "ollama", "x") }),
            NullLogger<ModelRoleResolver>.Instance);
        var svc = new ContextCompactionService(db, config, summarizer, resolver,
            NullLogger<ContextCompactionService>.Instance);

        var history = Enumerable.Range(0, 5)
            .Select(i => new ChatCompletionMessage("user", $"msg{i}"))
            .ToList();
        var ok = await svc.MaybeCompactAsync("c1", history, "chat-model",
            CancellationToken.None, "u1");

        Assert.That(ok, Is.True);
        Assert.That(summarizer.LastModel, Is.EqualTo("tiny"));
    }

    [Test]
    public async Task MaybeCompact_SemPapelMemory_UsaModeloDaRun()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("u1"));
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1" });
        await db.SaveChangesAsync();

        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 1, CancellationToken.None);
        await config.SetAsync("compaction:tail_messages", 1, CancellationToken.None);
        await config.SetAsync("compaction:tail_chars", 1, CancellationToken.None);

        var summarizer = new RecordingSummarizer("resumo");
        var resolver = new ModelRoleResolver(db,
            new ConfigService(db, TestCache.Create()),
            _ => Task.FromResult(new List<ModelInfo>()),
            NullLogger<ModelRoleResolver>.Instance);
        var svc = new ContextCompactionService(db, config, summarizer, resolver,
            NullLogger<ContextCompactionService>.Instance);

        var ok = await svc.MaybeCompactAsync("c1",
            [.. Enumerable.Range(0, 5).Select(i => new ChatCompletionMessage("user", $"m{i}"))],
            "chat-model", CancellationToken.None, "u1");

        Assert.That(ok, Is.True);
        Assert.That(summarizer.LastModel, Is.EqualTo("chat-model"));
    }

    private sealed class RecordingSummarizer(string? result) : IContextSummarizer
    {
        public string? LastModel { get; private set; }

        public Task<ContextSummary?> SummarizeAsync(string model, string transcript, string? userId, CancellationToken ct)
        {
            LastModel = model;
            return Task.FromResult(result is null ? null
            : new ContextSummary(result, [], [], []));
        }
    }
}
