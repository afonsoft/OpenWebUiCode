using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Pipeline inbound do messaging gateway
/// (SPEC-20261023-messaging-gateway RF-001..006): feature flag, assinatura
/// antes de qualquer efeito, pareamento por código, sessão persistente por
/// peer, wrap <c>&lt;untrusted_input&gt;</c>, política de tools e auditoria
/// de deliveries — com o HttpClient "gateway" estubado (sem Telegram real).
/// </summary>
[TestFixture, IsolateEnvironment]
public class GatewayInboundTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _user = null!;
    private readonly List<string> _sentBodies = [];

    private const string Update = """
        {"update_id":1,"message":{"message_id":7,"chat":{"id":555},"from":{"id":777,"username":"ana"},"text":"oi bot"}}
        """;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-gw-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("Providers__MaxAttempts", "1");
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddHttpClient("gateway")
                    .ConfigurePrimaryHttpMessageHandler(_ =>
                        new EchoHandler(_sentBodies))));
        _client = _factory.CreateClient();

        _user = await SignUpAsync("GwUser", "gw@local.test");
        // Feature flag opt-in do gateway (RF-007 / acceptance).
        using (var scope = _factory.Services.CreateScope())
        {
            var config = scope.ServiceProvider
                .GetRequiredService<ConfigService>();
            await config.SetAsync("agent.gateway.enabled", true, default);
        }
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

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var res = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email, password = "senha123!" });
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await res.Content.ReadAsStringAsync());
        return (await res.Content
            .ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", token);

    private async Task<GatewayChannel> CreateChannelAsync(
        string? policy = null, string? model = "m1")
    {
        UseToken(_user.Token);
        var res = await _client.PostAsJsonAsync("/api/v1/gateway/channels",
            new
            {
                platform = "telegram", botToken = "123:abc",
                toolsPolicy = policy, defaultModelId = model,
            });
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await res.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(
            await res.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetString()!;
        await using var db = NewDb();
        return await db.GatewayChannels.FirstAsync(c => c.Id == id);
    }

    private HttpRequestMessage Inbound(
        GatewayChannel channel, string body, string? secret)
    {
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/gateway/telegram/{channel.Id}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (secret is not null)
        {
            req.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secret);
        }
        return req;
    }

    // ---------- auth surface ----------

    [Test]
    public async Task Channels_Unauthorized_Sem_Token()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        Assert.That((await _client.GetAsync("/api/v1/gateway/channels"))
            .StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _client.PostAsJsonAsync("/api/v1/gateway/pair",
            new { code = "123456" })).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task CreateChannel_Validacao_E_WebhookUrl()
    {
        UseToken(_user.Token);
        var bad = await _client.PostAsJsonAsync("/api/v1/gateway/channels",
            new { platform = "slack", botToken = "x" });
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var noToken = await _client.PostAsJsonAsync("/api/v1/gateway/channels",
            new { platform = "telegram" });
        Assert.That(noToken.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var badPolicy = await _client.PostAsJsonAsync("/api/v1/gateway/channels",
            new { platform = "telegram", botToken = "t", toolsPolicy = "root" });
        Assert.That(badPolicy.StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest));

        var ok = await _client.PostAsJsonAsync("/api/v1/gateway/channels",
            new { platform = "telegram", botToken = "123:abc" });
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var doc = JsonDocument.Parse(
            await ok.Content.ReadAsStringAsync());
        var url = doc.RootElement.GetProperty("webhookUrl").GetString()!;
        Assert.That(url, Does.StartWith("/api/v1/gateway/telegram/"));
        Assert.That(doc.RootElement.GetProperty("secretRef").GetString(),
            Does.StartWith("GATEWAY_BOT_"));
        // Token do bot nunca volta na resposta nem fica plano no canal.
        Assert.That(await ok.Content.ReadAsStringAsync(),
            Does.Not.Contain("123:abc"));
    }

    // ---------- inbound gate ----------

    [Test]
    public async Task Inbound_Token_Invalido_404_E_Assinatura_Errada_401()
    {
        var res = await _client.PostAsync(
            "/api/v1/gateway/telegram/nao-existe",
            new StringContent(Update, Encoding.UTF8, "application/json"));
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var channel = await CreateChannelAsync();
        var bad = await _client.SendAsync(Inbound(channel, Update, "errado"));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        await using var db = NewDb();
        Assert.That(await db.GatewayDeliveries.CountAsync(
            d => d.ChannelId == channel.Id && d.Status == "rejected"),
            Is.EqualTo(1));
        Assert.That(await db.GatewayPeers.CountAsync(
            p => p.ChannelId == channel.Id), Is.EqualTo(0));
    }

    [Test]
    public async Task Inbound_Update_Sem_Mensagem_E_Filtrado()
    {
        var channel = await CreateChannelAsync();
        var res = await _client.SendAsync(
            Inbound(channel, """{"update_id":9}""", channel.WebhookSecret));
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var db = NewDb();
        Assert.That(await db.GatewayDeliveries.AnyAsync(
            d => d.ChannelId == channel.Id && d.Status == "filtered"),
            Is.True);
    }

    // ---------- pairing ----------

    [Test]
    public async Task Peer_Novo_Recebe_Codigo_E_Pareia()
    {
        var channel = await CreateChannelAsync();
        var res = await _client.SendAsync(
            Inbound(channel, Update, channel.WebhookSecret));
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string code;
        await using (var db = NewDb())
        {
            var peer = await db.GatewayPeers.SingleAsync(
                p => p.ChannelId == channel.Id);
            Assert.That(peer.PairingCode, Does.Match("^[0-9]{6}$"));
            Assert.That(peer.PairedUserId, Is.Null);
            code = peer.PairingCode!;
            Assert.That(await db.GatewayDeliveries.AnyAsync(
                d => d.ChannelId == channel.Id && d.Status == "pairing"),
                Is.True);
        }
        // Código foi enviado ao peer pela API (stub).
        Assert.That(_sentBodies.Any(b => b.Contains(code)), Is.True);

        UseToken(_user.Token);
        var pair = await _client.PostAsJsonAsync("/api/v1/gateway/pair",
            new { code });
        Assert.That(pair.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await pair.Content.ReadAsStringAsync(),
            Does.Contain("\"paired\":true"));

        var wrong = await _client.PostAsJsonAsync("/api/v1/gateway/pair",
            new { code = "000000" });
        Assert.That(wrong.StatusCode,
            Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------- sessão + run ----------

    [Test]
    public async Task Peer_Pareado_Cria_Sessao_Run_Restricted_E_Untrusted()
    {
        var channel = await CreateChannelAsync(model: "gpt-test");
        await using (var db = NewDb())
        {
            db.GatewayPeers.Add(new GatewayPeer
            {
                ChannelId = channel.Id, ExternalChatId = "555",
                ExternalUserId = "777", PairedUserId = _user.User.Id,
                PairedAt = 1, CreatedAt = 1,
            });
            await db.SaveChangesAsync();
        }

        var res = await _client.SendAsync(
            Inbound(channel, Update, channel.WebhookSecret));
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var db2 = NewDb();
        var peer = await db2.GatewayPeers.SingleAsync(
            p => p.ChannelId == channel.Id);
        var session = await db2.GatewaySessions.SingleAsync(
            s => s.PeerId == peer.Id);
        var chat = await db2.Chats.SingleAsync(c => c.Id == session.ChatId);
        Assert.That(chat.Title, Does.StartWith("gateway:telegram:"));
        Assert.That(chat.UserId, Is.EqualTo(_user.User.Id));

        var msg = await db2.ChatMessages.SingleAsync(
            m => m.ChatId == chat.Id);
        Assert.That(msg.Content, Does.Contain(
            "<untrusted_input source=\"gateway:telegram\""));
        Assert.That(msg.Content, Does.Contain("oi bot"));

        var run = await db2.ChatRuns.SingleAsync(r => r.ChatId == chat.Id);
        Assert.That(run.Model, Is.EqualTo("gpt-test"));
        using var req = JsonDocument.Parse(run.RequestJson);
        var toolIds = req.RootElement.GetProperty("toolIds")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.That(toolIds, Does.Contain("builtin:web_search"));
        Assert.That(toolIds, Does.Not.Contain("builtin:shell_exec"));
        Assert.That(toolIds, Does.Not.Contain("builtin:file_write"));
        Assert.That(toolIds, Does.Not.Contain("builtin:delegate_task"));
        // Escrita na memória do dono e approvals web-only fora do restricted.
        Assert.That(toolIds, Does.Not.Contain("builtin:memory_save"));
        Assert.That(toolIds, Does.Not.Contain("builtin:memory_learn"));
        Assert.That(toolIds, Does.Not.Contain("builtin:ask_user"));

        Assert.That(await db2.GatewayDeliveries.AnyAsync(
            d => d.ChannelId == channel.Id && d.Status == "accepted"
                && d.RunId == run.Id), Is.True);

        // Segunda mensagem reusa a MESMA sessão/chat (RF-003).
        var res2 = await _client.SendAsync(Inbound(channel,
            """
            {"update_id":2,"message":{"message_id":8,"chat":{"id":555},"from":{"id":777},"text":"de novo"}}
            """,
            channel.WebhookSecret));
        Assert.That(res2.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var db3 = NewDb();
        Assert.That(await db3.GatewaySessions.CountAsync(
            s => s.PeerId == peer.Id), Is.EqualTo(1));
        Assert.That(await db3.ChatRuns.CountAsync(
            r => r.ChatId == chat.Id), Is.EqualTo(2));
    }

    [Test]
    public async Task Peer_Bloqueado_Silencioso_Sem_Run()
    {
        var channel = await CreateChannelAsync();
        await using (var db = NewDb())
        {
            db.GatewayPeers.Add(new GatewayPeer
            {
                ChannelId = channel.Id, ExternalChatId = "555",
                ExternalUserId = "999", Blocked = true,
                PairedUserId = _user.User.Id, PairedAt = 1, CreatedAt = 1,
            });
            await db.SaveChangesAsync();
        }
        var res = await _client.SendAsync(Inbound(channel,
            """
            {"update_id":3,"message":{"chat":{"id":555},"from":{"id":999},"text":"hello"}}
            """,
            channel.WebhookSecret));
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var db2 = NewDb();
        Assert.That(await db2.GatewayDeliveries.AnyAsync(
            d => d.ChannelId == channel.Id && d.Status == "blocked"),
            Is.True);
        Assert.That(await db2.ChatRuns.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Policy_Standard_Nao_Forca_ToolIds()
    {
        var channel = await CreateChannelAsync(
            policy: "standard", model: "m1");
        await using (var db = NewDb())
        {
            db.GatewayPeers.Add(new GatewayPeer
            {
                ChannelId = channel.Id, ExternalChatId = "555",
                ExternalUserId = "777", PairedUserId = _user.User.Id,
                PairedAt = 1, CreatedAt = 1,
            });
            await db.SaveChangesAsync();
        }
        await _client.SendAsync(
            Inbound(channel, Update, channel.WebhookSecret));
        await using var db2 = NewDb();
        var peer = await db2.GatewayPeers.SingleAsync(
            p => p.ChannelId == channel.Id);
        var session = await db2.GatewaySessions.SingleAsync(
            s => s.PeerId == peer.Id);
        var run = await db2.ChatRuns.SingleAsync(
            r => r.ChatId == session.ChatId);
        using var req = JsonDocument.Parse(run.RequestJson);
        var hasTools = req.RootElement.TryGetProperty("toolIds", out var t)
            && t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 0;
        Assert.That(hasTools, Is.False);
    }

    // ---------- peers / deliveries API ----------

    [Test]
    public async Task Peers_List_Block_E_Deliveries_Audit()
    {
        var channel = await CreateChannelAsync();
        await _client.SendAsync(
            Inbound(channel, Update, channel.WebhookSecret));

        UseToken(_user.Token);
        var peers = await _client.GetAsync("/api/v1/gateway/peers");
        Assert.That(peers.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await peers.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("\"externalUserId\":\"777\""));

        await using (var db = NewDb())
        {
            var peer = await db.GatewayPeers.SingleAsync(
                p => p.ChannelId == channel.Id);
            var block = await _client.PatchAsJsonAsync(
                $"/api/v1/gateway/peers/{peer.Id}", new { blocked = true });
            Assert.That(block.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        var deliveries = await _client.GetAsync(
            $"/api/v1/gateway/channels/{channel.Id}/deliveries");
        Assert.That(deliveries.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await deliveries.Content.ReadAsStringAsync(),
            Does.Contain("\"status\":\"pairing\""));
    }

    // ---------- higiene de sessão/canal ----------

    [Test]
    public async Task Unpair_Remove_Sessao_E_Requer_Novo_Codigo()
    {
        var channel = await CreateChannelAsync();
        string peerId;
        string oldChatId;
        await using (var db = NewDb())
        {
            var peer = new GatewayPeer
            {
                ChannelId = channel.Id, ExternalChatId = "555",
                ExternalUserId = "777", PairedUserId = _user.User.Id,
                PairedAt = 1, CreatedAt = 1,
            };
            db.GatewayPeers.Add(peer);
            await db.SaveChangesAsync();
            peerId = peer.Id;
        }
        await _client.SendAsync(
            Inbound(channel, Update, channel.WebhookSecret));
        await using (var db = NewDb())
        {
            oldChatId = (await db.GatewaySessions
                .SingleAsync(s => s.PeerId == peerId)).ChatId;
        }

        UseToken(_user.Token);
        var unpair = await _client.PatchAsJsonAsync(
            $"/api/v1/gateway/peers/{peerId}", new { unpair = true });
        Assert.That(unpair.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using (var db = NewDb())
        {
            Assert.That(await db.GatewaySessions.CountAsync(
                s => s.PeerId == peerId), Is.EqualTo(0),
                "unpair remove a sessão do dono anterior");
        }

        // Nova mensagem → peer não-pareado: recebe código, sem run e sem
        // reabrir o chat antigo.
        _client.DefaultRequestHeaders.Authorization = null;
        var res = await _client.SendAsync(
            Inbound(channel, Update, channel.WebhookSecret));
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var db2 = NewDb();
        Assert.That(await db2.ChatRuns.CountAsync(
            r => r.ChatId == oldChatId), Is.EqualTo(1));
        Assert.That(await db2.GatewayDeliveries.AnyAsync(
            d => d.ChannelId == channel.Id && d.Status == "pairing"), Is.True);
    }

    [Test]
    public async Task DeleteChannel_Limpa_Dependentes_E_Segredo()
    {
        var channel = await CreateChannelAsync();
        await using (var db = NewDb())
        {
            var peer = new GatewayPeer
            {
                ChannelId = channel.Id, ExternalChatId = "555",
                ExternalUserId = "777", PairedUserId = _user.User.Id,
                PairedAt = 1, CreatedAt = 1,
            };
            db.GatewayPeers.Add(peer);
            await db.SaveChangesAsync();
            db.GatewaySessions.Add(new GatewaySession
            {
                PeerId = peer.Id, ChatId = "chat-orphan", CreatedAt = 1,
            });
            db.GatewayDeliveries.Add(new GatewayDelivery
            {
                ChannelId = channel.Id, PeerId = peer.Id,
                Direction = "in", Status = "accepted", CreatedAt = 1,
            });
            await db.SaveChangesAsync();
        }

        UseToken(_user.Token);
        var del = await _client.DeleteAsync(
            $"/api/v1/gateway/channels/{channel.Id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var db2 = NewDb();
        Assert.That(await db2.GatewayChannels.CountAsync(
            c => c.Id == channel.Id), Is.EqualTo(0));
        Assert.That(await db2.GatewayPeers.CountAsync(
            p => p.ChannelId == channel.Id), Is.EqualTo(0));
        Assert.That(await db2.GatewaySessions.CountAsync(), Is.EqualTo(0));
        Assert.That(await db2.GatewayDeliveries.CountAsync(
            d => d.ChannelId == channel.Id), Is.EqualTo(0));
    }

    [Test]
    public async Task DeleteChannel_Sem_SecretRef_Tambem_Remove()
    {
        await using (var db = NewDb())
        {
            db.GatewayChannels.Add(new GatewayChannel
            {
                OwnerUserId = _user.User.Id,
                Platform = GatewayPlatforms.Telegram,
                SecretRef = string.Empty,
            });
            await db.SaveChangesAsync();
        }
        string id;
        await using (var db = NewDb())
        {
            id = (await db.GatewayChannels.FirstAsync(
                c => c.SecretRef == "")).Id;
        }
        UseToken(_user.Token);
        var del = await _client.DeleteAsync($"/api/v1/gateway/channels/{id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Inbound_Payload_Grande_413_Sem_Ler_Tudo()
    {
        var channel = await CreateChannelAsync();
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/gateway/telegram/{channel.Id}")
        {
            Content = new StringContent(
                new string('x', 300 * 1024), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Telegram-Bot-Api-Secret-Token", channel.WebhookSecret);
        var res = await _client.SendAsync(req);
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    /// <summary>Handler de teste: captura o corpo e responde ok=true do Telegram.</summary>
    private sealed class EchoHandler(List<string> sentBodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sentBodies.Add(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}"""),
            };
        }
    }
}
