using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da SPEC-20261115-scheduler-advanced: TimeZoneId (IANA) para
/// daily/weekly, ScheduleKind cron via Cronos e ActionKind run — ChatRun
/// real enfileirada com ApprovalPreset no chat.
/// </summary>
[TestFixture]
public class AutomationScheduleAdvancedTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-adv-{Guid.NewGuid():N}.db");
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

    private static Automation NewAutomation(string actionKind = "run") =>
        new()
        {
            UserId = "u1",
            Name = "auto-adv",
            Prompt = "faça algo",
            ModelId = "fake:1",
            ScheduleKind = "interval",
            IntervalMinutes = 5,
            ActionKind = actionKind,
        };

    private static AutomationService NewService(AppDbContext db, IChatRunDispatcher? dispatcher = null) =>
        new(db,
            new ProviderService(new FailFactory(), new ConfigService(db, TestCache.Create()),
                NullLogger<ProviderService>.Instance, TestCache.Create(),
                new ConfigurationBuilder().Build()),
            new NotificationService(new FailFactory(), db, NullLogger<NotificationService>.Instance),
            dispatcher: dispatcher)
        {
            RetryDelayAsync = (_, _) => Task.CompletedTask,
        };

    // ---------- RF-001: timezone ----------

    [Test]
    public void Daily_SaoPaulo_ConvertsToUtc()
    {
        // Seg 2026-11-16 00:00 UTC = dom 21:00 em São Paulo (UTC-3, sem DST).
        // Próxima daily 09:00 local = 12:00 UTC do mesmo dia.
        var automation = NewAutomation();
        automation.ScheduleKind = "daily";
        automation.TimeOfDay = "09:00";
        automation.TimeZoneId = "America/Sao_Paulo";

        var now = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        var expected = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        Assert.That(AutomationSchedule.ComputeNextRun(automation, now),
            Is.EqualTo(expected.ToUnixTimeSeconds()));
    }

    [Test]
    public void Daily_InvalidTimeZone_FallsBackToUtc()
    {
        var automation = NewAutomation();
        automation.ScheduleKind = "daily";
        automation.TimeOfDay = "09:00";
        automation.TimeZoneId = "Planeta/Mars";

        var now = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        var expected = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

        Assert.That(AutomationSchedule.ComputeNextRun(automation, now),
            Is.EqualTo(expected.ToUnixTimeSeconds()));
    }

    [Test]
    public void Interval_IgnoresTimeZone()
    {
        var automation = NewAutomation();
        automation.IntervalMinutes = 60;
        automation.TimeZoneId = "America/Sao_Paulo";

        var now = new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        var expected = now.AddMinutes(60);

        Assert.That(AutomationSchedule.ComputeNextRun(automation, now),
            Is.EqualTo(expected.ToUnixTimeSeconds()));
    }

    // ---------- RF-002: cron ----------

    [Test]
    public void Cron_Valid_SchedulesNext()
    {
        var automation = NewAutomation();
        automation.ScheduleKind = "cron";
        automation.CronExpression = "*/15 * * * *";

        var now = new DateTimeOffset(2026, 11, 16, 10, 7, 0, TimeSpan.Zero);
        var expected = new DateTimeOffset(2026, 11, 16, 10, 15, 0, TimeSpan.Zero);

        Assert.That(AutomationSchedule.ComputeNextRun(automation, now),
            Is.EqualTo(expected.ToUnixTimeSeconds()));
    }

    [Test]
    public void Cron_Invalid_ReturnsNull()
    {
        var automation = NewAutomation();
        automation.ScheduleKind = "cron";
        automation.CronExpression = "not-a-cron";

        Assert.That(AutomationSchedule.ComputeNextRun(automation, DateTimeOffset.UtcNow),
            Is.Null);
    }

    // ---------- RF-003: ActionKind run ----------

    [Test]
    public async Task RunAction_CreatesQueuedChatRun()
    {
        await using var db = NewDb();
        var dispatcher = new FakeDispatcher();
        var service = NewService(db, dispatcher);
        var automation = NewAutomation("run");
        db.Automations.Add(automation);
        await db.SaveChangesAsync();

        var run = await service.RunNowAsync(automation);

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("ok"), run.Error);
            Assert.That(run.ChatId, Is.Not.Null);
            Assert.That(run.RunId, Is.Not.Null);
            Assert.That(dispatcher.Enqueued, Has.Count.EqualTo(1));
            Assert.That(dispatcher.Enqueued[0], Is.EqualTo(run.RunId));
        });

        var chatRun = await db.ChatRuns.FindAsync(run.RunId);
        Assert.Multiple(() =>
        {
            Assert.That(chatRun, Is.Not.Null);
            Assert.That(chatRun!.Status, Is.EqualTo(ChatRunStatus.Queued));
            Assert.That(chatRun.UserId, Is.EqualTo("u1"));
        });
    }

    [Test]
    public async Task RunAction_ApprovalPreset_AppliedToChat()
    {
        await using var db = NewDb();
        var dispatcher = new FakeDispatcher();
        var service = NewService(db, dispatcher);
        var automation = NewAutomation("run");
        automation.ApprovalPreset = "approve-mutations";
        db.Automations.Add(automation);
        await db.SaveChangesAsync();

        var run = await service.RunNowAsync(automation);
        var chat = await db.Chats.FindAsync(run.ChatId);

        Assert.That(chat!.ApprovalPreset, Is.EqualTo("approve-mutations"));
    }

    [Test]
    public async Task RunAction_FullQueue_MarksRunFailed()
    {
        await using var db = NewDb();
        var dispatcher = new FakeDispatcher { Accept = false };
        var service = NewService(db, dispatcher);
        var automation = NewAutomation("run");
        db.Automations.Add(automation);
        await db.SaveChangesAsync();

        var run = await service.RunNowAsync(automation);

        Assert.That(run.Status, Is.EqualTo("failed"));
    }

    // ---------- RF-002: validação no endpoint ----------

    private static AutomationUpsertRequest NewRequest(string kind, string? cron = null,
        string? tz = null, string? preset = null) =>
        new("auto", "prompt", "fake:1", kind, 60, "09:00", 1, true,
            ActionKind: "run", CronExpression: cron, TimeZoneId: tz,
            ApprovalPreset: preset);

    [Test]
    public void Validate_Cron_Invalid_Rejected()
    {
        Assert.That(OpenWebUI.Api.Endpoints.AutomationEndpoints.Validate(
            NewRequest("cron", "not-a-cron")), Is.Not.Null);
    }

    [Test]
    public void Validate_Cron_Valid_Accepted()
    {
        Assert.That(OpenWebUI.Api.Endpoints.AutomationEndpoints.Validate(
            NewRequest("cron", "*/15 * * * *")), Is.Null);
    }

    [Test]
    public void Validate_BadTimeZone_Rejected()
    {
        Assert.That(OpenWebUI.Api.Endpoints.AutomationEndpoints.Validate(
            NewRequest("daily", tz: "Planeta/Mars")), Is.Not.Null);
    }

    [Test]
    public void Validate_BadApprovalPreset_Rejected()
    {
        Assert.That(OpenWebUI.Api.Endpoints.AutomationEndpoints.Validate(
            NewRequest("cron", "*/15 * * * *", preset: "superuser")), Is.Not.Null);
    }

    [Test]
    public void ValidateAction_Run_AllowedWithoutJson()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AutomationService.ActionKinds, Does.Contain("run"));
            Assert.That(AutomationService.ValidateAction("run", null), Is.Null);
        });
    }

    // ---------- fakes ----------

    private sealed class FakeDispatcher : IChatRunDispatcher
    {
        public List<string> Enqueued { get; } = [];
        public bool Accept { get; set; } = true;

        public bool Enqueue(string runId)
        {
            Enqueued.Add(runId);
            return Accept;
        }
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
