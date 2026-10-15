using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// CRUD das tools <c>routine</c>/<c>reminder</c> (SPEC-20261010-agent-routines):
/// listagem por dono, create com todos os schedule kinds e validações,
/// update parcial, delete/enable/disable/run_now e o lembrete único.
/// </summary>
[TestFixture]
public class RoutineToolTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"owui-routine-{Guid.NewGuid():N}.db");
        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Users.Add(new User { Id = "u2", Name = "U2", Email = "u2@t.local", Role = "user" });
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

    private RoutineBuiltinTool Tool(AppDbContext db) =>
        new(db, new AutomationService(db, NewProviders(db), NewNotifications(db)));

    private static ProviderService NewProviders(AppDbContext db) =>
        new(new FailFactory(), new ConfigService(db, TestCache.Create()),
            NullLogger<ProviderService>.Instance, TestCache.Create());

    private static NotificationService NewNotifications(AppDbContext db) =>
        new(new FailFactory(), db, NullLogger<NotificationService>.Instance);

    private static BuiltinToolContext Ctx(string userId = "u1", string? runId = null) =>
        new(userId, null, runId, "/tmp", "/tmp");

    private static JsonElement Args(object payload) =>
        JsonSerializer.SerializeToElement(payload);

    private async Task<ChatRun> SeedRunAsync(AppDbContext db, string model = "run:model")
    {
        var chat = new Chat
        {
            Id = $"c{Guid.NewGuid():N}", UserId = "u1", Title = "t",
            ModelsJson = "[]", CreatedAt = 1, UpdatedAt = 1,
        };
        var run = new ChatRun
        {
            ChatId = chat.Id, UserId = "u1", Model = model, CreatedAt = 1,
        };
        db.Chats.Add(chat);
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    // ---------------- ação inválida / list ----------------

    [Test]
    public async Task Routine_AcaoInvalida_ERequerida()
    {
        await using var db = NewDb();
        var tool = Tool(db);

        var semAcao = await tool.ExecuteAsync(Args(new { }), Ctx(), default);
        Assert.That(semAcao.Text, Does.Contain("Ação inválida"));

        var ruim = await tool.ExecuteAsync(Args(new { action = "explode" }), Ctx(), default);
        Assert.That(ruim.Text, Does.Contain("Ação inválida"));
    }

    [Test]
    public async Task Routine_List_VazioENaoVazio()
    {
        await using var db = NewDb();
        var tool = Tool(db);

        var vazio = await tool.ExecuteAsync(Args(new { action = "list" }), Ctx(), default);
        Assert.That(vazio.Text, Does.Contain("Nenhuma rotina"));

        var criada = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "diária", prompt = "bom dia",
                model = "m:1", schedule_kind = "daily", time_of_day = "08:00",
            }),
            Ctx(), default);
        Assert.That(criada.Text, Does.Contain("criada"));

        var cheio = await tool.ExecuteAsync(Args(new { action = "list" }), Ctx(), default);
        Assert.That(cheio.Text, Does.Contain("diária"));
        Assert.That(cheio.Text, Does.Contain("08:00"));

        // Isolamento: u2 não vê rotinas de u1.
        var outro = await tool.ExecuteAsync(Args(new { action = "list" }), Ctx("u2"), default);
        Assert.That(outro.Text, Does.Contain("Nenhuma rotina"));
    }

    // ---------------- create: validações e schedule kinds ----------------

    [Test]
    public async Task Routine_Create_ExigeNomeEPrompt()
    {
        await using var db = NewDb();
        var tool = Tool(db);

        var r1 = await tool.ExecuteAsync(
            Args(new { action = "create", prompt = "x", model = "m" }), Ctx(), default);
        Assert.That(r1.Text, Does.Contain("'name' e 'prompt'"));

        var r2 = await tool.ExecuteAsync(
            Args(new { action = "create", name = "x", model = "m" }), Ctx(), default);
        Assert.That(r2.Text, Does.Contain("'name' e 'prompt'"));
    }

    [Test]
    public async Task Routine_Create_PromptLongoDemais()
    {
        await using var db = NewDb();
        var tool = Tool(db);
        var r = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", model = "m",
                prompt = new string('x', 4001), schedule_kind = "interval",
            }),
            Ctx(), default);
        Assert.That(r.Text, Does.Contain("4000"));
    }

    [Test]
    public async Task Routine_Create_SemModeloSemRun_Falha()
    {
        await using var db = NewDb();
        var tool = Tool(db);
        var r = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p", schedule_kind = "interval",
            }),
            Ctx(), default);
        Assert.That(r.Text, Does.Contain("'model'"));
    }

    [Test]
    public async Task Routine_Create_HerdaModeloDaRun()
    {
        await using var db = NewDb();
        var run = await SeedRunAsync(db, "herdado:m");
        var tool = Tool(db);

        var r = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p",
                schedule_kind = "interval", interval_minutes = 30,
            }),
            Ctx(runId: run.Id), default);

        Assert.That(r.Text, Does.Contain("criada"));
        Assert.That(r.Text, Does.Contain("herdado:m"));
    }

    [Test]
    public async Task Routine_Create_Once_InMinutesERunAt()
    {
        await using var db = NewDb();
        var tool = Tool(db);

        var inMin = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n1", prompt = "p", model = "m",
                schedule_kind = "once", in_minutes = 15,
            }),
            Ctx(), default);
        Assert.That(inMin.Text, Does.Contain("criada"));
        Assert.That(inMin.Text, Does.Contain("próx."));

        var futuro = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var runAt = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n2", prompt = "p", model = "m",
                schedule_kind = "once", run_at = futuro,
            }),
            Ctx(), default);
        Assert.That(runAt.Text, Does.Contain("criada"));

        var semQuando = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n3", prompt = "p", model = "m",
                schedule_kind = "once",
            }),
            Ctx(), default);
        Assert.That(semQuando.Text, Does.Contain("'run_at' futuro ou 'in_minutes'"));
    }

    [Test]
    public async Task Routine_Create_Interval_DefaultMinimo()
    {
        await using var db = NewDb();
        var tool = Tool(db);
        var r = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p", model = "m",
                schedule_kind = "interval",
            }),
            Ctx(), default);
        Assert.That(r.Text, Does.Contain("criada"));
        var automation = await db.Automations.SingleAsync();
        Assert.That(automation.IntervalMinutes, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public async Task Routine_Create_DailyWeekly_Validacoes()
    {
        await using var db = NewDb();
        var tool = Tool(db);

        var semHora = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p", model = "m",
                schedule_kind = "daily",
            }),
            Ctx(), default);
        Assert.That(semHora.Text, Does.Contain("time_of_day"));

        var horaRuim = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p", model = "m",
                schedule_kind = "daily", time_of_day = "8h",
            }),
            Ctx(), default);
        Assert.That(horaRuim.Text, Does.Contain("HH:mm"));

        var semDow = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p", model = "m",
                schedule_kind = "weekly", time_of_day = "09:00",
            }),
            Ctx(), default);
        Assert.That(semDow.Text, Does.Contain("weekday"));

        var dowRuim = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p", model = "m",
                schedule_kind = "weekly", time_of_day = "09:00", weekday = 9,
            }),
            Ctx(), default);
        Assert.That(dowRuim.Text, Does.Contain("0-6"));

        var weeklyOk = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p", model = "m",
                schedule_kind = "weekly", time_of_day = "09:00", weekday = 3,
            }),
            Ctx(), default);
        Assert.That(weeklyOk.Text, Does.Contain("criada"));
        Assert.That(weeklyOk.Text, Does.Not.Contain("(desabilitada)"));
    }

    [Test]
    public async Task Routine_Create_KindInvalido()
    {
        await using var db = NewDb();
        var tool = Tool(db);
        var r = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p", model = "m",
                schedule_kind = "quando-eu-quiser",
            }),
            Ctx(), default);
        Assert.That(r.Text, Does.Contain("once|interval|daily|weekly"));
    }

    // ---------------- update / delete / enable / disable / run_now ----------------

    [Test]
    public async Task Routine_OperacoesSemId_OuNaoEncontrada()
    {
        await using var db = NewDb();
        var tool = Tool(db);

        foreach (var acao in new[] { "update", "delete", "enable", "disable", "run_now" })
        {
            var semId = await tool.ExecuteAsync(Args(new { action = acao }), Ctx(), default);
            Assert.That(semId.Text, Does.Contain("'id'"), acao);

            var naoAcha = await tool.ExecuteAsync(
                Args(new { action = acao, id = "nope" }), Ctx(), default);
            Assert.That(naoAcha.Text, Does.Contain("não encontrada"), acao);
        }
    }

    [Test]
    public async Task Routine_Update_CamposEReschedule()
    {
        await using var db = NewDb();
        var tool = Tool(db);
        var criada = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p", model = "m",
                schedule_kind = "interval", interval_minutes = 10,
            }),
            Ctx(), default);
        var id = JsonSerializer.SerializeToElement(criada.Result!)
            .GetProperty("Id").GetString()!;

        var upd = await tool.ExecuteAsync(
            Args(new
            {
                action = "update", id, name = "novo nome", model = "m2",
                interval_minutes = 120,
            }),
            Ctx(), default);
        Assert.That(upd.Text, Does.Contain("atualizada"));

        var automation = await db.Automations.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(automation.Name, Is.EqualTo("novo nome"));
            Assert.That(automation.ModelId, Is.EqualTo("m2"));
            Assert.That(automation.IntervalMinutes, Is.EqualTo(120));
        });

        // Update com prompt longo → erro.
        var longo = await tool.ExecuteAsync(
            Args(new { action = "update", id, prompt = new string('y', 5000) }),
            Ctx(), default);
        Assert.That(longo.Text, Does.Contain("4000"));

        // Update com schedule inválido → erro.
        var ruim = await tool.ExecuteAsync(
            Args(new { action = "update", id, schedule_kind = "bogus" }),
            Ctx(), default);
        Assert.That(ruim.Text, Does.Contain("once|interval"));
    }

    [Test]
    public async Task Routine_EnableDisableDelete_FluxoCompleto()
    {
        await using var db = NewDb();
        var tool = Tool(db);
        var criada = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p", model = "m",
                schedule_kind = "daily", time_of_day = "07:00",
            }),
            Ctx(), default);
        var id = JsonSerializer.SerializeToElement(criada.Result!)
            .GetProperty("Id").GetString()!;

        var dis = await tool.ExecuteAsync(
            Args(new { action = "disable", id }), Ctx(), default);
        Assert.That(dis.Text, Does.Contain("desabilitada"));
        var dis_ = await db.Automations.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(dis_.Enabled, Is.False);
            Assert.That(dis_.NextRunAt, Is.Null);
        });

        var en = await tool.ExecuteAsync(
            Args(new { action = "enable", id }), Ctx(), default);
        Assert.That(en.Text, Does.Contain("habilitada"));
        var en_ = await db.Automations.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(en_.Enabled, Is.True);
            Assert.That(en_.NextRunAt, Is.Not.Null);
        });

        var del = await tool.ExecuteAsync(
            Args(new { action = "delete", id }), Ctx(), default);
        Assert.That(del.Text, Does.Contain("removida"));
        Assert.That(await db.Automations.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Routine_RunNow_SemProvider_FalhaRegistrada()
    {
        // Sem conexão configurada, RunNowAsync gera run "failed" e retorna
        // o erro — nunca um resultado enlatado.
        await using var db = NewDb();
        var tool = Tool(db);
        var criada = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p", model = "m",
                schedule_kind = "interval",
            }),
            Ctx(), default);
        var id = JsonSerializer.SerializeToElement(criada.Result!)
            .GetProperty("Id").GetString()!;

        var ran = await tool.ExecuteAsync(
            Args(new { action = "run_now", id }), Ctx(), default);

        Assert.That(ran.Text, Does.Contain("falhou"));
        var run = await db.AutomationRuns.SingleAsync();
        Assert.That(run.Status, Is.EqualTo("failed"));
    }

    [Test]
    public async Task Routine_CrudDeOutroUsuario_NaoToca()
    {
        await using var db = NewDb();
        var tool = Tool(db);
        var criada = await tool.ExecuteAsync(
            Args(new
            {
                action = "create", name = "n", prompt = "p", model = "m",
                schedule_kind = "interval",
            }),
            Ctx(), default);
        var id = JsonSerializer.SerializeToElement(criada.Result!)
            .GetProperty("Id").GetString()!;

        // u2 não enxerga a rotina de u1 em nenhuma operação por id.
        var upd = await tool.ExecuteAsync(
            Args(new { action = "delete", id }), Ctx("u2"), default);
        Assert.That(upd.Text, Does.Contain("não encontrada"));
        Assert.That(await db.Automations.CountAsync(), Is.EqualTo(1));
    }

    // ---------------- reminder ----------------

    [Test]
    public async Task Reminder_ExigeMessage()
    {
        await using var db = NewDb();
        var tool = new ReminderBuiltinTool(db);
        var r = await tool.ExecuteAsync(Args(new { }), Ctx(), default);
        Assert.That(r.Text, Does.Contain("'message'"));
    }

    [Test]
    public async Task Reminder_AgendaComDefaultsEModeloDaRun()
    {
        await using var db = NewDb();
        var run = await SeedRunAsync(db, "reminder:m");
        var tool = new ReminderBuiltinTool(db);

        var r = await tool.ExecuteAsync(
            Args(new { message = "ligar pra Ana" }), Ctx(runId: run.Id), default);

        Assert.That(r.Text, Does.Contain("Lembrete"));
        var automation = await db.Automations.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(automation.ScheduleKind, Is.EqualTo("once"));
            Assert.That(automation.ModelId, Is.EqualTo("reminder:m"));
            Assert.That(automation.NextRunAt,
                Is.GreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            Assert.That(automation.Name, Does.StartWith("Lembrete:"));
        });
    }

    [Test]
    public async Task Reminder_RunAtFuturo_VenceInMinutes()
    {
        await using var db = NewDb();
        var tool = new ReminderBuiltinTool(db);
        var futuro = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();

        var r = await tool.ExecuteAsync(
            Args(new { message = "x", run_at = futuro, in_minutes = 1, model = "m" }),
            Ctx(), default);

        Assert.That(r.Text, Does.Contain("agendado"));
        Assert.That((await db.Automations.SingleAsync()).NextRunAt,
            Is.EqualTo(futuro));
    }

    [Test]
    public async Task Reminder_SemModeloENemRun_Falha()
    {
        await using var db = NewDb();
        var tool = new ReminderBuiltinTool(db);
        var r = await tool.ExecuteAsync(
            Args(new { message = "x" }), Ctx(), default);
        Assert.That(r.Text, Does.Contain("'model'"));
    }

    [Test]
    public async Task Reminder_TituloLongo_Trunca()
    {
        await using var db = NewDb();
        var tool = new ReminderBuiltinTool(db);
        var r = await tool.ExecuteAsync(
            Args(new { message = new string('z', 200), model = "m" }), Ctx(), default);

        Assert.That(r.Text, Does.Contain("agendado"));
        Assert.That((await db.Automations.SingleAsync()).Name.Length, Is.LessThanOrEqualTo(91));
    }

    /// <summary>Factory que sempre falha — ProviderService sem conexões.</summary>
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
