using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do ledger de uso (SPEC-20261023-run-cost-budgets RF-001):
/// escrita idempotente por RunId, agregação mensal UTC, cadeia por
/// ParentRunId e preservação de CostUsd=null quando não reportado.
/// </summary>
[TestFixture, IsolateEnvironment]
public class RunUsageLedgerTests
{
    private string _dbPath = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-ledger-{Guid.NewGuid():N}.db");
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private static RunUsageLedgerService Ledger(AppDbContext db) =>
        new(db, NullLogger<RunUsageLedgerService>.Instance);

    // Covers RF-001: um record por run, idempotente por RunId.
    [Test]
    public async Task RecordAsync_Idempotent_DoesNotDuplicateRun()
    {
        await using var db = CreateContext();
        var ledger = Ledger(db);

        await ledger.RecordAsync(new RunUsageLedgerService.UsageEntry("run-1", "chat-1", "user-1", null, "model-a", 3, 100, 50, 10, 0.01), CancellationToken.None);
        await ledger.RecordAsync(new RunUsageLedgerService.UsageEntry("run-1", "chat-1", "user-1", null, "model-a", 3, 100, 50, 10, 0.01), CancellationToken.None);

        Assert.That(await db.RunUsageRecords.CountAsync(
            r => r.RunId == "run-1"), Is.EqualTo(1));
    }

    // Covers RF-001: run sem usage reportado grava tokens=0 e CostUsd=null
    // — nunca estimado.
    [Test]
    public async Task RecordAsync_NoUsage_StoresNullCost()
    {
        await using var db = CreateContext();
        await Ledger(db).RecordAsync(new RunUsageLedgerService.UsageEntry("run-2", "chat-1", "user-1", null, "local-model", 0, 0, 0, 0, null), CancellationToken.None);

        var row = await db.RunUsageRecords.SingleAsync(
            r => r.RunId == "run-2");
        Assert.Multiple(() =>
        {
            Assert.That(row.PromptTokens, Is.EqualTo(0));
            Assert.That(row.CompletionTokens, Is.EqualTo(0));
            Assert.That(row.CostUsd, Is.Null);
        });
    }

    // Covers RF-002: agregação do mês UTC ignora linhas fora da janela.
    [Test]
    public async Task MonthTotalsAsync_SumsCurrentMonthOnly()
    {
        await using var db = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0,
            TimeSpan.Zero).ToUnixTimeSeconds();
        db.RunUsageRecords.AddRange(
            new RunUsageRecord
            {
                RunId = "in", ChatId = "c", UserId = "u", Steps = 1,
                PromptTokens = 100, CompletionTokens = 50,
                ReasoningTokens = 10, CostUsd = 0.5, CreatedAt = start + 60,
            },
            new RunUsageRecord
            {
                RunId = "out", ChatId = "c", UserId = "u", Steps = 1,
                PromptTokens = 999, CompletionTokens = 999, CostUsd = 9.0,
                CreatedAt = start - 60,
            });
        await db.SaveChangesAsync();

        var totals = await Ledger(db).MonthTotalsAsync(
            "u", now, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(totals.PromptTokens, Is.EqualTo(100));
            Assert.That(totals.CompletionTokens, Is.EqualTo(50));
            Assert.That(totals.CostUsd, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(totals.Runs, Is.EqualTo(1));
        });
    }

    // Covers RF-003: custo dos descendentes conta na cadeia do pai
    // (ParentRunId walk — filho e neto somam, run de outra árvore não).
    [Test]
    public async Task ChainCostUsdAsync_SumsDescendants()
    {
        await using var db = CreateContext();
        db.RunUsageRecords.AddRange(
            new RunUsageRecord
            {
                RunId = "root", ChatId = "c", UserId = "u",
                CostUsd = 1.0, CreatedAt = 1,
            },
            new RunUsageRecord
            {
                RunId = "child", ChatId = "c2", UserId = "u",
                ParentRunId = "root", CostUsd = 0.30, CreatedAt = 2,
            },
            new RunUsageRecord
            {
                RunId = "grand", ChatId = "c3", UserId = "u",
                ParentRunId = "child", CostUsd = 0.20, CreatedAt = 3,
            },
            new RunUsageRecord
            {
                RunId = "other", ChatId = "c4", UserId = "u",
                CostUsd = 99.0, CreatedAt = 4,
            });
        await db.SaveChangesAsync();

        var chain = await Ledger(db).ChainCostUsdAsync(
            "root", CancellationToken.None);

        Assert.That(chain, Is.EqualTo(1.5).Within(1e-9));
    }
}
