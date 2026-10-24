using System.Net;
using System.Text;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Services.Gateway;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Contrato do adapter Telegram (SPEC-20261023-messaging-gateway G):
/// verificação do secret_token em tempo constante, parse do update
/// (só DM de texto vira mensagem), chunking ≤4000 e envio com
/// fallback para texto puro quando a plataforma rejeita o markup.
/// </summary>
[TestFixture]
public class TelegramAdapterTests
{
    private static GatewayChannel Channel(string secret = "sec") => new()
    {
        Platform = GatewayPlatforms.Telegram,
        WebhookSecret = secret,
        SecretRef = "GATEWAY_BOT_X",
        OwnerUserId = "u1",
    };

    private static GatewayPeer Peer() => new()
    {
        ChannelId = "c1", ExternalChatId = "123", ExternalUserId = "456",
    };

    private const string Update = """
        {"update_id":1,"message":{"message_id":7,"chat":{"id":123},"from":{"id":456,"username":"ana"},"text":"oi"}}
        """;

    // ---------- assinatura ----------

    [Test]
    public void VerifySignature_Ok_When_Header_Equals_Secret()
    {
        var a = new TelegramAdapter(new StubFactory());
        Assert.That(a.VerifySignature(Channel(), "sec", "{}"), Is.True);
    }

    [Test]
    public void VerifySignature_Rejeita_Errado_Nulo_E_Sem_Secret()
    {
        var a = new TelegramAdapter(new StubFactory());
        Assert.That(a.VerifySignature(Channel(), "wrong", "{}"), Is.False);
        Assert.That(a.VerifySignature(Channel(), null, "{}"), Is.False);
        Assert.That(a.VerifySignature(Channel(secret: ""), "x", "{}"), Is.False);
    }

    // ---------- parse ----------

    [Test]
    public void ParseInbound_Update_Valido()
    {
        var a = new TelegramAdapter(new StubFactory());
        var msg = a.ParseInbound(Update);
        Assert.That(msg, Is.Not.Null);
        Assert.That(msg!.ExternalChatId, Is.EqualTo("123"));
        Assert.That(msg.ExternalUserId, Is.EqualTo("456"));
        Assert.That(msg.ExternalName, Is.EqualTo("ana"));
        Assert.That(msg.Text, Is.EqualTo("oi"));
        Assert.That(msg.MessageId, Is.EqualTo("7"));
    }

    [Test]
    public void ParseInbound_Sem_Message_Json_Malformado_Ou_Sem_Texto_Null()
    {
        var a = new TelegramAdapter(new StubFactory());
        Assert.That(a.ParseInbound("""{"update_id":1}"""), Is.Null);
        Assert.That(a.ParseInbound("not json"), Is.Null);
        Assert.That(a.ParseInbound(
            """{"message":{"chat":{"id":1},"from":{"id":2}}}"""), Is.Null);
        Assert.That(a.ParseInbound("""
            {"message":{"chat":{"id":1},"from":{"id":2},"text":""}}
            """), Is.Null);
        Assert.That(a.ParseInbound(
            """{"message":{"from":{"id":2},"text":"x"}}"""), Is.Null);
    }

    [Test]
    public void ParseInbound_FirstName_Quando_Sem_Username()
    {
        var a = new TelegramAdapter(new StubFactory());
        var msg = a.ParseInbound("""
            {"message":{"chat":{"id":1},"from":{"id":2,"first_name":"Ana"},"text":"oi"}}
            """);
        Assert.That(msg!.ExternalName, Is.EqualTo("Ana"));
    }

    // ---------- chunking ----------

    [Test]
    public void Chunk_Divide_Em_Blocos_Com_Quebra_Limpa()
    {
        var text = string.Join('\n',
            Enumerable.Repeat(new string('x', 900), 6));
        var chunks = TelegramAdapter.Chunk(text, TelegramAdapter.MaxMessageChars);
        Assert.That(chunks, Has.Count.GreaterThan(1));
        Assert.That(chunks.All(c => c.Length <= TelegramAdapter.MaxMessageChars));
        Assert.That(string.Concat(chunks).Replace("\n", "").Length,
            Is.EqualTo(900 * 6));
        Assert.That(TelegramAdapter.Chunk("curto", 4000), Has.Count.EqualTo(1));
    }

    // ---------- envio ----------

    [Test]
    public async Task SendReply_Ok_E_Fallback_Para_Texto_Puro()
    {
        // 1ª chamada (MarkdownV2) rejeitada → 2ª sem parse_mode aceita.
        var calls = new List<string>();
        var factory = new StubFactory(async req =>
        {
            var body = await req.Content!.ReadAsStringAsync();
            calls.Add(body);
            return body.Contains("MarkdownV2")
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    { Content = new StringContent("can't parse entities") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("""{"ok":true}""") };
        });
        var a = new TelegramAdapter(factory);
        var res = await a.SendReplyAsync(Channel(), Peer(), "oi", "tok", default);
        Assert.That(res.Ok, Is.True);
        Assert.That(calls, Has.Count.EqualTo(2));
        Assert.That(calls[0], Does.Contain("MarkdownV2"));
        Assert.That(calls[1], Does.Not.Contain("MarkdownV2"));
    }

    [Test]
    public async Task SendReply_Falha_Propaga_Erro()
    {
        var factory = new StubFactory(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"description":"unauthorized"}"""),
            }));
        var a = new TelegramAdapter(factory);
        var res = await a.SendReplyAsync(Channel(), Peer(), "oi", "tok", default);
        Assert.That(res.Ok, Is.False);
        Assert.That(res.Error, Does.Contain("401"));
    }

    [Test]
    public async Task SendReply_Chunka_Texto_Longo()
    {
        var calls = 0;
        var factory = new StubFactory(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"ok":true}""") });
        });
        var a = new TelegramAdapter(factory);
        var res = await a.SendReplyAsync(
            Channel(), Peer(), new string('a', 8100), "tok", default);
        Assert.That(res.Ok, Is.True);
        Assert.That(calls, Is.EqualTo(3)); // 4000+4000+100
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _respond;

        public StubFactory(
            Func<HttpRequestMessage, Task<HttpResponseMessage>>? respond = null) =>
            _respond = respond;

        public HttpClient CreateClient(string name) =>
            new(new StubHandler(_respond ?? (_ => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"ok":true}""") }))));
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
