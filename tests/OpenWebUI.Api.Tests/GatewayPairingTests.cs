using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Gateway;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Pareamento do gateway (SPEC-20261023-messaging-gateway RF-002):
/// código de 6 dígitos com TTL, cap de 5 tentativas, consumo one-shot
/// e bloqueado sem código novo.
/// </summary>
[TestFixture]
public class GatewayPairingTests
{
    private string _root = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-gwpair-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "test.db");
        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Users.Add(new User
        {
            Id = "u1", Name = "U", Email = "u@t.local", Role = "user",
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

    private static GatewayPairingService Svc(AppDbContext db) =>
        new(db, new ConfigService(db, TestCache.Create()));

    private static GatewayPeer Peer(
        string? code = null, long? expires = null, int attempts = 0,
        bool blocked = false) => new()
    {
        ChannelId = "c1", ExternalChatId = "111", ExternalUserId = "222",
        PairingCode = code, PairingExpiresAt = expires,
        PairingAttempts = attempts, Blocked = blocked,
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    [Test]
    public async Task EnsureCode_Gera_6_Digitos_Com_Ttl_E_Reusa_Valido()
    {
        using var db = NewDb();
        var peer = Peer();
        db.GatewayPeers.Add(peer);
        await db.SaveChangesAsync();

        var svc = Svc(db);
        var code = await svc.EnsureCodeAsync(peer, default);
        Assert.That(code, Is.Not.Null);
        Assert.That(code, Does.Match("^[0-9]{6}$"));
        Assert.That(peer.PairingExpiresAt,
            Is.GreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        // Código válido é reaproveitado (mesmo código na 2ª mensagem).
        var again = await svc.EnsureCodeAsync(peer, default);
        Assert.That(again, Is.EqualTo(code));
    }

    [Test]
    public async Task EnsureCode_Regenera_Expirado_E_Bloqueado_Sem_Codigo()
    {
        using var db = NewDb();
        var expired = Peer(code: "111111",
            expires: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1);
        var blocked = Peer(blocked: true);
        blocked.ExternalUserId = "blk1";
        db.GatewayPeers.AddRange(expired, blocked);
        await db.SaveChangesAsync();

        var svc = Svc(db);
        var renewed = await svc.EnsureCodeAsync(expired, default);
        Assert.That(renewed, Is.Not.Null.And.Not.EqualTo("111111"));
        Assert.That(expired.PairingAttempts, Is.EqualTo(0));

        Assert.That(await svc.EnsureCodeAsync(blocked, default), Is.Null);
        Assert.That(blocked.PairingCode, Is.Null);
    }

    [Test]
    public async Task Pair_Ok_Vincula_E_Consome_Codigo()
    {
        using var db = NewDb();
        var peer = Peer(code: "424242",
            expires: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600);
        db.GatewayPeers.Add(peer);
        await db.SaveChangesAsync();

        var (result, paired) = await Svc(db).PairAsync("u1", "424242", default);
        Assert.That(result, Is.EqualTo(GatewayPairResult.Ok));
        Assert.That(paired!.PairedUserId, Is.EqualTo("u1"));
        Assert.That(paired.PairingCode, Is.Null);
        Assert.That(paired.PairedAt, Is.Not.Null);

        // One-shot: o mesmo código não pareia de novo.
        var again = await Svc(db).PairAsync("u1", "424242", default);
        Assert.That(again.Result, Is.EqualTo(GatewayPairResult.Invalid));
    }

    [Test]
    public async Task Pair_Invalido_Expirado_E_Esgotado()
    {
        using var db = NewDb();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expired = Peer(code: "333333", expires: now - 1);
        expired.ExternalUserId = "e1";
        var tired = Peer(code: "444444", expires: now + 600,
            attempts: GatewayPairingService.MaxPairAttempts);
        tired.ExternalUserId = "e2";
        db.GatewayPeers.AddRange(expired, tired);
        await db.SaveChangesAsync();

        var svc = Svc(db);
        Assert.That((await svc.PairAsync("u1", "000000", default)).Result,
            Is.EqualTo(GatewayPairResult.Invalid));
        Assert.That((await svc.PairAsync("u1", "333333", default)).Result,
            Is.EqualTo(GatewayPairResult.Expired));
        Assert.That((await svc.PairAsync("u1", "444444", default)).Result,
            Is.EqualTo(GatewayPairResult.Exhausted));

        // Códigos consumidos não pareiam depois.
        Assert.That(expired.PairingCode, Is.Null);
        Assert.That(tired.PairingCode, Is.Null);
    }

    [Test]
    public async Task Pair_Tentativas_Esgotadas_Consome_Codigo()
    {
        using var db = NewDb();
        var peer = Peer(code: "555555",
            expires: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600,
            attempts: GatewayPairingService.MaxPairAttempts);
        db.GatewayPeers.Add(peer);
        await db.SaveChangesAsync();

        var (result, _) = await Svc(db).PairAsync("u1", "555555", default);
        Assert.That(result, Is.EqualTo(GatewayPairResult.Exhausted));
        Assert.That(peer.PairingCode, Is.Null);
        Assert.That(peer.PairedUserId, Is.Null);
    }
}
