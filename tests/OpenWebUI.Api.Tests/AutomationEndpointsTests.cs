using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da slice automations-calendar: CRUD/ownership, run-now com provedor
/// mockado, run failed não bloqueando agenda e validação do schedule.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable] // popula o home global isolado e invalida o cache de Scan
public class AutomationEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-auto-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@auto.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);

        var baseUrl = StartMock();
        var connections = new ConnectionsConfig([baseUrl], [], []);
        var connResponse = await _client.PostAsJsonAsync("/api/v1/configs/connections", connections);
        Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private string StartMock()
    {
        var random = new Random();
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(40000, 60000);
            _mock = new HttpListener();
            _mock.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _mock.Start();
                break;
            }
            catch (HttpListenerException)
            {
                _mock.Close();
            }
        }
        if (!_mock.IsListening)
        {
            throw new InvalidOperationException("Nenhuma porta livre para o mock.");
        }
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => MockLoopAsync(_mockCts.Token));
        return $"http://localhost:{port}";
    }

    private async Task MockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _mock.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }

            var path = ctx.Request.Url!.AbsolutePath;
            var (status, json) = path switch
            {
                "/api/tags" => (200, "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}"),
                "/api/chat" => (200, "{\"message\":{\"content\":\"resposta do mock\"}}"),
                _ => (404, "{}"),
            };
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static AutomationUpsertRequest NewAutomation(
        string kind = "interval", int? minutes = 1,
        string? time = null, int? weekday = null, string model = "fake:1") =>
        new("Lembrete", "Diga olá", model, kind, minutes, time, weekday, true);

    [Test]
    public async Task Crud_ComValidacao_EOwnership()
    {
        var user = await SignUpAsync("User1", "u1@auto.local", "senha123");
        UseToken(user.Token);

        // Validação: intervalo < 1 → 400
        var invalid = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation(minutes: 0));
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Validação: weekly sem TimeOfDay → 400
        var invalidWeekly = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation(kind: "weekly", minutes: null, weekday: 2));
        Assert.That(invalidWeekly.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Cria interval de 1 minuto
        var created = await _client.PostAsJsonAsync("/api/v1/automations", NewAutomation());
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var automation = await created.Content.ReadFromJsonAsync<AutomationResponse>();
        Assert.That(automation, Is.Not.Null);
        Assert.That(automation!.NextRunAt, Is.Not.Null);

        // Outro usuário não enxerga (404)
        var other = await SignUpAsync("User2", "u2@auto.local", "senha123");
        UseToken(other.Token);
        var foreign = await _client.GetAsync($"/api/v1/automations/{automation.Id}");
        Assert.That(foreign.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Once_InMinutes_AgendaExecucaoUnica()
    {
        var user = await SignUpAsync("Once", "once@auto.local", "senha123");
        UseToken(user.Token);

        // Validação: once sem RunAt nem InMinutes → 400
        var invalid = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation(kind: "once", minutes: null));
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // once + inMinutes → 200 com NextRunAt futuro
        var created = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation(kind: "once", minutes: null) with { InMinutes = 5 });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var automation = await created.Content.ReadFromJsonAsync<AutomationResponse>();
        var esperado = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        Assert.That(automation!.NextRunAt, Is.InRange(esperado - 30, esperado + 30));
    }

    [Test]
    public async Task RunNow_ExecutaProvider_ECriaChat()
    {
        var user = await SignUpAsync("Runner", "runner@auto.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/automations", NewAutomation());
        var automation = await created.Content.ReadFromJsonAsync<AutomationResponse>();

        var runResponse = await _client.PostAsync($"/api/v1/automations/{automation!.Id}/run-now", null);
        Assert.That(runResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var run = await runResponse.Content.ReadFromJsonAsync<AutomationRunResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(run!.Status, Is.EqualTo("ok"));
            Assert.That(run.ChatId, Is.Not.Null);
        });

        // O chat criado contém prompt + resposta do mock
        var chat = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{run!.ChatId}");
        Assert.That(chat!.Messages, Has.Count.EqualTo(2));
        Assert.That(chat.Messages[1].Content, Is.EqualTo("resposta do mock"));

        // Run aparece na listagem
        var runs = await _client.GetFromJsonAsync<List<AutomationRunResponse>>(
            $"/api/v1/automations/{automation.Id}/runs");
        Assert.That(runs, Has.Count.EqualTo(1));

        // Calendário: runs no range do mês atual
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var inRange = await _client.GetFromJsonAsync<List<AutomationRunResponse>>(
            $"/api/v1/automations/runs?from={now - 3600}&to={now + 3600}");
        Assert.That(inRange!.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task RunNow_ProviderFora_RegistraFalhaEMantemAgenda()
    {
        var user = await SignUpAsync("Faller", "faller@auto.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation(model: "fake:1", kind: "daily", minutes: null, time: "23:59"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var automation = await created.Content.ReadFromJsonAsync<AutomationResponse>();

        // Quebramos as conexões (admin) para forçar falha de provider no run.
        UseToken(_adminToken);
        var broken = new ConnectionsConfig(["http://localhost:1"], [], []);
        await _client.PostAsJsonAsync("/api/v1/configs/connections", broken);
        UseToken(user.Token);

        var runResponse = await _client.PostAsync($"/api/v1/automations/{automation!.Id}/run-now", null);
        var run = await runResponse.Content.ReadFromJsonAsync<AutomationRunResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(run!.Status, Is.EqualTo("failed"));
            Assert.That(run.Error, Is.Not.Null.And.Not.Empty);
        });

        // Agenda continua: NextRunAt recalculado e automação segue habilitada.
        var refreshed = await _client.GetFromJsonAsync<AutomationResponse>(
            $"/api/v1/automations/{automation.Id}");
        Assert.Multiple(() =>
        {
            Assert.That(refreshed!.Enabled, Is.True);
            Assert.That(refreshed.NextRunAt, Is.Not.Null);
        });

        // Restaura o mock para os demais testes/fixtures desta base compartilhada.
        UseToken(_adminToken);
        var mockUrl = _mock.Prefixes.First();
        await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([mockUrl.TrimEnd('/')], [], []));
    }

    [Test]
    public async Task Update_ToggleDesabilita_LimpaNextRun()
    {
        var user = await SignUpAsync("Toggler", "toggler@auto.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/automations", NewAutomation());
        var automation = await created.Content.ReadFromJsonAsync<AutomationResponse>();

        var disabled = await _client.PutAsJsonAsync($"/api/v1/automations/{automation!.Id}",
            NewAutomation() with { Enabled = false });
        var updated = await disabled.Content.ReadFromJsonAsync<AutomationResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(updated!.Enabled, Is.False);
            Assert.That(updated.NextRunAt, Is.Null);
        });

        var deleted = await _client.DeleteAsync($"/api/v1/automations/{automation.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var gone = await _client.GetAsync($"/api/v1/automations/{automation.Id}");
        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- automation-actions (#409): callers skill/mcp/reminder/webhook ----

    [Test]
    public async Task Create_ActionKindInvalido_Retorna400()
    {
        var user = await SignUpAsync("Kinds", "kinds@auto.local", "senha123");
        UseToken(user.Token);
        var bad = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation() with { ActionKind = "bogus" });
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Create_Mcp_PayloadIncompleto_Retorna400()
    {
        var user = await SignUpAsync("McpVal", "mcpval@auto.local", "senha123");
        UseToken(user.Token);

        var semJson = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation() with { ActionKind = "mcp" });
        Assert.That(semJson.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var semTool = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation() with { ActionKind = "mcp", ActionJson = "{\"server\":\"fs\"}" });
        Assert.That(semTool.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var jsonRuim = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation() with { ActionKind = "skill", ActionJson = "não-é-json" });
        Assert.That(jsonRuim.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task RunNow_Reminder_Ok_SemChat()
    {
        var user = await SignUpAsync("Remind", "remind@auto.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation(model: "") with
            {
                ActionKind = "reminder",
                ActionJson = "{\"title\":\"Beber água\",\"body\":\"hidratação\"}",
                PermissionMode = "auto",
            });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var automation = await created.Content.ReadFromJsonAsync<AutomationResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(automation!.ActionKind, Is.EqualTo("reminder"));
            Assert.That(automation.PermissionMode, Is.EqualTo("auto"));
        });

        var runResponse = await _client.PostAsync($"/api/v1/automations/{automation!.Id}/run-now", null);
        var run = await runResponse.Content.ReadFromJsonAsync<AutomationRunResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(run!.Status, Is.EqualTo("ok"), run!.Error);
            Assert.That(run.ActionKind, Is.EqualTo("reminder"));
            Assert.That(run.Output, Does.Contain("Beber água"));
            Assert.That(run.ChatId, Is.Null); // reminder não cria chat
        });
    }

    [Test]
    public async Task RunNow_SkillDesconhecida_Failed()
    {
        var user = await SignUpAsync("Skiller", "skiller@auto.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation() with
            {
                ActionKind = "skill",
                ActionJson = "{\"skill\":\"skill-inexistente-xyz\"}",
            });
        var automation = await created.Content.ReadFromJsonAsync<AutomationResponse>();

        var runResponse = await _client.PostAsync($"/api/v1/automations/{automation!.Id}/run-now", null);
        var run = await runResponse.Content.ReadFromJsonAsync<AutomationRunResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(run!.Status, Is.EqualTo("failed"));
            Assert.That(run.Error, Does.Contain("skill-inexistente-xyz"));
        });
    }

    [Test]
    public async Task RunNow_Skill_Ok_MontaPromptDaSkill()
    {
        // Skill no home global isolado da suíte (SPEC-20261016-global-agents-scope).
        var skillDir = Path.Join(TestHomeIsolation.Home, ".agents", "skills", "hidrate");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Join(skillDir, "SKILL.md"), """
            ---
            name: hidrate
            description: Lembrete de hidratação
            ---
            Você é um coach de hidratação. MARCADOR-SKILL-42.
            """);
        SkillDiscoveryService.Invalidate("");

        var user = await SignUpAsync("SkillOk", "skillok@auto.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation() with
            {
                ActionKind = "skill",
                ActionJson = "{\"skill\":\"hidrate\",\"args\":\"2 copos\"}",
            });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var automation = await created.Content.ReadFromJsonAsync<AutomationResponse>();

        var runResponse = await _client.PostAsync($"/api/v1/automations/{automation!.Id}/run-now", null);
        var run = await runResponse.Content.ReadFromJsonAsync<AutomationRunResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(run!.Status, Is.EqualTo("ok"), run!.Error);
            Assert.That(run.ActionKind, Is.EqualTo("skill"));
            Assert.That(run.ChatId, Is.Not.Null);
        });
        var chat = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{run!.ChatId}");
        Assert.That(chat!.Messages[0].Content, Does.Contain("MARCADOR-SKILL-42"));
    }

    [Test]
    public async Task RunNow_Mcp_ServidorInexistente_Failed()
    {
        var user = await SignUpAsync("Mcper", "mcper@auto.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation() with
            {
                ActionKind = "mcp",
                ActionJson = "{\"server\":\"env:nao-existe\",\"tool\":\"qualquer\"}",
            });
        var automation = await created.Content.ReadFromJsonAsync<AutomationResponse>();

        var runResponse = await _client.PostAsync($"/api/v1/automations/{automation!.Id}/run-now", null);
        var run = await runResponse.Content.ReadFromJsonAsync<AutomationRunResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(run!.Status, Is.EqualTo("failed"));
            Assert.That(run.Error, Does.Contain("env:nao-existe"));
        });
    }

    [Test]
    public async Task Create_Webhook_UrlInvalida_Retorna400()
    {
        var user = await SignUpAsync("HookVal", "hookval@auto.local", "senha123");
        UseToken(user.Token);
        var bad = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation() with
            {
                ActionKind = "webhook",
                ActionJson = "{\"url\":\"não-é-url\"}",
            });
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task RunNow_Webhook_Loopback_Bloqueado()
    {
        var user = await SignUpAsync("Hooked", "hooked@auto.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/automations",
            NewAutomation(model: "") with
            {
                ActionKind = "webhook",
                ActionJson = "{\"url\":\"http://localhost:1/hook\"}",
            });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var automation = await created.Content.ReadFromJsonAsync<AutomationResponse>();

        var runResponse = await _client.PostAsync($"/api/v1/automations/{automation!.Id}/run-now", null);
        var run = await runResponse.Content.ReadFromJsonAsync<AutomationRunResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(run!.Status, Is.EqualTo("failed"));
            Assert.That(run.Error, Does.Contain("bloqueada"));
        });
    }

    [TestCase("https://api.exemplo.com/hook", true)]
    [TestCase("http://8.8.8.8/x", true)]
    [TestCase("http://localhost/x", false)]
    [TestCase("http://127.0.0.1/x", false)]
    [TestCase("http://10.0.0.5/x", false)]
    [TestCase("http://192.168.1.1/x", false)]
    [TestCase("http://172.16.0.1/x", false)]
    [TestCase("http://172.20.0.1/x", false)]
    [TestCase("http://169.254.169.254/latest", false)]
    [TestCase("http://[::1]/x", false)]
    public void WebhookHost_SsrfGuard(string url, bool esperado) =>
        Assert.That(
            Infrastructure.Services.AutomationService.IsSafeWebhookHost(new Uri(url)),
            Is.EqualTo(esperado));
}
