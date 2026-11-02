using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes do data fix retroativo de markup de raciocínio corrompido.</summary>
[TestFixture]
public class CorruptMarkupDataFixTests
{
    private SqliteConnection _conn = null!;
    private AppDbContext _db = null!;

    [SetUp]
    public void SetUp()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private async Task SeedMsgAsync(string content, string versions = "[]")
    {
        if (await _db.Chats.FindAsync("c1") is null)
        {
            _db.Users.Add(new User { Id = "u1", Email = "u@x" });
            _db.Chats.Add(new Chat { Id = "c1", UserId = "u1" });
        }
        _db.ChatMessages.Add(new ChatMessage
        {
            ChatId = "c1",
            Role = "assistant",
            Content = content,
            VersionsJson = versions,
        });
        await _db.SaveChangesAsync();
    }

    [Test]
    public async Task RunAsync_LimpaMensagemCorrompida_EMarcaExecucao()
    {
        await SeedMsgAsync("a<|channel|>b<think>raciocínio</think>c");
        var n = await CorruptMarkupDataFix.RunAsync(_db);
        Assert.That(n, Is.GreaterThanOrEqualTo(1));
        var msg = _db.ChatMessages.Single();
        Assert.That(msg.Content, Does.Not.Contain("<|channel|>"));
        Assert.That(msg.Content, Does.Not.Contain("<think>"));
        Assert.That(await _db.ConfigEntries.FindAsync(CorruptMarkupDataFix.MarkerKey), Is.Not.Null);
    }

    [Test]
    public async Task RunAsync_Idempotente_SegundaVezZero()
    {
        await SeedMsgAsync("ok<|channel|>x");
        await CorruptMarkupDataFix.RunAsync(_db);
        Assert.That(await CorruptMarkupDataFix.RunAsync(_db), Is.EqualTo(0));
    }

    [Test]
    public async Task RunAsync_PreservaConteudoLimpo()
    {
        await SeedMsgAsync("texto normal");
        await CorruptMarkupDataFix.RunAsync(_db);
        Assert.That(_db.ChatMessages.Single().Content, Is.EqualTo("texto normal"));
    }

    [Test]
    public async Task RunAsync_LimpaVersoesAntigas()
    {
        await SeedMsgAsync("limpo",
            """[{"content":"v<|channel|>1","model":"m","timestamp":1}]""");
        await CorruptMarkupDataFix.RunAsync(_db);
        Assert.That(_db.ChatMessages.Single().VersionsJson,
            Does.Not.Contain("<|channel|>"));
    }
}
