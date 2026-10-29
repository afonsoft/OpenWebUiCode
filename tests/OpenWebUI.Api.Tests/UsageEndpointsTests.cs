using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Contrato dos endpoints de uso/budget (SPEC-20261023-run-cost-budgets
/// RF-005): <c>/usage/me</c> do próprio usuário, <c>/admin/usage</c>
/// agregado, CRUD de <c>/admin/budget-policies</c> e o gate 429
/// <c>budget_exceeded</c> no enqueue de mensagem.
/// </summary>
[TestFixture, IsolateEnvironment]
public class UsageEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-usage-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("Providers__MaxAttempts", "1");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin", "admin@usage.local");
        UseToken(_admin.Token);
        var updated = await _client.PostAsJsonAsync(
            "/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
        _user = await SignUpAsync("User", "user@usage.local");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("Providers__MaxAttempts", null);
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
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

    private async Task SeedUsageAsync(string userId, double costUsd,
        long tokens, string chatId = "chat-x", bool clear = false)
    {
        await using var db = CreateDb();
        if (clear)
        {
            // O fixture compartilha a base entre testes — zera o mês do
            // usuário para asserções de totais exatos.
            db.RunUsageRecords.RemoveRange(db.RunUsageRecords.Where(
                r => r.UserId == userId));
        }
        var start = new DateTimeOffset(DateTimeOffset.UtcNow.Year,
            DateTimeOffset.UtcNow.Month, 1, 0, 0, 0,
            TimeSpan.Zero).ToUnixTimeSeconds();
        db.RunUsageRecords.Add(new RunUsageRecord
        {
            RunId = Guid.NewGuid().ToString(), ChatId = chatId,
            UserId = userId, PromptTokens = tokens / 2,
            CompletionTokens = tokens / 2, CostUsd = costUsd,
            Steps = 1, CreatedAt = start + 60,
        });
        await db.SaveChangesAsync();
    }

    // Covers RF-005: /usage/me devolve totais do mês e o cap efetivo.
    [Test]
    public async Task UsageMe_ReturnsMonthTotalsAndCap()
    {
        await using (var db = CreateDb())
        {
            // Testes compartilham o usuário — subject é único na tabela.
            db.BudgetPolicies.RemoveRange(db.BudgetPolicies.Where(
                p => p.Subject == $"user:{_user.User.Id}"));
            db.BudgetPolicies.Add(new BudgetPolicy
            {
                Subject = $"user:{_user.User.Id}", UsdPerMonth = 10.0,
            });
            await db.SaveChangesAsync();
        }
        await SeedUsageAsync(_user.User.Id, 2.5, 1000, clear: true);
        UseToken(_user.Token);

        var doc = JsonDocument.Parse(await (await _client.GetAsync(
            "/api/v1/usage/me")).Content.ReadAsStringAsync());

        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("promptTokens")
                .GetInt64(), Is.EqualTo(500));
            Assert.That(doc.RootElement.GetProperty("completionTokens")
                .GetInt64(), Is.EqualTo(500));
            Assert.That(doc.RootElement.GetProperty("costUsd")
                .GetDouble(), Is.EqualTo(2.5).Within(1e-6));
            Assert.That(doc.RootElement.GetProperty("runs")
                .GetInt32(), Is.EqualTo(1));
            Assert.That(doc.RootElement.GetProperty("cap")
                .GetProperty("usdPerMonth").GetDouble(), Is.EqualTo(10.0));
            Assert.That(doc.RootElement.GetProperty("cap")
                .GetProperty("usedPct").GetDouble(),
                Is.EqualTo(25.0).Within(0.1));
        });
    }

    // Covers info-panel: /usage/chats/{id} devolve totais, agregado por
    // modelo e série por run; chat de outro usuário → 404.
    [Test]
    public async Task ChatUsage_PerChatTotalsModelsAndRuns()
    {
        UseToken(_user.Token);
        var chat = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("Chat usage", ["llama3"], []));
        Assert.That(chat.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await chat.Content.ReadAsStringAsync());
        var chatId = JsonDocument.Parse(await chat.Content
            .ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        await using (var db = CreateDb())
        {
            db.RunUsageRecords.AddRange(
                new RunUsageRecord
                {
                    RunId = "r1", ChatId = chatId, UserId = _user.User.Id,
                    ModelId = "gemini", Steps = 2, PromptTokens = 100,
                    CompletionTokens = 50, CostUsd = 0.1, CreatedAt = 1,
                },
                new RunUsageRecord
                {
                    RunId = "r2", ChatId = chatId, UserId = _user.User.Id,
                    ModelId = "gpt", ParentRunId = "r1", Steps = 1,
                    PromptTokens = 200,
                    CompletionTokens = 80, CostUsd = 0.2, CreatedAt = 2,
                });
            await db.SaveChangesAsync();
        }

        var doc = JsonDocument.Parse(await (await _client.GetAsync(
            $"/api/v1/usage/chats/{chatId}")).Content.ReadAsStringAsync());
        var totals = doc.RootElement.GetProperty("totals");
        var models = doc.RootElement.GetProperty("models");
        var runs = doc.RootElement.GetProperty("runs");
        Assert.Multiple(() =>
        {
            Assert.That(totals.GetProperty("runs").GetInt32(), Is.EqualTo(2));
            Assert.That(totals.GetProperty("promptTokens").GetInt64(), Is.EqualTo(300));
            Assert.That(totals.GetProperty("completionTokens").GetInt64(), Is.EqualTo(130));
            Assert.That(totals.GetProperty("costUsd").GetDouble(), Is.EqualTo(0.3).Within(1e-6));
            Assert.That(models.GetArrayLength(), Is.EqualTo(2));
            Assert.That(models.EnumerateArray().First(
                m => m.GetProperty("modelId").GetString() == "gpt")
                .GetProperty("tokens").GetInt64(), Is.EqualTo(280));
            Assert.That(runs.GetArrayLength(), Is.EqualTo(2));
            Assert.That(runs.EnumerateArray().First()
                .GetProperty("modelId").GetString(), Is.EqualTo("gemini"));
            Assert.That(runs.EnumerateArray().ElementAt(1)
                .GetProperty("parentRunId").GetString(), Is.EqualTo("r1"));
        });

        UseToken(_admin.Token);
        var other = await _client.GetAsync($"/api/v1/usage/chats/{chatId}");
        Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        UseToken(_user.Token);
    }

    // Covers RF-005: /admin/usage agrega por usuário e top chats;
    // não-admin recebe 403.
    [Test]
    public async Task AdminUsage_AdminAggregates_NonAdminForbidden()
    {
        await SeedUsageAsync(_user.User.Id, 1.25, 400, "chat-top");

        UseToken(_admin.Token);
        var doc = JsonDocument.Parse(await (await _client.GetAsync(
            "/api/v1/admin/usage")).Content.ReadAsStringAsync());
        var users = doc.RootElement.GetProperty("users");
        var me = users.EnumerateArray().First(u =>
            u.GetProperty("userId").GetString() == _user.User.Id);

        Assert.Multiple(() =>
        {
            Assert.That(me.GetProperty("email").GetString(),
                Is.EqualTo("user@usage.local"));
            Assert.That(me.GetProperty("costUsd").GetDouble(),
                Is.GreaterThan(0));
            Assert.That(doc.RootElement.GetProperty("topChats")
                .EnumerateArray().Any(c =>
                    c.GetProperty("chatId").GetString() == "chat-top"),
                Is.True);
        });

        UseToken(_user.Token);
        var denied = await _client.GetAsync("/api/v1/admin/usage");
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // Covers RF-005: CRUD de budget-policies — create/list/update/delete,
    // subject inválido e policy vazia rejeitados.
    [Test]
    public async Task BudgetPolicies_CrudLifecycle()
    {
        UseToken(_admin.Token);

        var badSubject = await _client.PostAsJsonAsync(
            "/api/v1/admin/budget-policies",
            new { subject = "team:x", usdPerMonth = 1.0 });
        Assert.That(badSubject.StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest));

        var empty = await _client.PostAsJsonAsync(
            "/api/v1/admin/budget-policies", new { subject = "global" });
        Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var created = await _client.PostAsJsonAsync(
            "/api/v1/admin/budget-policies",
            new { subject = "global", usdPerMonth = 100.0, maxModelTier = 2 });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var policy = JsonDocument.Parse(
            await created.Content.ReadAsStringAsync());
        var policyId = policy.RootElement.GetProperty("id").GetString()!;

        var dup = await _client.PostAsJsonAsync(
            "/api/v1/admin/budget-policies",
            new { subject = "global", usdPerMonth = 5.0 });
        Assert.That(dup.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var updated = await _client.PutAsJsonAsync(
            $"/api/v1/admin/budget-policies/{policyId}",
            new { subject = "global", usdPerMonth = 42.0 });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var listed = JsonDocument.Parse(await (await _client.GetAsync(
            "/api/v1/admin/budget-policies")).Content.ReadAsStringAsync());
        Assert.That(listed.RootElement.EnumerateArray()
            .Single(p => p.GetProperty("subject").GetString() == "global")
            .GetProperty("usdPerMonth").GetDouble(), Is.EqualTo(42.0));

        var deleted = await _client.DeleteAsync(
            $"/api/v1/admin/budget-policies/{policyId}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        UseToken(_user.Token);
        var denied = await _client.GetAsync("/api/v1/admin/budget-policies");
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // Covers RF-002/AC: mês acima do cap → enqueue responde 429 com
    // error_code budget_exceeded.
    [Test]
    public async Task EnqueueMessage_MonthCapExceeded_Returns429()
    {
        await using (var db = CreateDb())
        {
            db.BudgetPolicies.RemoveRange(db.BudgetPolicies.Where(
                p => p.Subject == $"user:{_user.User.Id}"));
            await db.SaveChangesAsync();
        }
        UseToken(_admin.Token);
        var created = await _client.PostAsJsonAsync(
            "/api/v1/admin/budget-policies",
            new { subject = $"user:{_user.User.Id}", usdPerMonth = 0.0001 });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        await SeedUsageAsync(_user.User.Id, 1.0, 100);

        UseToken(_user.Token);
        var chat = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("Chat budget", ["llama3"], []));
        Assert.That(chat.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await chat.Content.ReadAsStringAsync());
        var chatId = JsonDocument.Parse(await chat.Content
            .ReadAsStringAsync()).RootElement.GetProperty("id").GetString();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest("oi", "llama3"));

        Assert.That(response.StatusCode,
            Is.EqualTo((HttpStatusCode)429));
        var doc = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.That(doc.RootElement.GetProperty("error_code").GetString(),
            Is.EqualTo("budget_exceeded"));
    }
    // Cobre os ramos de 404 do CRUD e o cap nulo de /usage/me.
    [Test]
    public async Task BudgetPolicies_UpdateDelete_NotFound()
    {
        UseToken(_admin.Token);
        var put = await _client.PutAsJsonAsync(
            "/api/v1/admin/budget-policies/missing",
            new { subject = "global", usdPerMonth = 1.0 });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var del = await _client.DeleteAsync(
            "/api/v1/admin/budget-policies/missing");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UsageMe_WithoutPolicy_CapNull()
    {
        await using (var db = CreateDb())
        {
            db.BudgetPolicies.RemoveRange(db.BudgetPolicies.Where(
                p => p.Subject == $"user:{_user.User.Id}"
                    || p.Subject == "global"));
            await db.SaveChangesAsync();
        }
        UseToken(_user.Token);
        var res = await _client.GetAsync("/api/v1/usage/me");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var doc = JsonDocument.Parse(
            await res.Content.ReadAsStringAsync());
        Assert.That(doc.RootElement.GetProperty("cap").ValueKind,
            Is.EqualTo(JsonValueKind.Null));
    }

    [Test]
    public async Task AdminUsage_AcceptsWindowParams()
    {
        UseToken(_admin.Token);
        var res = await _client.GetAsync(
            "/api/v1/admin/usage?from=2020-01-01&to=2030-01-01&top=5");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await res.Content.ReadAsStringAsync());
        var doc = JsonDocument.Parse(
            await res.Content.ReadAsStringAsync());
        Assert.That(doc.RootElement.GetProperty("from").GetString(),
            Is.EqualTo("2020-01-01"));
    }
}
