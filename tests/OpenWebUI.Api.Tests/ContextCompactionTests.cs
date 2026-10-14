using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261010-context-compaction: split sem quebrar grupo de tools, aplicação
/// do kv persistido, compactação completa com summarizer fake e integração do
/// executor (histórico vira [resumo] + tail).
/// </summary>
[TestFixture]
public class ContextCompactionTests
{
    private string _dir = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"owui-compact-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "t.db");
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

    private static List<ChatCompletionMessage> Msgs(int n, int chars = 10) =>
        [.. Enumerable.Range(0, n).Select(i =>
            new ChatCompletionMessage(i % 2 == 0 ? "user" : "assistant", new string('x', chars)))];

    [Test]
    public void SplitIndex_NaoCortaDentroDeGrupoDeTools()
    {
        // Tail acaba começando num tool → SplitIndex avança além do grupo.
        var history = new List<ChatCompletionMessage>
        {
            new("user", "u1"),
            new("assistant", "", ToolCallsJson: "[{}]"),
            new("tool", "r1"),
            new("tool", "r2"),
            new("assistant", "a2"),
            new("user", "u2"),
        };
        var t = new ContextCompactionService.Thresholds(1, 3, 1_000);
        var idx = ContextCompactionService.SplitIndex(history, t);
        // 3 últimas = tool/tool/assistant... → idx pousa na tool r1, avança p/ assistant "a2".
        Assert.That(history[idx].Role, Is.Not.EqualTo("tool"));
        Assert.That(idx, Is.EqualTo(4));
    }

    [Test]
    public async Task ApplyAsync_SemKvDevolveHistoricoInteiro()
    {
        await using var db = NewDb();
        var svc = NewService(db, new FakeSummarizer("x"));
        var history = Msgs(4);
        var result = await svc.ApplyAsync("c1", history, CancellationToken.None);
        Assert.That(result, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task ApplyAsync_ComKvDevolveResumoMaisTail()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("chat:c1:compaction",
            new { cutoff = 2, summary = "RESUMO ANTIGO" });
        var svc = NewService(db, new FakeSummarizer("x"));
        var result = await svc.ApplyAsync("c1", Msgs(6), CancellationToken.None);
        Assert.That(result, Has.Count.EqualTo(5)); // 1 resumo + 4 do tail
        Assert.That(result[0].Role, Is.EqualTo("user"));
        Assert.That(result[0].Content,
            Does.Contain(ContextCompactionService.SummaryPrefix.Trim()));
        Assert.That(result[0].Content, Does.Contain("RESUMO ANTIGO"));
    }

    [Test]
    public async Task ApplyAsync_CutoffForaDoRangeDevolveInteiro()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("chat:c1:compaction",
            new { cutoff = 10, summary = "s" });
        var svc = NewService(db, new FakeSummarizer("x"));
        Assert.That(await svc.ApplyAsync("c1", Msgs(3), CancellationToken.None),
            Has.Count.EqualTo(3));
    }

    [Test]
    public async Task MaybeCompact_AbaixoDoLimiarNaoCompacta()
    {
        await using var db = NewDb();
        var fake = new FakeSummarizer("RESUMO");
        var svc = NewService(db, fake);
        Assert.That(await svc.MaybeCompactAsync("c1", Msgs(4), "m", CancellationToken.None),
            Is.False);
        Assert.That(fake.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task MaybeCompact_AcimaDoLimiarGravaKvEMarcador()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        // Limiar baixo pra forçar compactação com poucas msgs.
        await config.SetAsync("compaction:threshold", 10);
        await config.SetAsync("compaction:tail_messages", 2);
        var fake = new FakeSummarizer("RESUMO GERADO");
        var svc = NewService(db, fake, config);

        var ok = await svc.MaybeCompactAsync("c1", Msgs(10, 5), "m", CancellationToken.None);
        Assert.That(ok, Is.True);
        Assert.That(fake.Calls, Is.EqualTo(1));

        // kv gravado com cutoff + summary.
        var kv = await config.GetAsync<System.Text.Json.JsonElement>(
            "chat:c1:compaction", default);
        Assert.That(kv.GetProperty("summary").GetString(), Is.EqualTo("RESUMO GERADO"));
        Assert.That(kv.GetProperty("cutoff").GetInt32(), Is.GreaterThan(0));

        // Marcador persistido no chat.
        await using var db2 = NewDb();
        var marker = await db2.ChatMessages
            .Where(m => m.ChatId == "c1" && m.Role == "compaction")
            .SingleOrDefaultAsync();
        Assert.That(marker, Is.Not.Null);
        Assert.That(marker!.Content, Is.EqualTo("RESUMO GERADO"));

        // Aplicação subsequente devolve resumo + tail — nunca o head inteiro.
        var applied = await svc.ApplyAsync("c1", Msgs(10, 5), CancellationToken.None);
        Assert.That(applied[0].Content, Does.Contain("RESUMO GERADO"));
        Assert.That(applied.Count, Is.LessThan(10));
    }

    [Test]
    public async Task MaybeCompact_SummarizerNullNaoQuebra()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 10);
        var svc = NewService(db, new FakeSummarizer(null), config);
        Assert.That(await svc.MaybeCompactAsync("c1", Msgs(10, 5), "m", CancellationToken.None),
            Is.False);
    }

    [Test]
    public async Task MaybeCompact_SummarizerLancaRunSegue()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 10);
        var svc = NewService(db, new ThrowingSummarizer(), config);
        Assert.That(await svc.MaybeCompactAsync("c1", Msgs(10, 5), "m", CancellationToken.None),
            Is.False);
    }

    private static ContextCompactionService NewService(
        AppDbContext db, IContextSummarizer summarizer, ConfigService? config = null) =>
        new(db,
            config ?? new ConfigService(db, TestCache.Create()),
            summarizer,
            new ModelRoleResolver(db, _ => Task.FromResult(new List<ModelInfo>()),
                NullLogger<ModelRoleResolver>.Instance),
            NullLogger<ContextCompactionService>.Instance);

    private sealed class FakeSummarizer(string? result) : IContextSummarizer
    {
        public int Calls { get; private set; }

        public Task<string?> SummarizeAsync(string model, string transcript, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingSummarizer : IContextSummarizer
    {
        public Task<string?> SummarizeAsync(string model, string transcript, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }
}
