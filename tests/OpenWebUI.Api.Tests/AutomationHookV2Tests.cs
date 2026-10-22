using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura do webhook v2 (SPEC-20261021-harness-seams RF-D1/D2):
/// HMAC-SHA256 no disparo, filtro de eventos por provider, dedup por
/// delivery key, template <c>{{path}}</c>, auditoria em
/// <c>/api/v1/hooks/{id}/deliveries</c> e compatibilidade de hooks
/// legados sem secret.
/// </summary>
[TestFixture, IsolateEnvironment]
public class AutomationHookV2Tests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-hooksv2-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        var admin = await SignUpAsync("Admin", "admin@hooksv2.local");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync(
            "/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret),
                Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private async Task<AutomationHookResponse> CreateHookAsync(
        AutomationHookCreateRequest req)
    {
        var created = await _client.PostAsJsonAsync("/api/v1/hooks", req);
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content
            .ReadFromJsonAsync<AutomationHookResponse>())!;
    }

    private async Task<HttpResponseMessage> FireRawAsync(
        string url, string body,
        IEnumerable<(string Name, string Value)>? headers = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        foreach (var (n, v) in headers ?? [])
        {
            req.Headers.TryAddWithoutValidation(n, v);
        }
        _client.DefaultRequestHeaders.Authorization = null;
        return await _client.SendAsync(req);
    }

    [Test]
    public async Task Webhook_Github_AssinaturaValida_Aceita_E_Invalida_Rejeita()
    {
        var auth = await SignUpAsync("Gh", "gh@hooks.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Chat gh", ["llama3"]);
        var hook = await CreateHookAsync(new AutomationHookCreateRequest(
            "gh hook", chat.Id, Secret: "segredo-hmac", Provider: "github"));

        var body = """{"ref":"main"}""";
        var ok = await FireRawAsync(hook.Url, body,
            [("X-GitHub-Event", "push"), ("X-GitHub-Delivery", "d-1"),
             ("X-Hub-Signature-256", Sign("segredo-hmac", body))]);
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await ok.Content.ReadAsStringAsync());

        var bad = await FireRawAsync(hook.Url, body,
            [("X-GitHub-Event", "push"), ("X-GitHub-Delivery", "d-2"),
             ("X-Hub-Signature-256", "sha256=deadbeef")]);
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        var missing = await FireRawAsync(hook.Url, body,
            [("X-GitHub-Event", "push"), ("X-GitHub-Delivery", "d-3")]);
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        // Auditoria: 1 accepted + 2 rejected.
        UseToken(auth.Token);
        var deliveries = await _client.GetFromJsonAsync<
            List<AutomationHookDeliveryResponse>>(
            $"/api/v1/hooks/{hook.Id}/deliveries");
        Assert.That(deliveries, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(deliveries!.Count(d => d.Status == "accepted"),
                Is.EqualTo(1));
            Assert.That(deliveries.Count(d => d.Status == "rejected"),
                Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Webhook_FiltroDeEventos_Filtra_E_Dedup_Repete_Run()
    {
        var auth = await SignUpAsync("F", "f@hooks.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Chat f", ["llama3"]);
        var hook = await CreateHookAsync(new AutomationHookCreateRequest(
            "filtered", chat.Id, Secret: "s", Provider: "github",
            Events: ["push"]));

        var body = """{"prompt":"vai"}""";
        var ping = await FireRawAsync(hook.Url, body,
            [("X-GitHub-Event", "ping"), ("X-GitHub-Delivery", "p-1"),
             ("X-Hub-Signature-256", Sign("s", body))]);
        Assert.That(ping.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var push1 = await FireRawAsync(hook.Url, body,
            [("X-GitHub-Event", "push"), ("X-GitHub-Delivery", "dup-1"),
             ("X-Hub-Signature-256", Sign("s", body))]);
        Assert.That(push1.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var fired1 = (await push1.Content
            .ReadFromJsonAsync<AutomationHookFireResponse>())!;

        // Mesma delivery key → idempotente: "duplicate" e MESMA run.
        var push2 = await FireRawAsync(hook.Url, body,
            [("X-GitHub-Event", "push"), ("X-GitHub-Delivery", "dup-1"),
             ("X-Hub-Signature-256", Sign("s", body))]);
        Assert.That(push2.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var fired2 = (await push2.Content
            .ReadFromJsonAsync<AutomationHookFireResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(fired2.Status, Is.EqualTo("duplicate"));
            Assert.That(fired2.RunId, Is.EqualTo(fired1.RunId));
        });

        UseToken(auth.Token);
        var deliveries = await _client.GetFromJsonAsync<
            List<AutomationHookDeliveryResponse>>(
            $"/api/v1/hooks/{hook.Id}/deliveries");
        Assert.That(deliveries!.Any(d => d.Status == "filtered"), Is.True);
    }

    [Test]
    public async Task Webhook_Template_Resolve_Payload_E_CampoAusente_422()
    {
        var auth = await SignUpAsync("T", "t@hooks.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Chat t", ["llama3"]);
        var hook = await CreateHookAsync(new AutomationHookCreateRequest(
            "tpl", chat.Id, Provider: "github",
            PromptTemplate: "Issue {{issue.number}}: {{issue.title}}"));

        var ok = await FireRawAsync(hook.Url,
            """{"issue":{"number":7,"title":"bug no login"}}""",
            [("X-GitHub-Event", "issues"), ("X-GitHub-Delivery", "t-1")]);
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await ok.Content.ReadAsStringAsync());

        UseToken(auth.Token);
        var depois = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}");
        Assert.That(depois!.Messages.Any(m =>
            m.Content.Contains("Issue 7: bug no login")), Is.True);

        _client.DefaultRequestHeaders.Authorization = null;
        var missing = await FireRawAsync(hook.Url,
            """{"issue":{"number":7}}""",
            [("X-GitHub-Event", "issues"), ("X-GitHub-Delivery", "t-2")]);
        Assert.That(missing.StatusCode,
            Is.EqualTo(HttpStatusCode.UnprocessableEntity));
    }

    [Test]
    public async Task Webhook_Legado_SemSecret_Aceita_Com_Warning_E_Deliveries_404_ParaIntruso()
    {
        var auth = await SignUpAsync("L", "l@hooks.local");
        var intruso = await SignUpAsync("I", "i@hooks.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Chat l", ["llama3"]);
        var hook = await CreateHookAsync(new AutomationHookCreateRequest(
            "legacy", chat.Id));

        var fire = await _client.PostAsJsonAsync(hook.Url,
            new { prompt = "legado" });
        Assert.That(fire.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        UseToken(auth.Token);
        var deliveries = await _client.GetFromJsonAsync<
            List<AutomationHookDeliveryResponse>>(
            $"/api/v1/hooks/{hook.Id}/deliveries");
        Assert.That(deliveries!.Single().SecurityWarning,
            Does.Contain("unsigned"));

        UseToken(intruso.Token);
        var intrusoResp = await _client.GetAsync(
            $"/api/v1/hooks/{hook.Id}/deliveries");
        Assert.That(intrusoResp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Webhook_AgentMode_Aplica_No_Chat()
    {
        var auth = await SignUpAsync("M", "m@hooks.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Chat m", ["llama3"]);
        var hook = await CreateHookAsync(new AutomationHookCreateRequest(
            "mode", chat.Id, AgentMode: "plan"));

        _client.DefaultRequestHeaders.Authorization = null;
        var fire = await _client.PostAsJsonAsync(hook.Url,
            new { prompt = "planeje" });
        Assert.That(fire.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        UseToken(auth.Token);
        var depois = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}");
        Assert.That(depois!.Mode, Is.EqualTo("plan"));
    }

    [Test]
    public async Task Webhook_Delete_Remove_Hook_E_Deliveries_Lista_Vazia()
    {
        var auth = await SignUpAsync("D", "d@hooks.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Chat d", ["llama3"]);
        var hook = await CreateHookAsync(new AutomationHookCreateRequest(
            "del", chat.Id));

        _client.DefaultRequestHeaders.Authorization = null;
        var fire = await _client.PostAsJsonAsync(hook.Url,
            new { prompt = "ping" });
        Assert.That(fire.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        UseToken(auth.Token);
        var deliveries = await _client.GetFromJsonAsync<
            List<AutomationHookDeliveryResponse>>(
            $"/api/v1/hooks/{hook.Id}/deliveries");
        Assert.That(deliveries!.Count, Is.EqualTo(1));

        var del = await _client.DeleteAsync($"/api/v1/hooks/{hook.Id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetFromJsonAsync<
            List<AutomationHookResponse>>("/api/v1/hooks");
        Assert.That(list!.Any(h => h.Id == hook.Id), Is.False);

        // Fire após delete → 404.
        _client.DefaultRequestHeaders.Authorization = null;
        var fire2 = await _client.PostAsJsonAsync(hook.Url,
            new { prompt = "ping" });
        Assert.That(fire2.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Webhook_ListAsync_Expoe_Hooks_Do_Usuario()
    {
        var auth = await SignUpAsync(
            "L", $"list-{Guid.NewGuid():N}@hooks.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Chat l", ["llama3"]);
        var hook = await CreateHookAsync(new AutomationHookCreateRequest(
            "list", chat.Id, Provider: "github", Secret: "s3"));

        var list = await _client.GetFromJsonAsync<
            List<AutomationHookResponse>>("/api/v1/hooks");
        var found = list!.FirstOrDefault(h => h.Id == hook.Id);
        Assert.Multiple(() =>
        {
            Assert.That(found, Is.Not.Null);
            Assert.That(found!.Name, Is.EqualTo("list"));
            Assert.That(found.Url, Does.Contain(hook.Id));
        });
    }

    // ---------------- helpers ----------------

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new SignUpRequest(name, email, "senha123"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    private async Task<ChatResponse> CriarChatAsync(
        string title, string[] models)
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest(title, models, []));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<ChatResponse>())!;
    }
}
