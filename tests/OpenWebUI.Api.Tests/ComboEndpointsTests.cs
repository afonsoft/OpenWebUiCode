using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da slice model-combos (#410): CRUD/ownership, estratégias de
/// resolução, cooldown/exhausted, rate limit compartilhado e failover de
/// run via automação com <c>combo:{id}</c>.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ComboEndpointsTests
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
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-combo-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@combo.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);

        var baseUrl = StartMock();
        var connResponse = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([baseUrl], [], []));
        Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [SetUp]
    public void SetUp() => ComboService.ResetState();

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
            var (status, json) = (200, "{}");
            if (path == "/api/tags")
            {
                json = "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}," +
                       "{\"model\":\"fail:1\",\"name\":\"fail:1\"}]}";
            }
            else if (path == "/api/chat")
            {
                // Modelo "fail:*" → 500 transitório; qualquer outro → ok.
                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var model = JsonDocument.Parse(body).RootElement
                    .TryGetProperty("model", out var m) ? m.GetString() : null;
                if (model?.StartsWith("fail:", StringComparison.Ordinal) == true)
                {
                    status = 500;
                }
                else
                {
                    json = $"{{\"message\":{{\"content\":\"ok de {model}\"}}}}";
                }
            }

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

    private static async Task<ComboUnavailableException?> CatchComboAsync(
        Func<Task<ComboPick>> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (ComboUnavailableException ex)
        {
            return ex;
        }
    }

    private ComboService ScopedService()
    {
        var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ComboService>();
    }

    private static ComboUpsertRequest NewCombo(
        string name = "meu-combo", string strategy = "priority",
        params string[] models) =>
        new(name, strategy, [.. models.Select(m => new ComboItemRequest(m))]);

    [Test]
    public async Task Crud_Validacao_EOwnership()
    {
        var user = await SignUpAsync("Owner", "owner@combo.local", "senha123");
        UseToken(user.Token);

        var badStrategy = await _client.PostAsJsonAsync("/api/v1/combos",
            NewCombo(strategy: "bogus"));
        Assert.That(badStrategy.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var semItens = await _client.PostAsJsonAsync("/api/v1/combos",
            NewCombo() with { Items = [] });
        Assert.That(semItens.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var created = await _client.PostAsJsonAsync("/api/v1/combos",
            NewCombo(models: ["fake:1", "fail:1"]));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var combo = await created.Content.ReadFromJsonAsync<ComboResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(combo!.Items, Is.EqualTo(new[] { "fake:1", "fail:1" }));
            Assert.That(combo.UserId, Is.EqualTo(user.User.Id));
        });

        var dup = await _client.PostAsJsonAsync("/api/v1/combos",
            NewCombo(models: ["fake:1"]));
        Assert.That(dup.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        // Não-admin não cria combo global.
        var global = await _client.PostAsJsonAsync("/api/v1/combos",
            NewCombo(name: "g", models: ["fake:1"]) with { Global = true });
        Assert.That(global.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // Outro usuário não vê/edita (404).
        var other = await SignUpAsync("Other", "other@combo.local", "senha123");
        UseToken(other.Token);
        Assert.That((await _client.GetAsync($"/api/v1/combos/{combo!.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));

        // Admin cria global; o usuário lê mas não edita.
        UseToken(_adminToken);
        var adminCombo = await _client.PostAsJsonAsync("/api/v1/combos",
            NewCombo(name: "global-1", models: ["fake:1"]) with { Global = true });
        Assert.That(adminCombo.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var g = await adminCombo.Content.ReadFromJsonAsync<ComboResponse>();
        UseToken(user.Token);
        Assert.That((await _client.GetAsync($"/api/v1/combos/{g!.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        var edit = await _client.PutAsJsonAsync($"/api/v1/combos/{g.Id}",
            NewCombo(name: "global-2", models: ["fake:1"]));
        Assert.That(edit.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Resolve_Priority_Cooldown_EExhausted()
    {
        var user = await SignUpAsync("Prio", "prio@combo.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/combos",
            NewCombo(models: ["fail:1", "fake:1"]));
        var combo = await created.Content.ReadFromJsonAsync<ComboResponse>();

        var service = ScopedService();
        var pick1 = await service.ResolveAsync(combo!.Id, user.User.Id);
        Assert.That(pick1.ModelId, Is.EqualTo("fail:1")); // priority: posição 0

        // Falha reportada → cooldown: próximo resolve pula fail:1.
        await service.ReportAsync(combo.Id, "fail:1", ok: false);
        var pick2 = await service.ResolveAsync(combo.Id, user.User.Id);
        Assert.That(pick2.ModelId, Is.EqualTo("fake:1"));

        // Todos em cooldown → combo-exhausted com retryAt.
        await service.ReportAsync(combo.Id, "fake:1", ok: false);
        var ex = await CatchComboAsync(() => service.ResolveAsync(combo.Id, user.User.Id));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Code, Is.EqualTo("combo-exhausted"));
            Assert.That(ex.RetryAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Resolve_RoundRobin_Distribui()
    {
        var user = await SignUpAsync("Rr", "rr@combo.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/combos",
            NewCombo(strategy: "round-robin", models: ["m1", "m2", "m3"]));
        var combo = await created.Content.ReadFromJsonAsync<ComboResponse>();

        var service = ScopedService();
        var picks = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            picks.Add((await service.ResolveAsync(combo!.Id, user.User.Id)).ModelId);
        }
        Assert.That(picks.Distinct().Count(), Is.EqualTo(3));
    }

    [Test]
    public async Task Resolve_RateLimit_BloqueiaJanela()
    {
        var user = await SignUpAsync("Rl", "rl@combo.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/combos",
            NewCombo(models: ["fake:1", "m2"]) with { RequestsPerMinute = 1 });
        var combo = await created.Content.ReadFromJsonAsync<ComboResponse>();

        var service = ScopedService();
        await service.ResolveAsync(combo!.Id, user.User.Id);
        var ex = await CatchComboAsync(() => service.ResolveAsync(combo.Id, user.User.Id));
        Assert.That(ex!.Code, Is.EqualTo("combo-rate-limit"));
    }

    [Test]
    public async Task Status_ExpoeCooldownEJanela()
    {
        var user = await SignUpAsync("St", "st@combo.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/combos",
            NewCombo(models: ["fail:1", "fake:1"]));
        var combo = await created.Content.ReadFromJsonAsync<ComboResponse>();

        var service = ScopedService();
        await service.ResolveAsync(combo!.Id, user.User.Id);
        await service.ReportAsync(combo.Id, "fail:1", ok: false);

        var status = await _client.GetFromJsonAsync<ComboStatusResponse>(
            $"/api/v1/combos/{combo.Id}/status");
        Assert.Multiple(() =>
        {
            Assert.That(status!.EffectiveModel, Is.EqualTo("fake:1"));
            Assert.That(status.Cooldowns, Has.Count.EqualTo(1));
            Assert.That(status.Cooldowns[0].ModelId, Is.EqualTo("fail:1"));
            Assert.That(status.WindowRequests, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RunNow_Combo_FazFailoverParaProximoModelo()
    {
        var user = await SignUpAsync("Run", "run@combo.local", "senha123");
        UseToken(user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/combos",
            NewCombo(models: ["fail:1", "fake:1"]));
        var combo = await created.Content.ReadFromJsonAsync<ComboResponse>();

        // Automação com model = combo:{id} → provider falha em fail:1 →
        // report + retry em fake:1 (RF-003, caminho automação).
        var automation = await _client.PostAsJsonAsync("/api/v1/automations",
            new AutomationUpsertRequest(
                "ComboRun", "Diga olá", $"combo:{combo!.Id}",
                "interval", 60, null, null, true));
        Assert.That(automation.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var auto = await automation.Content.ReadFromJsonAsync<AutomationResponse>();

        var runResponse = await _client.PostAsync($"/api/v1/automations/{auto!.Id}/run-now", null);
        var run = await runResponse.Content.ReadFromJsonAsync<AutomationRunResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(run!.Status, Is.EqualTo("ok"), run!.Error);
            Assert.That(run.ChatId, Is.Not.Null);
        });

        var chat = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{run!.ChatId}");
        Assert.That(chat!.Messages[1].Content, Is.EqualTo("ok de fake:1"));

        // Status confirma cooldown do modelo que falhou.
        var status = await _client.GetFromJsonAsync<ComboStatusResponse>(
            $"/api/v1/combos/{combo.Id}/status");
        Assert.That(status!.Cooldowns.Any(c => c.ModelId == "fail:1"), Is.True);
    }
}
