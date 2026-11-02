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

    // Covers superfície admin: query por janela com filtro de usuário e
    // ordenação por criação descendente.
    [Test]
    public async Task QueryAsync_Filtra_Janela_E_Usuario()
    {
        await using var db = CreateContext();
        db.RunUsageRecords.AddRange(
            new RunUsageRecord
            {
                RunId = "a", ChatId = "c", UserId = "u1", CreatedAt = 100,
                CostUsd = 1,
            },
            new RunUsageRecord
            {
                RunId = "b", ChatId = "c", UserId = "u2", CreatedAt = 200,
                CostUsd = 2,
            },
            new RunUsageRecord
            {
                RunId = "c", ChatId = "c", UserId = "u1", CreatedAt = 300,
                CostUsd = 3,
            });
        await db.SaveChangesAsync();

        var all = await Ledger(db).QueryAsync(
            null, 0, 250, CancellationToken.None);
        Assert.That(all.Select(r => r.RunId),
            Is.EqualTo(new[] { "b", "a" }));
        var u1 = await Ledger(db).QueryAsync(
            "u1", 0, 400, CancellationToken.None);
        Assert.That(u1.Select(r => r.RunId),
            Is.EqualTo(new[] { "c", "a" }));
    }

    // Covers superfície admin: top chats agrega custo por (chat, user) na
    // janela e ordena pelo total.
    [Test]
    public async Task TopChatsAsync_Agrega_E_Ordena_Por_Custo()
    {
        await using var db = CreateContext();
        db.RunUsageRecords.AddRange(
            new RunUsageRecord
            {
                RunId = "a", ChatId = "cheap", UserId = "u", CreatedAt = 10,
                CostUsd = 0.1,
            },
            new RunUsageRecord
            {
                RunId = "b", ChatId = "cheap", UserId = "u", CreatedAt = 20,
                CostUsd = 0.2,
            },
            new RunUsageRecord
            {
                RunId = "c", ChatId = "dear", UserId = "u", CreatedAt = 30,
                CostUsd = 5,
            });
        await db.SaveChangesAsync();

        var top = await Ledger(db).TopChatsAsync(
            0, 100, 10, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(top[0].ChatId, Is.EqualTo("dear"));
            Assert.That(top[0].CostUsd, Is.EqualTo(5).Within(1e-9));
            Assert.That(top[1].ChatId, Is.EqualTo("cheap"));
            Assert.That(top[1].CostUsd, Is.EqualTo(0.3).Within(1e-9));
        });
    }

    // Covers SPEC-20261102 RF-004: top chats carrega tokens totais além
    // do custo (ranking por custo, coluna de tokens na UI).
    [Test]
    public async Task TopChatsAsync_Retorna_Tokens_Totais()
    {
        await using var db = CreateContext();
        db.RunUsageRecords.Add(
            new RunUsageRecord
            {
                RunId = "a", ChatId = "c1", UserId = "u", CreatedAt = 10,
                PromptTokens = 100, CompletionTokens = 50,
                ReasoningTokens = 10, CostUsd = 0.1,
            });
        await db.SaveChangesAsync();

        var top = await Ledger(db).TopChatsAsync(
            0, 100, 10, null, CancellationToken.None);

        Assert.That(top[0].Tokens, Is.EqualTo(160));
    }

    // Covers SPEC-20261102 RF-001: série diária agrega por dia UTC na
    // janela, em ordem cronológica.
    [Test]
    public async Task ByDayAsync_Agrega_Por_Dia_Utc()
    {
        await using var db = CreateContext();
        var day1 = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero)
            .ToUnixTimeSeconds();
        var day2 = new DateTimeOffset(2026, 10, 21, 3, 0, 0, TimeSpan.Zero)
            .ToUnixTimeSeconds();
        db.RunUsageRecords.AddRange(
            new RunUsageRecord
            {
                RunId = "a", ChatId = "c", UserId = "u", CreatedAt = day1,
                PromptTokens = 100, CompletionTokens = 50,
                ReasoningTokens = 10, CostUsd = 0.1,
            },
            new RunUsageRecord
            {
                RunId = "b", ChatId = "c", UserId = "u", CreatedAt = day1 + 60,
                PromptTokens = 200, CompletionTokens = 100, CostUsd = 0.2,
            },
            new RunUsageRecord
            {
                RunId = "c", ChatId = "c", UserId = "u", CreatedAt = day2,
                PromptTokens = 300, CompletionTokens = 150,
                ReasoningTokens = 5, CostUsd = 0.3,
            });
        await db.SaveChangesAsync();

        var series = await Ledger(db).ByDayAsync(
            day1 - 1, day2 + 86400, null, CancellationToken.None);

        Assert.That(series, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(series[0].Day, Is.EqualTo("2026-10-20"));
            Assert.That(series[0].PromptTokens, Is.EqualTo(300));
            Assert.That(series[0].CompletionTokens, Is.EqualTo(150));
            Assert.That(series[0].ReasoningTokens, Is.EqualTo(10));
            Assert.That(series[0].CostUsd, Is.EqualTo(0.3).Within(1e-9));
            Assert.That(series[1].Day, Is.EqualTo("2026-10-21"));
            Assert.That(series[1].PromptTokens, Is.EqualTo(300));
        });
    }

    // Covers SPEC-20261102 RF-002: agregado por modelo ordena por tokens
    // desc e agrupa ModelId nulo como "unknown".
    [Test]
    public async Task ByModelAsync_Agrega_E_Null_Vira_Unknown()
    {
        await using var db = CreateContext();
        db.RunUsageRecords.AddRange(
            new RunUsageRecord
            {
                RunId = "a", ChatId = "c", UserId = "u", CreatedAt = 10,
                ModelId = "gpt-4o", PromptTokens = 100,
                CompletionTokens = 50, CostUsd = 0.1,
            },
            new RunUsageRecord
            {
                RunId = "b", ChatId = "c", UserId = "u", CreatedAt = 20,
                ModelId = "gpt-4o", PromptTokens = 900,
                CompletionTokens = 100, ReasoningTokens = 10, CostUsd = 0.4,
            },
            new RunUsageRecord
            {
                RunId = "c", ChatId = "c", UserId = "u", CreatedAt = 30,
                ModelId = null, PromptTokens = 500, CostUsd = null,
            });
        await db.SaveChangesAsync();

        var models = await Ledger(db).ByModelAsync(
            0, 100, 20, null, CancellationToken.None);

        Assert.That(models, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(models[0].ModelId, Is.EqualTo("gpt-4o"));
            Assert.That(models[0].Runs, Is.EqualTo(2));
            Assert.That(models[0].Tokens, Is.EqualTo(1160));
            Assert.That(models[0].CostUsd, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(models[1].ModelId, Is.EqualTo("unknown"));
            Assert.That(models[1].CostUsd, Is.EqualTo(0).Within(1e-9));
        });
    }

    // Covers SPEC-20261102 RF-003: agregado por usuário expõe split
    // prompt/completion/reasoning e soma runs filhas (mesmo UserId) no
    // dono — delegate_task herda o UserId da raiz.
    [Test]
    public async Task ByUserAsync_Split_Tokens_E_Filhas_Somam()
    {
        await using var db = CreateContext();
        db.RunUsageRecords.AddRange(
            new RunUsageRecord
            {
                RunId = "root", ChatId = "c", UserId = "u1", CreatedAt = 10,
                PromptTokens = 100, CompletionTokens = 50,
                ReasoningTokens = 10, CostUsd = 0.2,
            },
            new RunUsageRecord
            {
                RunId = "child", ChatId = "c", UserId = "u1",
                ParentRunId = "root", CreatedAt = 20,
                PromptTokens = 80, CompletionTokens = 20, CostUsd = 0.1,
            },
            new RunUsageRecord
            {
                RunId = "other", ChatId = "c", UserId = "u2", CreatedAt = 30,
                PromptTokens = 5, CompletionTokens = 5, CostUsd = 0.01,
            });
        await db.SaveChangesAsync();

        var users = await Ledger(db).ByUserAsync(
            0, 100, null, CancellationToken.None);

        Assert.That(users, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(users[0].UserId, Is.EqualTo("u1"));
            Assert.That(users[0].PromptTokens, Is.EqualTo(180));
            Assert.That(users[0].CompletionTokens, Is.EqualTo(70));
            Assert.That(users[0].ReasoningTokens, Is.EqualTo(10));
            Assert.That(users[0].Tokens, Is.EqualTo(260));
            Assert.That(users[0].Runs, Is.EqualTo(2));
            Assert.That(users[0].CostUsd, Is.EqualTo(0.3).Within(1e-9));
        });
    }
}
