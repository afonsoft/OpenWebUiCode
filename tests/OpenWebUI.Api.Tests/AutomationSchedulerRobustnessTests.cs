using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da SPEC-20261115-scheduler-robustness: retry com backoff,
/// circuit breaker (ConsecutiveFailures → Enabled=false), jitter do
/// NextRunAt, threshold de "missed" e validação do MisfirePolicy.
/// Fixture espelha <see cref="AutomationCallersTests"/> (sqlite temp).
/// </summary>
[TestFixture]
public class AutomationSchedulerRobustnessTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-sched-{Guid.NewGuid():N}.db");
        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@c.local", Role = "user" });
        db.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private static Automation NewAutomation(
        string actionKind = "webhook", string? actionJson = null) =>
        new()
        {
            UserId = "u1",
            Name = "auto-teste",
            Prompt = "diga olá",
            ModelId = "fake:1",
            ScheduleKind = "interval",
            IntervalMinutes = 5,
            ActionKind = actionKind,
            ActionJson = actionJson,
        };

    private static async Task<Automation> PersistAsync(AppDbContext db, Automation automation)
    {
        db.Automations.Add(automation);
        await db.SaveChangesAsync();
        return automation;
    }

    private static AutomationService NewService(
        AppDbContext db, IHttpClientFactory? httpFactory = null,
        IConfiguration? config = null) =>
        new(db,
            new ProviderService(new FailFactory(), new ConfigService(db, TestCache.Create()),
                NullLogger<ProviderService>.Instance, TestCache.Create(),
                new ConfigurationBuilder().Build()),
            new NotificationService(new FailFactory(), db, NullLogger<NotificationService>.Instance),
            httpFactory: httpFactory, config: config)
        {
            // Backoff real (1s→4s) não roda em teste.
            RetryDelayAsync = (_, _) => Task.CompletedTask,
        };

    // ---------- RF-003: retry transitório ----------

    [Test]
    public async Task Retry_TransientFailure_SucceedsOnThirdAttempt()
    {
        var handler = new SequenceHandler(
            HttpStatusCode.InternalServerError,
            HttpStatusCode.BadGateway,
            HttpStatusCode.OK);
        await using var db = NewDb();
        var service = NewService(db, new StubFactory(handler));

        var run = await service.RunNowAsync(
            await PersistAsync(db, NewAutomation("webhook",
                "{\"url\":\"https://api.exemplo.com/hook\"}")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("ok"), run.Error);
            Assert.That(handler.Calls, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Retry_AllTransient_ExhaustsAttemptsAndFails()
    {
        var handler = new SequenceHandler(HttpStatusCode.ServiceUnavailable);
        await using var db = NewDb();
        var service = NewService(db, new StubFactory(handler));

        var run = await service.RunNowAsync(
            await PersistAsync(db, NewAutomation("webhook",
                "{\"url\":\"https://api.exemplo.com/hook\"}")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("failed"));
            Assert.That(run.Error, Does.Contain("503"));
            Assert.That(handler.Calls, Is.EqualTo(AutomationService.MaxTransientAttempts));
        });
    }

    [Test]
    public async Task Retry_NonTransient400_DoesNotRetry()
    {
        var handler = new SequenceHandler(HttpStatusCode.BadRequest);
        await using var db = NewDb();
        var service = NewService(db, new StubFactory(handler));

        var run = await service.RunNowAsync(
            await PersistAsync(db, NewAutomation("webhook",
                "{\"url\":\"https://api.exemplo.com/hook\"}")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("failed"));
            Assert.That(handler.Calls, Is.EqualTo(1));
        });
    }

    // ---------- RF-003: circuit breaker ----------

    [Test]
    public async Task CircuitBreaker_ThresholdReached_DisablesAutomation()
    {
        var handler = new SequenceHandler(HttpStatusCode.InternalServerError);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Automation:CircuitBreakerThreshold"] = "3",
            }).Build();
        await using var db = NewDb();
        var service = NewService(db, new StubFactory(handler), config);
        var automation = await PersistAsync(db, NewAutomation("webhook",
            "{\"url\":\"https://api.exemplo.com/hook\"}"));

        for (var i = 0; i < 3; i++)
        {
            await service.RunNowAsync(automation);
        }

        Assert.Multiple(() =>
        {
            Assert.That(automation.ConsecutiveFailures, Is.EqualTo(3));
            Assert.That(automation.Enabled, Is.False);
            Assert.That(automation.NextRunAt, Is.Null);
        });
    }

    [Test]
    public async Task CircuitBreaker_Success_ResetsCounter()
    {
        // 1ª run: 3 tentativas 500 → failed, contador=1.
        // 2ª run: 200 → ok, contador volta a 0.
        var handler = new SequenceHandler(HttpStatusCode.InternalServerError);
        await using var db = NewDb();
        var service = NewService(db, new StubFactory(handler));
        var automation = await PersistAsync(db, NewAutomation("webhook",
            "{\"url\":\"https://api.exemplo.com/hook\"}"));

        await service.RunNowAsync(automation);
        Assert.That(automation.ConsecutiveFailures, Is.EqualTo(1));

        handler.Enqueue(HttpStatusCode.OK);
        await service.RunNowAsync(automation);
        Assert.That(automation.ConsecutiveFailures, Is.EqualTo(0));
    }

    // ---------- RF-005: jitter ----------

    [Test]
    public void Jitter_StaysWithinPlusMinus5Pct()
    {
        var now = DateTimeOffset.UtcNow;
        var baseNext = now.ToUnixTimeSeconds() + 3600;
        for (var i = 0; i < 200; i++)
        {
            var jittered = AutomationService.ApplyJitter(baseNext, now, "interval");
            Assert.That(jittered, Is.Not.Null);
            var delta = jittered!.Value - baseNext;
            Assert.That(Math.Abs(delta), Is.LessThanOrEqualTo(3600 * 0.05 + 2),
                $"jitter fora do range: {delta}");
        }
    }

    [Test]
    public void Jitter_OnceAndNull_AreUntouched()
    {
        var now = DateTimeOffset.UtcNow;
        var baseNext = now.ToUnixTimeSeconds() + 3600;
        Assert.Multiple(() =>
        {
            Assert.That(AutomationService.ApplyJitter(baseNext, now, "once"),
                Is.EqualTo(baseNext));
            Assert.That(AutomationService.ApplyJitter(null, now, "interval"),
                Is.Null);
        });
    }

    // ---------- RF-002: threshold de missed ----------

    [Test]
    public void MissedThreshold_IntervalVsDaily()
    {
        var interval = NewAutomation();
        interval.IntervalMinutes = 5;
        var daily = NewAutomation();
        daily.ScheduleKind = "daily";

        Assert.Multiple(() =>
        {
            Assert.That(AutomationScheduler.MissedThresholdSeconds(interval),
                Is.EqualTo(600));
            Assert.That(AutomationScheduler.MissedThresholdSeconds(daily),
                Is.EqualTo(86400));
        });
    }

    // ---------- fakes ----------

    /// <summary>Responde na sequência configurada; repete a última resposta.</summary>
    private sealed class SequenceHandler(params HttpStatusCode[] statuses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _queue = new(statuses);
        private HttpStatusCode _last = statuses[^1];

        public int Calls { get; private set; }

        public void Enqueue(HttpStatusCode status) => _queue.Enqueue(status);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            _last = _queue.Count > 0 ? _queue.Dequeue() : _last;
            return Task.FromResult(new HttpResponseMessage(_last)
            {
                Content = new StringContent("seq", Encoding.UTF8, "text/plain"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class FailFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FailHandler());
    }

    private sealed class FailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }
}
