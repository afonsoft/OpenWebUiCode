using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261020-context-snapshots RF-005: contrato das 3 builtin tools
/// (context_save / context_recovery / context_compact) — escopo chat,
/// erros estáveis (invalid_payload, secret_detected, context_not_found),
/// gate contexttools.enabled e reuso de poda/sumarização.
/// </summary>
[TestFixture]
public class ContextSnapshotBuiltinToolsTests
{
    private string _dir = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Join(Path.GetTempPath(), $"owui-snap-tools-{Guid.NewGuid():N}");
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

    private ContextSnapshotService Snapshots(AppDbContext db, ConfigService config) =>
        new(db, config, new WorkspaceRepoService(config, new TestEnv(_dir)));

    private static ModelRoleResolver Roles(AppDbContext db, ConfigService config) =>
        new(db, config, _ => Task.FromResult(new List<ModelInfo>()),
            NullLogger<ModelRoleResolver>.Instance);

    private ContextSaveBuiltinTool SaveTool(
        AppDbContext db, ConfigService config, IContextSummarizer? summarizer = null) =>
        new(Snapshots(db, config), new ContextBoundaryService(db, config),
            summarizer ?? new FakeSummarizer("RES"), Roles(db, config));

    private ContextRecoveryBuiltinTool RecoveryTool(AppDbContext db, ConfigService config) =>
        new(Snapshots(db, config), new ContextBoundaryService(db, config));

    private ContextCompactBuiltinTool CompactTool(
        AppDbContext db, ConfigService config, IContextSummarizer? summarizer = null) =>
        new(Snapshots(db, config), new ContextBoundaryService(db, config),
            summarizer ?? new FakeSummarizer("RES"), Roles(db, config));

    private static BuiltinToolContext Ctx(string? chatId = "c1") =>
        new("u1", chatId, "r1", "/tmp", "/tmp", Model: "m1");

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static JsonElement ResultJson(BuiltinToolResult r) =>
        JsonDocument.Parse(r.Text).RootElement;

    // ---------- context_save / context_recovery ----------

    [Test]
    public async Task Save_ERecovery_RoundTrip()
    {
        // AC: save com 2 decisões → recovery sem version devolve version 1.
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());

        var save = await SaveTool(db, config).ExecuteAsync(
            Args("""
                {"label":"l","items":[
                {"kind":"decision","content":"usar tokens"},
                {"kind":"decision","content":"OR com chars"}]}
                """),
            Ctx(), CancellationToken.None);
        var saved = ResultJson(save);
        Assert.Multiple(() =>
        {
            Assert.That(saved.GetProperty("version").GetInt32(), Is.EqualTo(1));
            Assert.That(saved.GetProperty("tokenEstimate").GetInt32(), Is.GreaterThan(0));
        });

