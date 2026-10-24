using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Gateway;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Entrega da resposta final à plataforma
/// (SPEC-20261023-messaging-gateway E/RF-004): run completada num chat de
/// sessão envia o texto final; falha vira aviso; deliveries out auditadas;
/// chat sem sessão é ignorado; segredo ausente marca delivery failed.
/// </summary>
[TestFixture]
public class GatewayReplyTests
{
    private string _root = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-gwreply-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "test.db");
        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Users.Add(new User
        {
            Id = "u1", Name = "U", Email = "u@t.local", Role = "user",
        });
        db.Chats.Add(new Chat
        {
            Id = "chat1", UserId = "u1", Title = "gateway:telegram:ana",
            CreatedAt = 1, UpdatedAt = 1,
        });
        db.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    /// <summary>Monta o notifier com adapter espião e segredo opcional no cofre.</summary>
    private async Task<(GatewayReplyService Svc, SpyAdapter Spy, AppDbContext Db)>
        BuildAsync(bool withSecret = true, bool withSession = true)
    {
        var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        var secrets = new UserSecretsService(config);
        var spy = new SpyAdapter();
        var pairing = new GatewayPairingService(db, config);
        var inbound = new GatewayInboundService(
            db, [spy], pairing, secrets, config, new NullDispatcher());
        var svc = new GatewayReplyService(
            db, [spy], inbound, NullLogger<GatewayReplyService>.Instance);

        db.GatewayChannels.Add(new GatewayChannel
        {
            Id = "ch1", OwnerUserId = "u1",
            Platform = GatewayPlatforms.Telegram,
            SecretRef = "GATEWAY_BOT_X", WebhookSecret = "s",
        });
        db.GatewayPeers.Add(new GatewayPeer
        {
            Id = "p1", ChannelId = "ch1", ExternalChatId = "555",
            ExternalUserId = "777", PairedUserId = "u1", PairedAt = 1,
        });
        if (withSession)
        {
            db.GatewaySessions.Add(new GatewaySession
            {
                PeerId = "p1", ChatId = "chat1", CreatedAt = 1,
            });
        }
        await db.SaveChangesAsync();
        if (withSecret)
        {
            await secrets.SetUserAsync("u1", "GATEWAY_BOT_X", "123:abc", default);
        }
        return (svc, spy, db);
    }

    private static ChatRunFinished Run(
        string status = ChatRunStatus.Completed,
        string chatId = "chat1", string? content = "resposta final",
        string? error = null) =>
        new("r1", chatId, "u1", "m1", status, error, content);

    [Test]
    public async Task RunCompleted_Envia_Texto_E_Audita_Ok()
    {
        var (svc, spy, db) = await BuildAsync();
        await svc.RunCompletedAsync(Run(), default);

        Assert.That(spy.Sent, Has.Count.EqualTo(1));
        Assert.That(spy.Sent[0].Text, Is.EqualTo("resposta final"));
        Assert.That(await db.GatewayDeliveries.AnyAsync(
            d => d.Direction == "out" && d.Status == "ok" && d.RunId == "r1"),
            Is.True);
    }

    [Test]
    public async Task RunCompleted_Sem_Sessao_NoOp()
    {
        var (svc, spy, db) = await BuildAsync(withSession: false);
        await svc.RunCompletedAsync(Run(chatId: "chat1"), default);
        Assert.That(spy.Sent, Is.Empty);
        Assert.That(await db.GatewayDeliveries.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task RunFailed_Envia_Aviso_Com_Status()
    {
        var (svc, spy, _) = await BuildAsync();
        await svc.RunCompletedAsync(Run(
            status: ChatRunStatus.Failed, content: null,
            error: "provider down"), default);
        Assert.That(spy.Sent, Has.Count.EqualTo(1));
        Assert.That(spy.Sent[0].Text, Does.Contain("failed"));
        Assert.That(spy.Sent[0].Text, Does.Contain("provider down"));
    }

    [Test]
    public async Task Sem_Secret_Marca_Delivery_Failed()
    {
        var (svc, spy, db) = await BuildAsync(withSecret: false);
        await svc.RunCompletedAsync(Run(), default);
        Assert.That(spy.Sent, Is.Empty);
        Assert.That(await db.GatewayDeliveries.AnyAsync(
            d => d.Direction == "out" && d.Status == "failed"), Is.True);
    }

    [Test]
    public async Task Envio_Falho_Vira_Delivery_Failed_Sem_Excecao()
    {
        var (svc, spy, db) = await BuildAsync();
        spy.FailWith = "telegram 500: down";
        await svc.RunCompletedAsync(Run(), default);
        Assert.That(await db.GatewayDeliveries.AnyAsync(
            d => d.Direction == "out" && d.Status == "failed"
                && d.Error != null && d.Error.Contains("telegram")), Is.True);
    }

    private sealed class SpyAdapter : IGatewayAdapter
    {
        public List<(GatewayPeer Peer, string Text)> Sent = [];
        public string? FailWith;

        public string Platform => GatewayPlatforms.Telegram;
        public string SignatureHeader => "X-Telegram-Bot-Api-Secret-Token";

        public bool VerifySignature(
            GatewayChannel channel, string? headerValue, string rawBody) =>
            headerValue == channel.WebhookSecret;

        public GatewayInboundMessage? ParseInbound(string rawBody) => null;

        public Task<GatewaySendResult> SendReplyAsync(
            GatewayChannel channel, GatewayPeer peer, string text,
            string botToken, CancellationToken ct)
        {
            if (FailWith is not null)
            {
                return Task.FromResult(new GatewaySendResult(false, FailWith));
            }
            Sent.Add((peer, text));
            return Task.FromResult(new GatewaySendResult(true, null));
        }
    }

    private sealed class NullDispatcher : IChatRunDispatcher
    {
        public void Enqueue(string runId) { }
    }
}
