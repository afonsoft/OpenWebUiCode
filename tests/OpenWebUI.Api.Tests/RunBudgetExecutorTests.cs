using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura do veto de budget dentro do tool loop
/// (SPEC-20261023-run-cost-budgets RF-003): Ollama fake (HttpListener)
/// que pede a tool "eco" no 1º round — o check do 2º round enxerga o
/// custo acumulado e veta/avisa antes da próxima chamada ao provider.
/// </summary>
[TestFixture, IsolateEnvironment]
public class RunBudgetExecutorTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _mockBase = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;
    private string _toolId = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-budgetrun-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mockBase = StartMock();

        _admin = await SignUpAsync("Admin", "admin@budgetrun.local");
        UseToken(_admin.Token);
        var updated = await _client.PostAsJsonAsync(
            "/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        var conn = await _client.PostAsJsonAsync(
            "/api/v1/configs/connections",
            new ConnectionsConfig([_mockBase], [], []));
        Assert.That(conn.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        _client.DefaultRequestHeaders.Authorization = null;
        _user = await SignUpAsync("User", "user@budgetrun.local");

        // Tool HTTP de teste — registrada só para forçar o caminho do
        // tool loop (sem tool o check de budget não dispara). Criada
        // como o usuário: LoadEnabledAsync filtra por dono.
        UseToken(_user.Token);
        var spec = "{\"type\":\"function\",\"function\":{\"name\":\"eco\","
            + "\"description\":\"Eco\",\"parameters\":{\"type\":\"object\"}}}";
        var tool = await _client.PostAsJsonAsync("/api/v1/tools/",
            new ToolUpsertRequest("Eco", "eco", spec, $"{_mockBase}/tool/eco"));
        Assert.That(tool.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await tool.Content.ReadAsStringAsync());
        _toolId = (await tool.Content.ReadFromJsonAsync<ToolResponse>())!.Id;

        // Modelo com custo alto: 15 tokens/round → ownUsd ≈ 0.015.
        await using var db = CreateDb();
        db.ModelEntries.Add(new ModelEntry
        {
            Id = "fake:1", Name = "fake:1", UserId = "public",
            MetaJson = """{"cost":{"input":1000,"output":1000}}""",
        });
        await db.SaveChangesAsync();
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
        for (var i = 0; i < 20; i++)
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
            throw new InvalidOperationException("Sem porta livre.");
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
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            var body = string.Empty;
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                body = await reader.ReadToEndAsync(ct);
            }
            var path = ctx.Request.Url!.AbsolutePath;
            string json;
            if (path == "/api/tags")
            {
                json = "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}";
            }
            else if (path == "/tool/eco")
            {
                json = "eco-ok";
            }
            else
            {
                // /api/chat: 1º round pede eco; rounds seguintes
                // (tool_call_id no histórico) devolvem conteúdo final.
                json = body.Contains("\"tool_call_id\"",
                        StringComparison.Ordinal)
                    || !body.Contains("\"tools\"", StringComparison.Ordinal)
                    ? "{\"message\":{\"role\":\"assistant\","
                        + "\"content\":\"fim\"},\"done\":true,"
                        + "\"prompt_eval_count\":10,\"eval_count\":5}"
                    : "{\"message\":{\"role\":\"assistant\",\"content\":\"\","
                        + "\"tool_calls\":[{\"id\":\"c1\",\"function\":{"
                        + "\"name\":\"eco\",\"arguments\":{}}}]},\"done\":true,"
                        + "\"prompt_eval_count\":10,\"eval_count\":5}";
            }
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private async Task SetPolicyAsync(double? usdPerRun = null,
        double? usdPerMonth = null, long? tokensPerMonth = null)
    {
        await using var db = CreateDb();
        db.BudgetPolicies.RemoveRange(db.BudgetPolicies.Where(
            p => p.Subject == $"user:{_user.User.Id}"));
        // Ledger do usuário zera — totais de mês ficam determinísticos
        // entre testes.
        db.RunUsageRecords.RemoveRange(db.RunUsageRecords.Where(
            r => r.UserId == _user.User.Id));
        db.BudgetPolicies.Add(new BudgetPolicy
        {
            Subject = $"user:{_user.User.Id}",
            UsdPerRun = usdPerRun,
            UsdPerMonth = usdPerMonth,
            TokensPerMonth = tokensPerMonth,
        });
        await db.SaveChangesAsync();
    }

    private async Task<(ChatRunResponse run, string chatId)> RunChatAsync(
        bool withTool)
    {
        UseToken(_user.Token);
        var chat = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("c", ["fake:1"], []));
        var chatId = (await chat.Content
            .ReadFromJsonAsync<ChatResponse>())!.Id;
        if (withTool)
        {
            // Tool loop só roda com workspace binding + modo build
            // (mesma preparação do ToolStreamingTests).
            using var scope = _factory.Services.CreateScope();
            var config = scope.ServiceProvider
                .GetRequiredService<ConfigService>();
            await config.SetAsync(
                $"chat:{chatId}:workspace.repo",
                new WorkspaceRepoBinding("a/b", "main",
                    "repos/a__b", null, null),
                default);
            var patch = await _client.PatchAsJsonAsync(
                $"/api/v1/chats/{chatId}",
                new ChatPatchRequest(null, Mode: "build"));
            Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                await patch.Content.ReadAsStringAsync());
        }
        var enq = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest("oi", "fake:1",
                ToolIds: withTool ? [_toolId] : null));
        Assert.That(enq.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await enq.Content.ReadAsStringAsync());
        var run = (await enq.Content
            .ReadFromJsonAsync<ChatRunResponse>())!;
        var limite = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < limite)
        {
            var cur = await _client.GetFromJsonAsync<ChatRunResponse>(
                $"/api/v1/chats/{chatId}/runs/{run.Id}");
            if (cur!.Status is not ("queued" or "running"))
            {
                return (cur, chatId);
            }
            await Task.Delay(150);
        }
        Assert.Fail("run não finalizou em 60s");
        return (null!, chatId);
    }

    [Test]
    public async Task PerRunCap_VetoesSecondRound()
    {
        await SetPolicyAsync(usdPerRun: 1e-9);
        var (run, _) = await RunChatAsync(withTool: true);
        Assert.That(run.Status, Is.EqualTo("failed"));
        Assert.That(run.Error, Does.Contain("budget_exceeded"));
    }

    [Test]
    public async Task MonthTokensCap_VetoesSecondRound()
    {
        await SetPolicyAsync(tokensPerMonth: 5);
        var (run, _) = await RunChatAsync(withTool: true);
        Assert.That(run.Status, Is.EqualTo("failed"));
        Assert.That(run.Error, Does.Contain("budget_exceeded"));
    }

    [Test]
    public async Task MonthWarn_EmitsBudgetWarningAndCompletes()
    {
        // usdPerMonth=0.018 → após o 1º round (own≈0.015, 83%) o check
        // emite budget_warning e a run segue até completar.
        await SetPolicyAsync(usdPerMonth: 0.018);
        var (run, _) = await RunChatAsync(withTool: true);
        Assert.That(run.Status, Is.EqualTo("completed"));

        await using var db = CreateDb();
        var warned = await db.ChatRunEvents.AsNoTracking()
            .AnyAsync(e => e.RunId == run.Id
                && e.Type == RunEventWriter.Types.BudgetWarning);
        Assert.That(warned, Is.True);
    }

    [Test]
    public async Task CompletedRun_WritesUsageLedgerRecord()
    {
        await SetPolicyAsync(usdPerMonth: 1000);
        var (run, _) = await RunChatAsync(withTool: true);
        Assert.That(run.Status, Is.EqualTo("completed"));

        await using var db = CreateDb();
        var rec = await db.RunUsageRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RunId == run.Id);
        Assert.That(rec, Is.Not.Null);
        Assert.That(rec!.PromptTokens, Is.EqualTo(20));
    }
}
