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
/// Ciclo de vida do chat (SPEC-20261115-chat-lifecycle-meta): archive
/// gera meta+resumo e purga o histórico mantendo métricas; unarchive
/// re-injeta o resumo como marcador compaction; delete grava meta
/// deleted e o analytics mostra status/título + popup de resumo.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ChatLifecycleTests
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
            $"openwebui-lifecycle-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin", "admin@lifecycle.local");
        UseToken(_admin.Token);
        var updated = await _client.PostAsJsonAsync(
            "/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
        _user = await SignUpAsync("User", "user@lifecycle.local");
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

    private async Task<string> CreateChatWithMessagesAsync(string title)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var res = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest(title, ["llama3"],
            [
                new ChatMessageModel("m1", "user", "Preciso migrar o banco", null, now),
                new ChatMessageModel("m2", "assistant", "Vou gerar a migration", "llama3", now + 1),
            ]));
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await res.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;
    }

    // Covers RF-002+RF-003: archive grava meta (título+resumo), purga
    // mensagens e mantém métricas; unarchive devolve o resumo como
    // marcador compaction na tela/contexto.
    [Test]
    public async Task ArchiveThenUnarchive_MetaSummaryAndCompactionMarker()
    {
        UseToken(_user.Token);
        var chatId = await CreateChatWithMessagesAsync("Chat para arquivar");

        var archive = await _client.PostAsync(
            $"/api/v1/chats/{chatId}/archive", null);
        Assert.That(archive.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await archive.Content.ReadAsStringAsync());

        await using (var db = CreateDb())
        {
            var meta = await db.ChatArchiveMetas
                .FirstOrDefaultAsync(m => m.ChatId == chatId);
            Assert.Multiple(() =>
            {
                Assert.That(meta, Is.Not.Null);
                Assert.That(meta!.Status, Is.EqualTo("archived"));
                Assert.That(meta.Title, Is.EqualTo("Chat para arquivar"));
                Assert.That(meta.Summary, Is.Not.Null.And.Not.Empty);
                Assert.That(
                    db.ChatMessages.Count(m => m.ChatId == chatId),
                    Is.EqualTo(0), "histórico pesado deve ser purgado");
            });
        }

        var unarchive = await _client.PostAsync(
            $"/api/v1/chats/{chatId}/archive", null);
        Assert.That(unarchive.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = JsonDocument.Parse(
            await unarchive.Content.ReadAsStringAsync()).RootElement;
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("archived").GetBoolean(), Is.False);
            Assert.That(messages, Has.Count.EqualTo(1));
            Assert.That(messages[0].GetProperty("role").GetString(),
                Is.EqualTo("compaction"));
            Assert.That(messages[0].GetProperty("content").GetString(),
                Is.Not.Null.And.Not.Empty);
        });
    }

    // Covers RF-004+RF-005: delete grava meta deleted (mantém métricas);
    // admin/usage reporta status=deleted com o título do meta; o
    // endpoint de lifecycle devolve resumo + métricas para o popup.
    [Test]
    public async Task Delete_KeepsMetricsAndMeta_AnalyticsShowsDeleted()
    {
        UseToken(_user.Token);
        var chatId = await CreateChatWithMessagesAsync("Chat para deletar");

        await using (var seed = CreateDb())
        {
            seed.RunUsageRecords.Add(new RunUsageRecord
            {
                RunId = $"lc-{chatId}", ChatId = chatId,
                UserId = _user.User.Id, ModelId = "llama3",
                PromptTokens = 500, CompletionTokens = 200,
                CostUsd = 0.42,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            await seed.SaveChangesAsync();
        }

        var del = await _client.DeleteAsync($"/api/v1/chats/{chatId}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await del.Content.ReadAsStringAsync());

        await using (var db = CreateDb())
        {
            Assert.Multiple(async () =>
            {
                Assert.That(await db.Chats.AnyAsync(c => c.Id == chatId),
                    Is.False);
                var meta = await db.ChatArchiveMetas
                    .FirstOrDefaultAsync(m => m.ChatId == chatId);
                Assert.That(meta, Is.Not.Null);
                Assert.That(meta!.Status, Is.EqualTo("deleted"));
                Assert.That(meta.Title, Is.EqualTo("Chat para deletar"));
                Assert.That(meta.Summary, Is.Not.Null.And.Not.Empty);
                Assert.That(await db.RunUsageRecords
                        .CountAsync(r => r.ChatId == chatId),
                    Is.EqualTo(1), "métricas devem ser preservadas");
            });
        }

        UseToken(_admin.Token);
        var usage = await _client.GetAsync("/api/v1/admin/usage?days=7");
        var topChats = JsonDocument.Parse(await usage.Content
            .ReadAsStringAsync()).RootElement
            .GetProperty("topChats").EnumerateArray().ToList();
        var row = topChats.FirstOrDefault(c =>
            c.GetProperty("chatId").GetString() == chatId);
        Assert.That(row.ValueKind, Is.Not.EqualTo(JsonValueKind.Undefined));
        Assert.Multiple(() =>
        {
            Assert.That(row.GetProperty("status").GetString(),
                Is.EqualTo("deleted"));
            Assert.That(row.GetProperty("title").GetString(),
                Is.EqualTo("Chat para deletar"));
        });

        var lifecycle = await _client.GetAsync(
            $"/api/v1/admin/chats/{chatId}/lifecycle");
        Assert.That(lifecycle.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await lifecycle.Content.ReadAsStringAsync());
        var lc = JsonDocument.Parse(await lifecycle.Content
            .ReadAsStringAsync()).RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(lc.GetProperty("status").GetString(),
                Is.EqualTo("deleted"));
            Assert.That(lc.GetProperty("summary").GetString(),
                Is.Not.Null.And.Not.Empty);
            Assert.That(lc.GetProperty("runs").GetInt32(), Is.EqualTo(1));
            Assert.That(lc.GetProperty("costUsd").GetDouble(),
                Is.EqualTo(0.42).Within(1e-6));
        });
    }

    // Covers AC: chat vivo segue com status active e o endpoint de
    // lifecycle responde summary null; não-admin leva 403.
    [Test]
    public async Task Lifecycle_ActiveChat_And_ForbiddenForNonAdmin()
    {
        UseToken(_user.Token);
        var chatId = await CreateChatWithMessagesAsync("Chat ativo");

        UseToken(_admin.Token);
        var res = await _client.GetAsync(
            $"/api/v1/admin/chats/{chatId}/lifecycle");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var lc = JsonDocument.Parse(await res.Content
            .ReadAsStringAsync()).RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(lc.GetProperty("status").GetString(),
                Is.EqualTo("active"));
            Assert.That(lc.GetProperty("title").GetString(),
                Is.EqualTo("Chat ativo"));
            Assert.That(lc.GetProperty("summary").ValueKind,
                Is.EqualTo(JsonValueKind.Null));
        });

        UseToken(_user.Token);
        var forbidden = await _client.GetAsync(
            $"/api/v1/admin/chats/{chatId}/lifecycle");
        Assert.That(forbidden.StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));
    }
}
