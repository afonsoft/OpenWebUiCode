using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do runner de evals (SPEC-20261023-agent-evals RF-002):
/// dispatch de caso×rep em chat efêmero, timeout vira falha de caso
/// (nunca derruba o EvalRun), progresso e idempotência na reexecução.
/// </summary>
[TestFixture, IsolateEnvironment]
public class EvalRunnerTests
{
    private string _dbPath = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-evalrun-{Guid.NewGuid():N}.db");
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

    private sealed class NoopDispatcher : IChatRunDispatcher
    {
        public int Enqueued;
        public void Enqueue(string runId) => Enqueued++;
    }

    private async Task<(IServiceProvider sp, NoopDispatcher dispatcher,
        string runId)> SeedAsync(int cases = 2)
    {
        var db = CreateContext();
        db.Users.Add(new User
        {
            Id = "u1", Name = "u", Email = "u@e.local",
            PasswordHash = "x", Role = "user",
        });
        var ds = new EvalDataset { UserId = "u1", Name = "ds" };
        db.EvalDatasets.Add(ds);
        for (var i = 0; i < cases; i++)
        {
            db.EvalCases.Add(new EvalCase
            {
                DatasetId = ds.Id, Prompt = $"p{i}",
                Expected = $"e{i}", CreatedAt = i,
            });
        }
        var run = new EvalRun
        {
            DatasetId = ds.Id, UserId = "u1", ModelId = "m",
            AgentName = "cod", ToolsetJson = """["fs_*"]""",
            K = 1, Concurrency = 1, Status = "running",
            TotalJobs = cases, StartedAt = 1,
        };
        db.EvalRuns.Add(run);
        // Timeout de caso curtíssimo: o dispatcher fake nunca move a run
        // de "queued" → cada caso cai no caminho de timeout em ~1.2s.
        db.ConfigEntries.Add(new ConfigEntry
        {
            Key = EvalRunnerService.CaseTimeoutKey,
            ValueJson = "0.02", UpdatedAt = 1,
        });
        await db.SaveChangesAsync();

        var dispatcher = new NoopDispatcher();
        var sp = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(new ConfigService(db, TestCache.Create()))
            .AddSingleton<IChatRunDispatcher>(dispatcher)
            .BuildServiceProvider();
        return (sp, dispatcher, run.Id);
    }

    [Test]
    public async Task Execute_CaseTimeout_FailsCaseNotRun()
    {
        var (sp, dispatcher, runId) = await SeedAsync(cases: 2);
        var runner = new EvalRunnerService(null!,
            NullLogger<EvalRunnerService>.Instance);

        await runner.ExecuteEvalRunAsync(sp, runId, CancellationToken.None);

        await using var db = CreateContext();
        var results = await db.EvalResults
            .Where(r => r.EvalRunId == runId).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(2));
            Assert.That(results.All(r => !r.Pass), Is.True);
            Assert.That(
                results.All(r => r.GraderDetailJson!.Contains("timeout")),
                Is.True);
            Assert.That(dispatcher.Enqueued, Is.EqualTo(2));
        });
        // Chats efêmeros + runs filhas foram criados para cada caso.
        Assert.That(await db.Chats.CountAsync(c => c.IsEphemeral),
            Is.EqualTo(2));
        var done = await db.EvalRuns.FindAsync(runId);
        Assert.That(done!.DoneJobs, Is.EqualTo(2));
    }

    [Test]
    public async Task Execute_Rerun_ReplacesResults()
    {
        var (sp, _, runId) = await SeedAsync(cases: 1);
        var runner = new EvalRunnerService(null!,
            NullLogger<EvalRunnerService>.Instance);

        await runner.ExecuteEvalRunAsync(sp, runId, CancellationToken.None);
        await runner.ExecuteEvalRunAsync(sp, runId, CancellationToken.None);

        await using var db = CreateContext();
        Assert.That(await db.EvalResults.CountAsync(
            r => r.EvalRunId == runId), Is.EqualTo(1));
    }
}
