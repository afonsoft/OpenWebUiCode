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

    [Test]
    public async Task Execute_MissingRun_Throws()
    {
        var (sp, _, _) = await SeedAsync(cases: 0);
        var runner = new EvalRunnerService(null!,
            NullLogger<EvalRunnerService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.ExecuteEvalRunAsync(
                sp, "missing", CancellationToken.None));
    }

    [Test]
    public async Task Execute_ToolsetMalformed_SemAgent_SemToolIds()
    {
        var db = CreateContext();
        db.Users.Add(new User
        {
            Id = "u1", Name = "u", Email = "u@e.local",
            PasswordHash = "x", Role = "user",
        });
        var ds = new EvalDataset { UserId = "u1", Name = "ds" };
        db.EvalDatasets.Add(ds);
        db.EvalCases.Add(new EvalCase
        {
            DatasetId = ds.Id, Prompt = "p", Expected = "e", CreatedAt = 1,
        });
        // AgentName null → Params null; ToolsetJson malformado → ToolIds null.
        var run = new EvalRun
        {
            DatasetId = ds.Id, UserId = "u1", ModelId = "m",
            AgentName = null, ToolsetJson = "nao-e-json",
            K = 1, Concurrency = 1, Status = "running", TotalJobs = 1,
        };
        db.EvalRuns.Add(run);
        db.ConfigEntries.Add(new ConfigEntry
        {
            Key = EvalRunnerService.CaseTimeoutKey,
            ValueJson = "0.02", UpdatedAt = 1,
        });
        await db.SaveChangesAsync();

        var sp = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(new ConfigService(db, TestCache.Create()))
            .AddSingleton<IChatRunDispatcher>(new NoopDispatcher())
            .BuildServiceProvider();
        var runner = new EvalRunnerService(null!,
            NullLogger<EvalRunnerService>.Instance);

        await runner.ExecuteEvalRunAsync(sp, run.Id, CancellationToken.None);

        await using var check = CreateContext();
        var child = await check.ChatRuns.FirstAsync();
        using var doc = JsonDocument.Parse(child.RequestJson);
        var hasParams = doc.RootElement.TryGetProperty("params", out var p)
            && p.ValueKind != JsonValueKind.Null;
        Assert.That(hasParams, Is.False);
        var hasTools = doc.RootElement.TryGetProperty("toolIds", out var t)
            && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0;
        Assert.That(hasTools, Is.False);
    }

    /// <summary>Dispatcher que completa a run filha com a resposta
    /// esperada — exercita o caminho finished→grade→result pass.</summary>
    private sealed class CompletingDispatcher(AppDbContext db)
        : IChatRunDispatcher
    {
        public void Enqueue(string runId)
        {
            var run = db.ChatRuns.Find(runId)!;
            run.Status = ChatRunStatus.Completed;
            run.PartialContent = "e0";
            run.CompletedAt = 1;
            db.SaveChanges();
        }
    }

    [Test]
    public async Task Execute_CompletedRun_Grades_And_StoresResult()
    {
        var db = CreateContext();
        db.Users.Add(new User
        {
            Id = "u1", Name = "u", Email = "u@e.local",
            PasswordHash = "x", Role = "user",
        });
        var ds = new EvalDataset { UserId = "u1", Name = "ds" };
        db.EvalDatasets.Add(ds);
        db.EvalCases.Add(new EvalCase
        {
            DatasetId = ds.Id, Prompt = "p", Expected = "e0",
            Grader = "exact", CreatedAt = 1,
        });
        var run = new EvalRun
        {
            DatasetId = ds.Id, UserId = "u1", ModelId = "m",
            K = 1, Concurrency = 1, Status = "running", TotalJobs = 1,
        };
        db.EvalRuns.Add(run);
        // Juiz configurado — cobre o branch JudgeModelKey da factory.
        db.ConfigEntries.Add(new ConfigEntry
        {
            Key = EvalRunnerService.JudgeModelKey,
            ValueJson = "\"juiz-m\"", UpdatedAt = 1,
        });
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(new ConfigService(db, TestCache.Create()));
        services.AddSingleton<IChatRunDispatcher>(new CompletingDispatcher(db));
        services.AddLogging();
        services.AddHybridCache();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
            new StubEnv());
        services.AddSingleton<IHttpClientFactory>(new StubHttpFactory());
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddScoped<ProviderService>();
        services.AddScoped<EvalGraderFactory>();
        var sp = services.BuildServiceProvider();

        var runner = new EvalRunnerService(null!,
            NullLogger<EvalRunnerService>.Instance);
        await runner.ExecuteEvalRunAsync(sp, run.Id, CancellationToken.None);

        await using var check = CreateContext();
        var result = await check.EvalResults.FirstAsync();
        Assert.Multiple(() =>
        {
            Assert.That(result.Pass, Is.True);
            Assert.That(result.ChatRunId, Is.Not.Null);
            Assert.That(result.ChatId, Is.Not.Null);
            Assert.That(result.FinalAnswer, Is.EqualTo("e0"));
        });
    }

    [Test]
    public async Task Tick_Recupera_Orfa_E_Processa_Pending()
    {
        var db = CreateContext();
        db.Users.Add(new User
        {
            Id = "u1", Name = "u", Email = "u@e.local",
            PasswordHash = "x", Role = "user",
        });
        var ds = new EvalDataset { UserId = "u1", Name = "ds" };
        db.EvalDatasets.Add(ds);
        db.EvalCases.Add(new EvalCase
        {
            DatasetId = ds.Id, Prompt = "p", Expected = "e", CreatedAt = 1,
        });
        // Órfã: running quando o host "caiu" — RecoverOrphans rebaixa.
        var orphan = new EvalRun
        {
            DatasetId = ds.Id, UserId = "u1", ModelId = "m",
            K = 1, Concurrency = 1, Status = "running", TotalJobs = 1,
        };
        db.EvalRuns.Add(orphan);
        db.ConfigEntries.Add(new ConfigEntry
        {
            Key = EvalRunnerService.CaseTimeoutKey,
            ValueJson = "0.02", UpdatedAt = 1,
        });
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite($"Data Source={_dbPath}"));
        services.AddSingleton(new ConfigService(db, TestCache.Create()));
        services.AddSingleton<IChatRunDispatcher>(new NoopDispatcher());
        services.AddLogging();
        var provider = services.BuildServiceProvider();

        var runner = new EvalRunnerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EvalRunnerService>.Instance);
        await runner.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            string? status = null;
            while (DateTime.UtcNow < deadline)
            {
                await using var check = CreateContext();
                status = (await check.EvalRuns.FindAsync(orphan.Id))!.Status;
                if (status is "done" or "failed")
                {
                    break;
                }
                await Task.Delay(500);
            }
            Assert.That(status, Is.EqualTo("done"),
                "órfã recuperada → pending → executada → done");
            await using var results = CreateContext();
            Assert.That(await results.EvalResults.CountAsync(
                r => r.EvalRunId == orphan.Id), Is.EqualTo(1));
        }
        finally
        {
            await runner.StopAsync(CancellationToken.None);
        }
    }

    private sealed class StubEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider
            ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
