using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Runs;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura do inbox de atenção persistido (SPEC-20261022-ade-surfaces
/// RF-C1..C4): upsert por chave, list/count/seen, fusão com aprovações
/// pendentes, escrita via <c>AttentionObserver</c> nos eventos
/// <c>run.completed</c>/<c>run.failed</c> e limpeza junto do chat.
/// </summary>
[TestFixture, IsolateEnvironment]
public class AttentionTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-attention-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        var admin = await SignUpAsync("Admin", "admin@attention.local");
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

    // ---------- AttentionService (unitário, sqlite) ----------

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private IServiceScopeFactory Scopes()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite($"Data Source={_dbPath}"));
        return services.BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }

    private static string UKey() => $"key-{Guid.NewGuid():N}";

    [Test]
    public async Task Raise_Upsert_MesmaChave_NaoDuplica()
    {
        using var db = NewDb();
        var key = UKey();
        await AttentionService.RaiseAsync(
            db, "u-att", key, "run_completed", "t1", "c1", default);
        var again = await AttentionService.RaiseAsync(
            db, "u-att", key, "run_failed", "t2", "c1", default);

        var items = await AttentionService.ListAsync(db, "u-att", default);
        Assert.Multiple(() =>
        {
            Assert.That(items.Count(i => i.Key == key), Is.EqualTo(1));
            Assert.That(items.Single(i => i.Key == key).Kind,
                Is.EqualTo("run_failed"));
            Assert.That(items.Single(i => i.Key == key).Title,
                Is.EqualTo("t2"));
        });
    }

    [Test]
    public async Task MarkSeen_Remove_SoDoDono()
    {
        using var db = NewDb();
        var key = UKey();
        await AttentionService.RaiseAsync(
            db, "u-att", key, "terminal", "term", null, default);

        // Outro usuário não consegue dispensar.
        Assert.That(await AttentionService.MarkSeenAsync(
            db, "outro", key, default), Is.False);
        Assert.That(await AttentionService.MarkSeenAsync(
            db, "u-att", key, default), Is.True);
        Assert.That(await AttentionService.MarkSeenAsync(
            db, "u-att", key, default), Is.False);
    }

    // ---------- AttentionObserver (unitário) ----------

    [Test]
    public async Task Observer_RunCompleted_E_Failed_Levantam_Itens()
    {
        using var db = NewDb();
        await SeedChatAsync(db, "u-obs", "chat-obs", "Chat Obs");

        var observer = new AttentionObserver(
            Scopes(), NullLogger<AttentionObserver>.Instance);

        await observer.OnRunEventAsync(
            new AgentRunEvent("r-ok", "chat-obs", "u-obs",
                "run.completed", "{}"), default);
        await observer.OnRunEventAsync(
            new AgentRunEvent("r-bad", "chat-obs", "u-obs",
                "run.failed", "{}"), default);
        // Ignorados: stopped, sem user, sem chat.
        await observer.OnRunEventAsync(
            new AgentRunEvent("r-stop", "chat-obs", "u-obs",
                "run.stopped", "{}"), default);
        await observer.OnRunEventAsync(
            new AgentRunEvent("r-nouser", "chat-obs", null,
                "run.failed", "{}"), default);

        var items = await AttentionService.ListAsync(db, "u-obs", default);
        Assert.Multiple(() =>
        {
            Assert.That(items.Select(i => i.Key),
                Is.EquivalentTo(new[] { "run:r-ok", "run:r-bad" }));
            Assert.That(items.Single(i => i.Key == "run:r-ok").Kind,
                Is.EqualTo("run_completed"));
            Assert.That(items.Single(i => i.Key == "run:r-ok").Title,
                Is.EqualTo("Chat Obs"));
            Assert.That(items.Single(i => i.Key == "run:r-ok").ChatId,
                Is.EqualTo("chat-obs"));
        });
    }

    // ---------- Endpoints ----------

    [Test]
    public async Task Attention_Lista_Conta_E_Ve_Sequencial()
    {
        var auth = await SignUpAsync("List", "list@attention.local");
        UseToken(auth.Token);

        using var db = NewDb();
        var user = await db.Users.SingleAsync(u => u.Email == "list@attention.local");
        await AttentionService.RaiseAsync(
            db, user.Id, $"run:{Guid.NewGuid():N}", "run_completed",
            "run feita", null, default);
        await AttentionService.RaiseAsync(
            db, user.Id, $"term:{Guid.NewGuid():N}", "terminal",
            "term saiu", null, default);

        var items = await _client.GetFromJsonAsync<
            List<AttentionItemResponse>>("/api/v1/attention");
        Assert.That(items, Is.Not.Null);
        Assert.That(items!.Count, Is.EqualTo(2));

        var count = await _client.GetFromJsonAsync<AttentionCountResponse>(
            "/api/v1/chats/attention/count");
        Assert.That(count!.Count, Is.GreaterThanOrEqualTo(2));

        var key = items[0].Key;
        var seen = await _client.PostAsync(
            $"/api/v1/attention/{key}/seen", null);
        Assert.That(seen.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var missing = await _client.PostAsync(
            $"/api/v1/attention/{key}/seen", null);
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Attention_DeleteChat_Limpa_Itens_Do_Chat()
    {
        var auth = await SignUpAsync("Del", "del@attention.local");
        UseToken(auth.Token);

        var chatRes = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("Chat attn", ["llama3"], []));
        Assert.That(chatRes.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await chatRes.Content.ReadAsStringAsync());
        var chat = await chatRes.Content
            .ReadFromJsonAsync<ChatResponse>();

        using var db = NewDb();
        var user = await db.Users.SingleAsync(u => u.Email == "del@attention.local");
        var key = $"run:{Guid.NewGuid():N}";
        await AttentionService.RaiseAsync(
            db, user.Id, key, "run_failed", "run falhou", chat!.Id, default);

        var del = await _client.DeleteAsync($"/api/v1/chats/{chat.Id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var items = await _client.GetFromJsonAsync<
            List<AttentionItemResponse>>("/api/v1/attention");
        Assert.That(items!.Any(i => i.Key == key), Is.False);
    }

    // ---------- helpers ----------

    private static async Task SeedChatAsync(
        AppDbContext db, string userId, string chatId, string title)
    {
        db.Users.Add(new User
        {
            Id = userId,
            Name = userId,
            Email = $"{userId}@x.local",
            PasswordHash = "x",
            Role = "user",
        });
        db.Chats.Add(new Chat
        {
            Id = chatId,
            UserId = userId,
            Title = title,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync();
    }

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
}
