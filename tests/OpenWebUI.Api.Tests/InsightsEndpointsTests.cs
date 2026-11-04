using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261104-session-insights-v2: heurística de tamanho/categoria/issues
/// (RF-001/RF-002), análise LLM sob demanda (RF-003 — 422 sem modelo, JSON
/// persistido com mock), permissões e agregados admin (RF-005).
/// </summary>
[TestFixture, IsolateEnvironment]
public class InsightsEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private string _userToken = null!;
    private string _userId = null!;
    private string _mockBase = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = TestInfra.NewDbPath("openwebui-insights");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mockCts = new CancellationTokenSource();
        _mock = StartMock(out _mockBase);

        var admin = await SignUpAsync("Admin", "admin@insights.local");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        Assert.That((await _client.PostAsJsonAsync(
            "/api/v1/auths/admin/config", config)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([_mockBase], [], []))).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var user = await SignUpAsync("User", "user@insights.local");
        _userToken = user.Token;
        _userId = user.User.Id;
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        _mock.Close();
        _mockCts.Dispose();
        _client.Dispose();
        _factory.Dispose();
        TestInfra.DeleteDb(_dbPath);
    }

    private HttpListener StartMock(out string baseUrl)
    {
        var random = new Random();
        HttpListener? listener = null;
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(40000, 60000);
            listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
                break;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }
        if (listener is not { IsListening: true })
        {
            throw new InvalidOperationException("Nenhuma porta livre para o mock.");
        }
        _ = Task.Run(() => MockLoopAsync(listener, _mockCts.Token));
        baseUrl = $"http://localhost:{port}";
        return listener;
    }

    /// <summary>Ollama fake — /api/tags lista o modelo; /api/chat devolve a
    /// análise JSON pedida pelo generate (RF-003).</summary>
    private static async Task MockLoopAsync(HttpListener listener, CancellationToken ct)
    {
        const string analysis = "{\"summary\":\"sessão ok\",\"issues\":[\"retry\"],"
            + "\"improvedPrompt\":\"melhore o prompt\","
            + "\"changes\":[{\"what\":\"contexto\",\"why\":\"clareza\"}],"
            + "\"actionItems\":[{\"kind\":\"skill\",\"text\":\"criar skill x\"}],"
            + "\"knowledgeUsed\":[\"nota\"]}";
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            var json = ctx.Request.Url!.AbsolutePath == "/api/tags"
                ? "{\"models\":[{\"model\":\"adm-a\",\"name\":\"adm-a\"}]}"
                : $"{{\"message\":{{\"content\":{JsonSerializer.Serialize(analysis)}}}}}";
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<ChatResponse> CriarChatAsync(string title)
    {
        var created = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest(title, [],
            [
                new ChatMessageModel("m1", "user", "conteúdo da primeira", null, 1L),
                new ChatMessageModel("m2", "assistant", "resposta", "adm-a", 2L),
            ]));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    private async Task<ChatRun> SemearRunAsync(
        string chatId, string status, long tokens, bool empty = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = new ChatRun
        {
            ChatId = chatId,
            UserId = _userId,
            Model = "adm-a",
            Status = status,
            RequestJson = "{}",
            PartialContent = empty ? null : "ok",
            MetaJson = $"{{\"usage\":{{\"promptTokens\":{tokens},"
                + $"\"completionTokens\":{tokens},\"costUsd\":0.01}}}}",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private async Task SemearEventoAsync(string runId, int seq, string type, string payload)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ChatRunEvents.Add(new ChatRunEvent
        {
            RunId = runId, Seq = seq, Type = type,
            PayloadJson = payload, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync();
    }

    // ---------------- RF-001/RF-002: heurística ----------------

    [Test]
    public async Task Insight_Heuristica_SizeCategoryIssues()
    {
        UseToken(_userToken);
        var chat = await CriarChatAsync("fix: corrige bug no login");
        var run = await SemearRunAsync(chat.Id, ChatRunStatus.Completed, 40_000);
        for (var i = 0; i < 3; i++)
        {
            await SemearEventoAsync(run.Id, i + 1, "tool_result",
                "{\"result\":\"error: boom\"}");
        }

        var insight = await _client.GetFromJsonAsync<ChatInsightResponse>(
            $"/api/v1/chats/{chat.Id}/insights");
        Assert.Multiple(() =>
        {
            // 40k in + 40k out = 80k → M; título com "bug" → bugfix.
            Assert.That(insight!.SizeClass, Is.EqualTo("M"));
            Assert.That(insight.Category, Is.EqualTo("bugfix"));
            Assert.That(insight.Metrics.Runs, Is.EqualTo(1));
            Assert.That(insight.Metrics.UserMessages, Is.EqualTo(1));
            Assert.That(insight.Issues.Select(i => i.Label),
                Does.Contain("tool_failure"));
            Assert.That(insight.Timeline, Is.Not.Empty);
            Assert.That(insight.Analysis, Is.Null);
        });
    }

    [Test]
    public async Task Insight_ChatVazio_XsUncategorized()
    {
        UseToken(_userToken);
        var chat = await CriarChatAsync("chat qualquer");
        var insight = await _client.GetFromJsonAsync<ChatInsightResponse>(
            $"/api/v1/chats/{chat.Id}/insights");
        Assert.Multiple(() =>
        {
            Assert.That(insight!.SizeClass, Is.EqualTo("XS"));
            Assert.That(insight.Category, Is.EqualTo("uncategorized"));
            Assert.That(insight.Issues, Is.Empty);
        });
    }

    [Test]
    public async Task Insight_ChatAlheio_404()
    {
        UseToken(_userToken);
        var chat = await CriarChatAsync("privado");
        var other = await SignUpAsync("Other", "other@insights.local");
        UseToken(other.Token);
        Assert.That((await _client.GetAsync(
            $"/api/v1/chats/{chat.Id}/insights")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---------------- RF-003: generate ----------------

    [Test]
    public async Task Generate_SemModelo_422()
    {
        UseToken(_userToken);
        var chat = await CriarChatAsync("sem modelo");
        var other = await SignUpAsync("NoModel", "nomodel@insights.local");
        UseToken(other.Token);
        var own = await CriarChatAsync("do outro");
        var response = await _client.PostAsync(
            $"/api/v1/chats/{own.Id}/insights/generate", null);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        _ = chat;
    }

    [Test]
    public async Task Generate_ComModelo_PersisteAnalise()
    {
        UseToken(_userToken);
        // tasks → adm-a (mock Ollama responde a análise JSON).
        Assert.That((await _client.PutAsJsonAsync("/api/v1/configs/user/model-roles",
            new UpdateModelRolesRequest(new Dictionary<string, List<string>>
            {
                ["tasks"] = ["adm-a"],
            }))).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var chat = await CriarChatAsync("com modelo");
        await SemearRunAsync(chat.Id, ChatRunStatus.Completed, 1_000);

        var generated = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/insights/generate", null);
        Assert.That(generated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await generated.Content.ReadAsStringAsync());
        var insight = await generated.Content.ReadFromJsonAsync<ChatInsightResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(insight!.Analysis, Is.Not.Null);
            Assert.That(insight.Analysis!.Summary, Is.EqualTo("sessão ok"));
            Assert.That(insight.Analysis.ModelId, Is.EqualTo("adm-a"));
            Assert.That(insight.Analysis.ActionItems[0].Kind, Is.EqualTo("skill"));
            Assert.That(insight.Analysis.ImprovedPrompt, Is.EqualTo("melhore o prompt"));
        });

        // GET subsequente devolve a análise persistida.
        var again = await _client.GetFromJsonAsync<ChatInsightResponse>(
            $"/api/v1/chats/{chat.Id}/insights");
        Assert.That(again!.Analysis!.Summary, Is.EqualTo("sessão ok"));
    }

    // ---------------- RF-005: admin summary ----------------

    [Test]
    public async Task Summary_Admin_AgregaDistribuicao()
    {
        UseToken(_userToken);
        var chat = await CriarChatAsync("xl chat");
        await SemearRunAsync(chat.Id, ChatRunStatus.Completed, 400_000);
        await _client.GetFromJsonAsync<ChatInsightResponse>(
            $"/api/v1/chats/{chat.Id}/insights"); // materializa

        UseToken(_adminToken);
        var summary = await _client.GetFromJsonAsync<AdminInsightsSummary>(
            "/api/v1/admin/insights/summary?days=1");
        Assert.Multiple(() =>
        {
            Assert.That(summary!.SizeDistribution["XL"], Is.GreaterThanOrEqualTo(1));
            Assert.That(summary.UnhealthyChats, Is.GreaterThanOrEqualTo(1));
            Assert.That(summary.ByCategory, Is.Not.Empty);
        });
    }

    [Test]
    public async Task Summary_NaoAdmin_403()
    {
        UseToken(_userToken);
        Assert.That((await _client.GetAsync(
            "/api/v1/admin/insights/summary")).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));
    }
}