        var rec = await RecoveryTool(db, config).ExecuteAsync(
            Args("{}"), Ctx(), CancellationToken.None);
        var payload = ResultJson(rec);
        var items = payload.GetProperty("items").EnumerateArray().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(payload.GetProperty("version").GetInt32(), Is.EqualTo(1));
            Assert.That(items, Has.Count.EqualTo(2));
            Assert.That(items[0].GetProperty("content").GetString(), Is.EqualTo("usar tokens"));
        });
    }

    [Test]
    public async Task Recovery_VersaoInexistente_ContextNotFound()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await SaveTool(db, config).ExecuteAsync(
            Args("""{"items":[{"kind":"note","content":"n"}]}"""),
            Ctx(), CancellationToken.None);

        var rec = await RecoveryTool(db, config).ExecuteAsync(
            Args("""{"version":99}"""), Ctx(), CancellationToken.None);
        Assert.That(rec.Text, Does.Contain("context_not_found"));
    }

    [Test]
    public async Task Recovery_ChatSemSnapshots_ContextNotFound()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());

        var rec = await RecoveryTool(db, config).ExecuteAsync(
            Args("{}"), Ctx(), CancellationToken.None);
        Assert.That(rec.Text, Does.Contain("context_not_found"));
    }

    [Test]
    public async Task Save_SecretNoConteudo_SecretDetected()
    {
        // AC: "aft_" + 32 hex → secret_detected, nada gravado.
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        var secret = "aft_" + new string('a', 32);

        var res = await SaveTool(db, config).ExecuteAsync(
            Args($$"""{"items":[{"kind":"note","content":"x {{secret}}"}]}"""),
            Ctx(), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(res.Text, Does.Contain("secret_detected"));
            Assert.That(db.ContextSnapshots.Count(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Recovery_BudgetElide_SnapshotIntacto()
    {
        // AC: recovery aplica elision na cópia; snapshot persistido não muda.
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        var big = new string('y', 5_000);
        await SaveTool(db, config).ExecuteAsync(
            Args($$"""
                {"items":[
                {"kind":"decision","content":"d"},
                {"kind":"tool_output","content":"{{big}}"}]}
                """),
            Ctx(), CancellationToken.None);

        var rec = await RecoveryTool(db, config).ExecuteAsync(
            Args("""{"tokenBudget":256}"""), Ctx(), CancellationToken.None);
        var items = ResultJson(rec).GetProperty("items").EnumerateArray().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(items[1].GetProperty("content").GetString(), Does.Contain("elidido"));
            Assert.That(items[1].GetProperty("elided").GetBoolean(), Is.True);
            Assert.That(items[0].GetProperty("content").GetString(), Is.EqualTo("d"));
        });

        var persisted = await db.ContextSnapshots.SingleAsync();
        Assert.That(persisted.PayloadJson, Does.Contain(new string('y', 100)));
    }

    // ---------- context_compact ----------

    [Test]
    public async Task Compact_ExatamenteUmaOrigem()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());

        var sem = await CompactTool(db, config).ExecuteAsync(
            Args("""{"strategy":"none","tokenBudget":256}"""),
            Ctx(), CancellationToken.None);
        var ambas = await CompactTool(db, config).ExecuteAsync(
            Args("""{"items":[{"kind":"note","content":"n"}],"fromVersion":1}"""),
            Ctx(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(sem.Text, Does.Contain("invalid_payload"));
            Assert.That(ambas.Text, Does.Contain("invalid_payload"));
        });
    }

    [Test]
    public async Task Compact_FromVersionInexistente_ContextNotFound()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());

        var res = await CompactTool(db, config).ExecuteAsync(
            Args("""{"fromVersion":7}"""), Ctx(), CancellationToken.None);
        Assert.That(res.Text, Does.Contain("context_not_found"));
    }

    [Test]
    public async Task Compact_SalvaNovaVersaoCompactada()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await SaveTool(db, config).ExecuteAsync(
            Args("""{"items":[{"kind":"note","content":"n"}]}"""),
            Ctx(), CancellationToken.None);

        var res = await CompactTool(db, config).ExecuteAsync(
            Args("""{"fromVersion":1,"strategy":"none","tokenBudget":16000,"save":true}"""),
            Ctx(), CancellationToken.None);
        var payload = ResultJson(res);
        Assert.Multiple(() =>
        {
            Assert.That(payload.GetProperty("savedVersion").GetInt32(), Is.EqualTo(2));
            Assert.That(payload.GetProperty("compacted").GetArrayLength(), Is.EqualTo(1));
        });

        var snaps = await db.ContextSnapshots.OrderBy(s => s.Version).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(snaps, Has.Count.EqualTo(2));
            Assert.That(snaps[1].Compacted, Is.True);
            Assert.That(snaps[1].Strategy, Is.EqualTo("none"));
        });
    }

    [Test]
    public async Task Compact_ElisionSummary_UsaSummarizer()
    {
        // summarizer fake → summaryUsed no stats do payload.
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        var big = new string('p', 9_000);

        var res = await CompactTool(db, config,
                new FakeSummarizer("RESUMO Y")).ExecuteAsync(
            Args($$"""
                {"items":[
                {"kind":"decision","content":"d"},
                {"kind":"passage","content":"{{big}}"}],
                "strategy":"elision+summary","tokenBudget":256}
                """),
            Ctx(), CancellationToken.None);
        var payload = ResultJson(res);
        Assert.Multiple(() =>
        {
            Assert.That(payload.GetProperty("stats")
                .GetProperty("summaryUsed").GetBoolean(), Is.True);
            Assert.That(payload.GetProperty("compacted").EnumerateArray()
                .Any(i => i.GetProperty("kind").GetString() == "summary"), Is.True);
        });
    }

    [Test]
    public async Task Compact_StrategyInvalida_InvalidPayload()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());

        var res = await CompactTool(db, config).ExecuteAsync(
            Args("""{"items":[{"kind":"note","content":"n"}],"strategy":"magic"}"""),
            Ctx(), CancellationToken.None);
        Assert.That(res.Text, Does.Contain("invalid_payload"));
    }

    // ---------- gate / escopo ----------

    [Test]
    public async Task GateDesligado_BloqueiaAsTres()
    {
        // RF-005: contexttools.enabled=false bloqueia execução das 3.
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("contexttools.enabled", false);

        var save = await SaveTool(db, config).ExecuteAsync(
            Args("""{"items":[{"kind":"note","content":"n"}]}"""),
            Ctx(), CancellationToken.None);
        var rec = await RecoveryTool(db, config).ExecuteAsync(
            Args("{}"), Ctx(), CancellationToken.None);
        var compact = await CompactTool(db, config).ExecuteAsync(
            Args("""{"items":[{"kind":"note","content":"n"}]}"""),
            Ctx(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(save.Text, Does.Contain("desabilitadas"));
            Assert.That(rec.Text, Does.Contain("desabilitadas"));
            Assert.That(compact.Text, Does.Contain("desabilitadas"));
            Assert.That(db.ContextSnapshots.Count(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task SemChatPersistido_Indisponivel()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());

        var save = await SaveTool(db, config).ExecuteAsync(
            Args("""{"items":[{"kind":"note","content":"n"}]}"""),
            Ctx(chatId: null), CancellationToken.None);
        Assert.That(save.Text, Does.Contain("requer chat persistido"));
    }

    [Test]
    public void Gate_CobreAsTresTools()
    {
        // O gate de anúncio/execução (IsContextTool) reconhece as 3 tools.
        Assert.Multiple(() =>
        {
            Assert.That(ContextBoundaryService.IsContextTool("context_save"), Is.True);
            Assert.That(ContextBoundaryService.IsContextTool("context_recovery"), Is.True);
            Assert.That(ContextBoundaryService.IsContextTool("context_compact"), Is.True);
        });
    }

    private sealed class FakeSummarizer(string? result) : IContextSummarizer
    {
        public Task<ContextSummary?> SummarizeAsync(
            string model, string transcript, string? userId, CancellationToken ct) =>
            Task.FromResult(result is null ? null : new ContextSummary(result, [], [], []));
    }

    /// <summary>IHostEnvironment mínimo apontando para a raiz temporária.</summary>
    private sealed class TestEnv(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
