using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261014-context-maintenance-v2 RF-003: <c>history://</c> resolve
/// o histórico persistido (inteiro, tail:n, msg:id, call:id) com RBAC do
/// dono e teto de 32KB.
/// </summary>
[TestFixture]
public class HistoryResourceTests
{
    private string _dir = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"owui-hist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "t.db");
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_dir)) { Directory.Delete(_dir, true); } }
        catch { /* ignore */ }
    }

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private async Task<(AppDbContext Db, string ChatId)> SeedChatAsync(
        int messages = 5, string userId = "u1")
    {
        var db = NewDb();
        db.Users.Add(new User
        {
            Id = userId, Name = "u", Email = "u@x", PasswordHash = "x", Role = "user",
        });
        var chat = new Chat { Id = "chat1", UserId = userId, Title = "t" };
        db.Chats.Add(chat);
        for (var i = 0; i < messages; i++)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                Id = $"m{i}", ChatId = chat.Id,
                Role = i % 2 == 0 ? "user" : "tool",
                Content = $"conteúdo {i}", Position = i,
                ToolCallId = i == 3 ? "call-3" : null,
                Timestamp = 1700000000 + i,
            });
        }
        await db.SaveChangesAsync();
        return (db, chat.Id);
    }

    [Test]
    public async Task Resolve_Inteiro_DevolveTranscript()
    {
        var (db, chatId) = await SeedChatAsync(5);
        var svc = new HistoryResourceService(db);

        var (text, err) = await svc.ResolveAsync($"history://{chatId}", "u1", null, default);

        Assert.That(err, Is.Null);
        Assert.That(text, Does.Contain("[0] user: conteúdo 0").And.Contain("[4] user: conteúdo 4"));
    }

    [Test]
    public async Task Resolve_Tail_DevolveUltimas()
    {
        var (db, chatId) = await SeedChatAsync(6);
        var svc = new HistoryResourceService(db);

        var (text, _) = await svc.ResolveAsync($"history://{chatId}/tail:2", "u1", null, default);

        Assert.That(text, Does.Contain("conteúdo 5").And.Contain("conteúdo 4"));
        Assert.That(text, Does.Not.Contain("conteúdo 0"));
    }

    [Test]
    public async Task Resolve_MsgECall_SelecionamUma()
    {
        var (db, chatId) = await SeedChatAsync(5);
        var svc = new HistoryResourceService(db);

        var (byMsg, _) = await svc.ResolveAsync($"history://{chatId}/msg:m2", "u1", null, default);
        var (byCall, _) = await svc.ResolveAsync($"history://{chatId}/call:call-3", "u1", null, default);

        Assert.Multiple(() =>
        {
            Assert.That(byMsg, Does.Contain("conteúdo 2").And.Not.Contain("conteúdo 1"));
            Assert.That(byCall, Does.Contain("conteúdo 3").And.Not.Contain("conteúdo 2"));
        });
    }

    [Test]
    public async Task Resolve_Current_UsaChatDaRun()
    {
        var (db, _) = await SeedChatAsync(3);
        var svc = new HistoryResourceService(db);

        var (text, err) = await svc.ResolveAsync("history://current/tail:1", "u1", "chat1", default);

        Assert.That(err, Is.Null);
        Assert.That(text, Does.Contain("conteúdo 2").And.Not.Contain("conteúdo 0"));
    }

    [Test]
    public async Task Resolve_OutroUsuario_NaoVaza()
    {
        var (db, chatId) = await SeedChatAsync(3);
        var svc = new HistoryResourceService(db);

        var (text, err) = await svc.ResolveAsync($"history://{chatId}", "u2", null, default);

        Assert.That(text, Is.Null);
        Assert.That(err, Does.Contain("não encontrado"));
    }

    [Test]
    public async Task Resolve_SelecaoInvalida_ErroLegivel()
    {
        var (db, chatId) = await SeedChatAsync(2);
        var svc = new HistoryResourceService(db);

        var (_, err) = await svc.ResolveAsync($"history://{chatId}/foo:1", "u1", null, default);
        var (_, err2) = await svc.ResolveAsync($"history://{chatId}/msg:nope", "u1", null, default);

        Assert.Multiple(() =>
        {
            Assert.That(err, Does.Contain("inválido"));
            Assert.That(err2, Does.Contain("não existe"));
        });
    }

    [Test]
    public async Task Resolve_TruncaEm32KB()
    {
        var db = NewDb();
        db.Users.Add(new User
        {
            Id = "u1", Name = "u", Email = "u@x", PasswordHash = "x", Role = "user",
        });
        db.Chats.Add(new Chat { Id = "big", UserId = "u1", Title = "t" });
        for (var i = 0; i < 300; i++)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                ChatId = "big", Role = "user",
                Content = new string('x', 400), Position = i,
                Timestamp = 1700000000 + i,
            });
        }
        await db.SaveChangesAsync();
        var svc = new HistoryResourceService(db);

        var (text, _) = await svc.ResolveAsync("history://big", "u1", null, default);

        Assert.Multiple(() =>
        {
            Assert.That(text!.Length, Is.LessThanOrEqualTo(HistoryResourceService.MaxBytes + 200));
            Assert.That(text, Does.Contain("truncado"));
        });
    }
}
