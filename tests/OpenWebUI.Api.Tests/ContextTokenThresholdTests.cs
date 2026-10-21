using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261020-context-snapshots RF-001: a compactação dispara por
/// estimativa de tokens (ceil chars/4) em regra OR com a KV de chars
/// existente — o default novo de compaction:threshold (1M chars) deixa a
/// KV de tokens mandar no disparo normal.
/// </summary>
[TestFixture]
public class ContextTokenThresholdTests
{
    private string _dir = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Join(Path.GetTempPath(), $"owui-tokthr-{Guid.NewGuid():N}");
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

    private static ContextCompactionService NewService(
        AppDbContext db, IContextSummarizer summarizer, ConfigService config) =>
        new(db, config, summarizer,
            new ModelRoleResolver(db, config, _ => Task.FromResult(new List<ModelInfo>()),
                NullLogger<ModelRoleResolver>.Instance),
            NullLogger<ContextCompactionService>.Instance);

    private static List<ChatCompletionMessage> Msgs(int n, int chars) =>
        [.. Enumerable.Range(0, n).Select(i =>
            new ChatCompletionMessage(i % 2 == 0 ? "user" : "assistant", new string('x', chars)))];

    [Test]
    public void EstimateTokens_CeilCharsPor4()
    {
        // RF-001: tokens = ceil(chars/4) — helper compartilhado.
        Assert.Multiple(() =>
        {
            Assert.That(ContextTokens.Estimate(0), Is.EqualTo(0));
            Assert.That(ContextTokens.Estimate(1), Is.EqualTo(1));
            Assert.That(ContextTokens.Estimate(4), Is.EqualTo(1));
            Assert.That(ContextTokens.Estimate(5), Is.EqualTo(2));
            Assert.That(ContextTokens.Estimate(8), Is.EqualTo(2));
            Assert.That(ContextTokens.Estimate(9), Is.EqualTo(3));
        });
    }

    [Test]
    public async Task MaybeCompact_AcimaDoLimiarDeTokens_Compacta()
    {
        // RF-001 / AC: 230k tokens dispara a compactação — aqui com KV
        // pequena pra não montar 1M chars de histórico.
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U", Email = "u@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 10_000_000); // chars fora do caminho
        await config.SetAsync("compaction:threshold_tokens", 32_000); // mínimo da faixa
        await config.SetAsync("compaction:tail_messages", 2);
        var fake = new FakeSummarizer("RESUMO");
        var svc = NewService(db, fake, config);

        // 70 msgs × 2000 chars = 140k chars → 35k tokens ≥ 32k.
        var ok = await svc.MaybeCompactAsync("c1", Msgs(70, 2_000), "m", CancellationToken.None);
        Assert.That(ok, Is.True);
        Assert.That(fake.Calls, Is.EqualTo(1));
    }

    [Test]
    public async Task MaybeCompact_AbaixoDosDoisLimiares_NaoCompacta()
    {
        // AC: 100k tokens e chars abaixo do limiar → sem compactação.
        // Defaults: tokens=224k, chars=1M — 400k chars (100k tokens)
        // fica abaixo dos dois.
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        var fake = new FakeSummarizer("RESUMO");
        var svc = NewService(db, fake, config);

        var ok = await svc.MaybeCompactAsync(
            "c1", Msgs(200, 2_000), "m", CancellationToken.None);
        Assert.That(ok, Is.False);
        Assert.That(fake.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task MaybeCompact_RegraOr_CharsAcimaTokensAbaixo_Compacta()
    {
        // AC: compat — admin configurou limiar de chars baixo; dispara por
        // chars mesmo com tokens abaixo do limiar de tokens.
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U", Email = "u@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 10);
        await config.SetAsync("compaction:threshold_tokens", 1_000_000);
        await config.SetAsync("compaction:tail_messages", 2);
        var fake = new FakeSummarizer("RESUMO");
        var svc = NewService(db, fake, config);

        var ok = await svc.MaybeCompactAsync("c1", Msgs(10, 5), "m", CancellationToken.None);
        Assert.That(ok, Is.True);
    }

    [Test]
    public async Task MaybeCompact_Defaults_500kCharsNaoCompacta()
    {
        // RF-001: com o default novo (threshold=1M chars, tokens=224k),
        // 500k chars (125k tokens) ficam abaixo dos dois — antes do
        // ajuste, o default de 256k chars teria compactado.
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        var fake = new FakeSummarizer("RESUMO");
        var svc = NewService(db, fake, config);

        var ok = await svc.MaybeCompactAsync(
            "c1", Msgs(250, 2_000), "m", CancellationToken.None);
        Assert.That(ok, Is.False);
        Assert.That(fake.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task MaybeCompact_ThresholdTokensClampeado_RespeitaFaixa()
    {
        // RF-007: KV abaixo do mínimo (32_000) é clampeada — um valor
        // absurdo tipo 1 não pode forçar compactação a qualquer turno.
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 10_000_000);
        await config.SetAsync("compaction:threshold_tokens", 1); // clamp → 32_000
        var fake = new FakeSummarizer("RESUMO");
        var svc = NewService(db, fake, config);

        // 1_000 chars → 250 tokens < 32_000 → não compacta.
        var ok = await svc.MaybeCompactAsync("c1", Msgs(10, 100), "m", CancellationToken.None);
        Assert.That(ok, Is.False);
    }

    private sealed class FakeSummarizer(string? result) : IContextSummarizer
    {
        public int Calls { get; private set; }

        public Task<ContextSummary?> SummarizeAsync(
            string model, string transcript, string? userId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result is null ? null
                : new ContextSummary(result, [], [], []));
        }
    }
}
