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
/// Dashboard de uso admin (SPEC-20261102-admin-analytics-v2): totais,
/// série diária, agregado por modelo, ranking por usuário com cap
/// efetivo, top chats enriquecidos, validação de janela e 403.
/// </summary>
[TestFixture, IsolateEnvironment]
public class AdminUsageTests
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
            $"openwebui-adminusage-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin", "admin@adminusage.local");
        UseToken(_admin.Token);
        var updated = await _client.PostAsJsonAsync(
            "/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
        _user = await SignUpAsync("User", "user@adminusage.local");
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

    private async Task SeedRunsAsync(params RunUsageRecord[] runs)
    {
        await using var db = CreateDb();
        // O fixture compartilha a base entre testes — zera o ledger para
        // asserções de totais exatos (só estes testes escrevem nele).
        db.RunUsageRecords.RemoveRange(db.RunUsageRecords);
        db.RunUsageRecords.AddRange(runs);
        await db.SaveChangesAsync();
    }

    private static long EpochDaysAgo(int daysAgo, int offsetSeconds = 0) =>
        DateTimeOffset.UtcNow.AddDays(-daysAgo).ToUnixTimeSeconds()
            + offsetSeconds;

    // Covers AC: admin autenticado recebe totals/series/models/users/
    // topChats batendo com os RunUsageRecords seedados; runs filhas
    // (ParentRunId) somam no dono.
    [Test]
    public async Task AdminUsage_Enriched_AggregatesWindow()
    {
        await SeedRunsAsync(
            new RunUsageRecord
            {
                RunId = "au-root", ChatId = "au-chat-a",
                UserId = _user.User.Id, ModelId = "gpt",
                PromptTokens = 100, CompletionTokens = 50,
                ReasoningTokens = 10, CostUsd = 0.25,
                CreatedAt = EpochDaysAgo(0, -3600),
            },
            new RunUsageRecord
            {
                RunId = "au-child", ChatId = "au-chat-a",
                UserId = _user.User.Id, ModelId = null,
                ParentRunId = "au-root",
                PromptTokens = 60, CompletionTokens = 20,
                CostUsd = 0.10, CreatedAt = EpochDaysAgo(0, -3500),
            },
            new RunUsageRecord
            {
                RunId = "au-old", ChatId = "au-chat-b",
                UserId = _user.User.Id, ModelId = "gpt",
                PromptTokens = 200, CompletionTokens = 100,
                CostUsd = 0.40, CreatedAt = EpochDaysAgo(1),
            },
            new RunUsageRecord
            {
                RunId = "au-outside", ChatId = "au-chat-c",
                UserId = _user.User.Id, ModelId = "gpt",
                PromptTokens = 999, CostUsd = 99.0,
                CreatedAt = EpochDaysAgo(120),
            });

        UseToken(_admin.Token);
        var res = await _client.GetAsync("/api/v1/admin/usage?days=30");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await res.Content.ReadAsStringAsync());
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var totals = root.GetProperty("totals");
        Assert.Multiple(() =>
        {
            Assert.That(totals.GetProperty("runs").GetInt32(),
                Is.GreaterThanOrEqualTo(3));
            Assert.That(totals.GetProperty("activeUsers").GetInt32(),
                Is.GreaterThanOrEqualTo(1));
            Assert.That(root.GetProperty("series").GetArrayLength(),
                Is.GreaterThanOrEqualTo(2));
            Assert.That(root.GetProperty("models").GetArrayLength(),
                Is.EqualTo(2));
        });

        var gpt = root.GetProperty("models").EnumerateArray()
            .First(m => m.GetProperty("modelId").GetString() == "gpt");
        var unknown = root.GetProperty("models").EnumerateArray()
            .First(m => m.GetProperty("modelId").GetString() == "unknown");
        Assert.Multiple(() =>
        {
            Assert.That(gpt.GetProperty("runs").GetInt32(),
                Is.GreaterThanOrEqualTo(2));
            Assert.That(unknown.GetProperty("runs").GetInt32(),
                Is.EqualTo(1));
            // Modelo nulo agrupa como "unknown" (AC).
            Assert.That(gpt.GetProperty("tokens").GetInt64(),
                Is.GreaterThan(unknown.GetProperty("tokens").GetInt64()));
        });

        var me = root.GetProperty("users").EnumerateArray()
            .First(u => u.GetProperty("userId").GetString()
                == _user.User.Id);
        Assert.Multiple(() =>
        {
            Assert.That(me.GetProperty("email").GetString(),
                Is.EqualTo("user@adminusage.local"));
            Assert.That(me.GetProperty("promptTokens").GetInt64(),
                Is.EqualTo(360));
            Assert.That(me.GetProperty("completionTokens").GetInt64(),
                Is.EqualTo(170));
            Assert.That(me.GetProperty("reasoningTokens").GetInt64(),
                Is.EqualTo(10));
            // Split + filha somada: 360+170+10 = 540.
            Assert.That(me.GetProperty("tokens").GetInt64(),
                Is.EqualTo(540));
            Assert.That(me.GetProperty("runs").GetInt32(), Is.EqualTo(3));
            Assert.That(me.GetProperty("costUsd").GetDouble(),
                Is.EqualTo(0.75).Within(1e-6));
        });
    }

    // Covers RF-003/AC: usuário com policy TokensPerMonth recebe
    // capTokensPerMonth e usedPct correto na linha do ranking.
    [Test]
    public async Task AdminUsage_Policy_ShowsCapAndUsedPct()
    {
        await using (var db = CreateDb())
        {
            db.BudgetPolicies.RemoveRange(db.BudgetPolicies.Where(
                p => p.Subject == $"user:{_user.User.Id}"
                    || p.Subject == "global"));
            db.BudgetPolicies.Add(new BudgetPolicy
            {
                Subject = $"user:{_user.User.Id}",
                TokensPerMonth = 10000, UsdPerMonth = 2.0,
            });
            await db.SaveChangesAsync();
        }
        await SeedRunsAsync(new RunUsageRecord
        {
            RunId = "cap-run", ChatId = "cap-chat",
            UserId = _user.User.Id, ModelId = "gpt",
            PromptTokens = 2000, CompletionTokens = 500,
            CostUsd = 0.5, CreatedAt = EpochDaysAgo(0, -60),
        });

        UseToken(_admin.Token);
        var res = await _client.GetAsync(
            $"/api/v1/admin/usage?days=1&userId={_user.User.Id}");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await res.Content.ReadAsStringAsync());
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var me = doc.RootElement.GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("userId").GetString()
                == _user.User.Id);

        Assert.Multiple(() =>
        {
            Assert.That(me.GetProperty("capTokensPerMonth").GetInt64(),
                Is.EqualTo(10000));
            Assert.That(me.GetProperty("capUsdPerMonth").GetDouble(),
                Is.EqualTo(2.0));
            // max(2500/10000, 0.5/2.0)·100 = 25.0
            Assert.That(me.GetProperty("usedPct").GetDouble(),
                Is.EqualTo(25.0).Within(0.1));
        });
    }

    // Covers RF-003: ausência de policy → campos de cap nulos.
    [Test]
    public async Task AdminUsage_NoPolicy_CapFieldsNull()
    {
        await using (var db = CreateDb())
        {
            db.BudgetPolicies.RemoveRange(db.BudgetPolicies.Where(
                p => p.Subject == $"user:{_user.User.Id}"
                    || p.Subject == "global"));
            await db.SaveChangesAsync();
        }
        await SeedRunsAsync(new RunUsageRecord
        {
            RunId = "nocap-run", ChatId = "nocap-chat",
            UserId = _user.User.Id, ModelId = "gpt",
            PromptTokens = 10, CostUsd = 0.01,
            CreatedAt = EpochDaysAgo(0, -30),
        });

        UseToken(_admin.Token);
        var res = await _client.GetAsync(
            $"/api/v1/admin/usage?days=1&userId={_user.User.Id}");
        var me = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
            .RootElement.GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("userId").GetString()
                == _user.User.Id);

        Assert.Multiple(() =>
        {
            Assert.That(me.GetProperty("capTokensPerMonth").ValueKind,
                Is.EqualTo(JsonValueKind.Null));
            Assert.That(me.GetProperty("capUsdPerMonth").ValueKind,
                Is.EqualTo(JsonValueKind.Null));
            Assert.That(me.GetProperty("usedPct").ValueKind,
                Is.EqualTo(JsonValueKind.Null));
        });
    }

    // Covers RF-004: topChats traz título (join Chats) e email; chat
    // inexistente → title null.
    [Test]
    public async Task AdminUsage_TopChats_EnrichedWithTitleAndEmail()
    {
        UseToken(_user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("Chat admin usage", ["llama3"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var chatId = JsonDocument.Parse(await created.Content
            .ReadAsStringAsync()).RootElement.GetProperty("id")
            .GetString()!;

        await SeedRunsAsync(
            new RunUsageRecord
            {
                RunId = "tc-run", ChatId = chatId,
                UserId = _user.User.Id, ModelId = "gpt",
                PromptTokens = 1000, CompletionTokens = 500,
                CostUsd = 0.9, CreatedAt = EpochDaysAgo(0, -60),
            });

        UseToken(_admin.Token);
        var res = await _client.GetAsync(
            "/api/v1/admin/usage?days=7&top=5");
        var chats = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
            .RootElement.GetProperty("topChats").EnumerateArray().ToList();
        var top = chats.First(c => c.GetProperty("chatId").GetString()
            == chatId);

        Assert.Multiple(() =>
        {
            Assert.That(top.GetProperty("title").GetString(),
                Is.EqualTo("Chat admin usage"));
            Assert.That(top.GetProperty("email").GetString(),
                Is.EqualTo("user@adminusage.local"));
            Assert.That(top.GetProperty("tokens").GetInt64(),
                Is.EqualTo(1500));
            Assert.That(top.GetProperty("costUsd").GetDouble(),
                Is.EqualTo(0.9).Within(1e-6));
        });
    }

    // Covers AC: janela sem runs → totais zerados e arrays vazios (a UI
    // mostra admin.no_data sem erro).
    [Test]
    public async Task AdminUsage_EmptyWindow_ZerosAndEmptyArrays()
    {
        UseToken(_admin.Token);
        var res = await _client.GetAsync(
            "/api/v1/admin/usage?from=2020-01-01&to=2020-01-02");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await res.Content.ReadAsStringAsync());
        var root = JsonDocument.Parse(
            await res.Content.ReadAsStringAsync()).RootElement;
        var totals = root.GetProperty("totals");

        Assert.Multiple(() =>
        {
            Assert.That(totals.GetProperty("runs").GetInt32(),
                Is.EqualTo(0));
            Assert.That(totals.GetProperty("promptTokens").GetInt64(),
                Is.EqualTo(0));
            Assert.That(totals.GetProperty("costUsd").GetDouble(),
                Is.EqualTo(0));
            Assert.That(root.GetProperty("series").GetArrayLength(),
                Is.EqualTo(0));
            Assert.That(root.GetProperty("models").GetArrayLength(),
                Is.EqualTo(0));
            Assert.That(root.GetProperty("users").GetArrayLength(),
                Is.EqualTo(0));
            Assert.That(root.GetProperty("topChats").GetArrayLength(),
                Is.EqualTo(0));
        });
    }

    // Covers RF-001 edge cases: days fora de 1..365 e to<from → 400.
    [Test]
    public async Task AdminUsage_InvalidWindow_400()
    {
        UseToken(_admin.Token);
        Assert.Multiple(async () =>
        {
            Assert.That((await _client.GetAsync(
                "/api/v1/admin/usage?days=0")).StatusCode,
                Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await _client.GetAsync(
                "/api/v1/admin/usage?days=999")).StatusCode,
                Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await _client.GetAsync(
                "/api/v1/admin/usage?from=2026-11-02&to=2026-10-01"))
                .StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    // Covers RF-001: ?days= gera janela de N dias UTC terminando no fim
    // de hoje.
    [Test]
    public async Task AdminUsage_DaysPreset_SpansRequestedWindow()
    {
        UseToken(_admin.Token);
        var res = await _client.GetAsync("/api/v1/admin/usage?days=7");
        var root = JsonDocument.Parse(
            await res.Content.ReadAsStringAsync()).RootElement;
        var from = DateTimeOffset.Parse(
            root.GetProperty("from").GetString()!);
        var to = DateTimeOffset.Parse(
            root.GetProperty("to").GetString()!);
        Assert.That((to - from).TotalDays, Is.EqualTo(7));
    }

    // Covers AC: não-admin → 403 (também cobre o invariante de auth).
    [Test]
    public async Task AdminUsage_NonAdmin_403()
    {
        UseToken(_user.Token);
        var denied = await _client.GetAsync("/api/v1/admin/usage?days=7");
        Assert.That(denied.StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));
    }
}
