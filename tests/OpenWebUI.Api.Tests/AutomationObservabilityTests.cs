using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da SPEC-20261115-automation-observability: endpoint de stats
/// agregados, dedup de webhook por Idempotency-Key/hash do payload e
/// thresholds do badge de saúde (<see cref="AutomationHealth"/>).
/// </summary>
[TestFixture, IsolateEnvironment]
public class AutomationObservabilityTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-obs-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@obs.local");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);

        var baseUrl = StartMock();
        var connections = new ConnectionsConfig([baseUrl], [], []);
        var conn = await _client.PostAsJsonAsync(
            "/api/v1/configs/connections", connections);
        Assert.That(conn.StatusCode, Is.EqualTo(HttpStatusCode.OK));
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
            throw new InvalidOperationException(
                "Nenhuma porta livre para o mock.");
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
            var (status, json) = ctx.Request.Url!.AbsolutePath switch
            {
                "/api/tags" => (200,
                    "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}"),
                "/api/chat" => (200,
                    "{\"message\":{\"content\":\"resposta do mock\"}}"),
                _ => (404, "{}"),
            };
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var resp = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    private async Task<AutomationResponse> CriarAutomacaoAsync(
        AutomationUpsertRequest req)
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/automations", req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await resp.Content.ReadAsStringAsync());
        return (await resp.Content
            .ReadFromJsonAsync<AutomationResponse>())!;
    }

    private async Task<HttpResponseMessage> FireRawAsync(
        string url, string body,
        IEnumerable<(string Name, string Value)>? headers = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                body, Encoding.UTF8, "application/json"),
        };
        foreach (var (n, v) in headers ?? [])
        {
            req.Headers.TryAddWithoutValidation(n, v);
        }
        _client.DefaultRequestHeaders.Authorization = null;
        return await _client.SendAsync(req);
    }

    [Test]
    public async Task Stats_ReturnsAggregatesAndLastRuns()
    {
        var user = await SignUpAsync("Stats", "stats@obs.local");
        UseToken(user.Token);

        // Automação que sempre falha: ação webhook para porta fechada.
        var bad = await CriarAutomacaoAsync(new AutomationUpsertRequest(
            "quebra", "", "", "interval", 5, null, null, true,
            ActionKind: "webhook",
            ActionJson: """{"url":"http://localhost:1/x"}"""));
        // Automação saudável: prompt no mock Ollama.
        var ok = await CriarAutomacaoAsync(new AutomationUpsertRequest(
            "boa", "Diga olá", "fake:1", "interval", 5, null, null, true));

        await _client.PostAsync(
            $"/api/v1/automations/{ok.Id}/run-now", null);
        await _client.PostAsync(
            $"/api/v1/automations/{ok.Id}/run-now", null);
        await _client.PostAsync(
            $"/api/v1/automations/{bad.Id}/run-now", null);

        var okStats = await _client.GetFromJsonAsync<AutomationStatsResponse>(
            $"/api/v1/automations/{ok.Id}/stats");
        var badStats = await _client.GetFromJsonAsync<AutomationStatsResponse>(
            $"/api/v1/automations/{bad.Id}/stats");

        Assert.Multiple(() =>
        {
            Assert.That(okStats!.Total, Is.GreaterThanOrEqualTo(2));
            Assert.That(okStats.Ok, Is.GreaterThanOrEqualTo(2));
            Assert.That(okStats.SuccessRate, Is.EqualTo(100d));
            Assert.That(okStats.LastRunAt, Is.Not.Null);
            Assert.That(okStats.LastError, Is.Null);
            Assert.That(okStats.Runs.Count,
                Is.LessThanOrEqualTo(10).And.GreaterThanOrEqualTo(2));

            Assert.That(badStats!.Failed, Is.GreaterThanOrEqualTo(1));
            Assert.That(badStats.SuccessRate, Is.EqualTo(0d));
            Assert.That(badStats.LastError, Is.Not.Null.And.Not.Empty);
            Assert.That(badStats.ConsecutiveFailures,
                Is.GreaterThanOrEqualTo(1));
        });
    }

    [Test]
    public async Task WebhookDedup_SameKey_SkipsRun()
    {
        var user = await SignUpAsync("Dedup", "dedup@obs.local");
        UseToken(user.Token);
        var chat = (await (await _client.PostAsJsonAsync("/api/v1/chats/",
                new ChatUpsertRequest("Chat dedup", ["fake:1"], [])))
            .Content.ReadFromJsonAsync<ChatResponse>())!;
        var hookResp = await _client.PostAsJsonAsync("/api/v1/hooks",
            new AutomationHookCreateRequest("hook dedup", chat.Id));
        var hook = (await hookResp.Content
            .ReadFromJsonAsync<AutomationHookResponse>())!;

        var first = await FireRawAsync(hook.Url,
            """{"prompt":"evento A"}""",
            [("Idempotency-Key", "key-1")]);
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        // Mesma chave → deduplicado, sem run nova.
        var second = await FireRawAsync(hook.Url,
            """{"prompt":"outro corpo"}""",
            [("Idempotency-Key", "key-1")]);
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var dedup = await second.Content
            .ReadFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>();
        Assert.That(dedup!["deduplicated"].GetBoolean(), Is.True);

        // Sem chave: mesmo payload = mesmo hash → deduplicado.
        var payload = """{"prompt":"evento B"}""";
        var p1 = await FireRawAsync(hook.Url, payload);
        Assert.That(p1.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var p2 = await FireRawAsync(hook.Url, payload);
        Assert.That(p2.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task DifferentPayload_NewRun()
    {
        var user = await SignUpAsync("Diff", "diff@obs.local");
        UseToken(user.Token);
        var chat = (await (await _client.PostAsJsonAsync("/api/v1/chats/",
                new ChatUpsertRequest("Chat diff", ["fake:1"], [])))
            .Content.ReadFromJsonAsync<ChatResponse>())!;
        var hookResp = await _client.PostAsJsonAsync("/api/v1/hooks",
            new AutomationHookCreateRequest("hook diff", chat.Id));
        var hook = (await hookResp.Content
            .ReadFromJsonAsync<AutomationHookResponse>())!;

        var a = await FireRawAsync(hook.Url, """{"prompt":"payload 1"}""");
        var b = await FireRawAsync(hook.Url, """{"prompt":"payload 2"}""");
        Assert.Multiple(() =>
        {
            Assert.That(a.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
            Assert.That(b.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        });
        var runA = await a.Content
            .ReadFromJsonAsync<AutomationHookFireResponse>();
        var runB = await b.Content
            .ReadFromJsonAsync<AutomationHookFireResponse>();
        Assert.That(runA!.RunId, Is.Not.EqualTo(runB!.RunId));
    }

    [Test]
    public void Badge_Thresholds()
    {
        static AutomationStatsResponse Stats(int total, int ok, double rate) =>
            new(total, ok, total - ok, rate, 1000, null, 0, []);

        Assert.Multiple(() =>
        {
            // Verde: ≥90% ou sem runs.
            Assert.That(AutomationHealth.Level(true, 0, Stats(20, 19, 95)),
                Is.EqualTo("ok"));
            Assert.That(AutomationHealth.Level(true, 0, null),
                Is.EqualTo("ok"));
            // Amarelo: 70-90% ou 1-2 falhas seguidas.
            Assert.That(AutomationHealth.Level(true, 0, Stats(20, 16, 80)),
                Is.EqualTo("warn"));
            Assert.That(AutomationHealth.Level(true, 2, Stats(20, 19, 95)),
                Is.EqualTo("warn"));
            // Vermelho: <70%, ≥3 seguidas ou desabilitada pelo breaker.
            Assert.That(AutomationHealth.Level(true, 0, Stats(20, 10, 50)),
                Is.EqualTo("bad"));
            Assert.That(AutomationHealth.Level(true, 3, Stats(20, 19, 95)),
                Is.EqualTo("bad"));
            Assert.That(AutomationHealth.Level(false, 5, Stats(20, 19, 95)),
                Is.EqualTo("bad"));
        });
    }
}
