using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Rate limiting do messaging gateway (RF-001 + hardening): o webhook
/// anônimo é limitado por IP **mesmo com o toggle global de ratelimit
/// desligado** (default), e <c>/pair</c> tem teto de tentativas por
/// usuário — fixture própria para isolar o bucket do RateLimitService.
/// </summary>
[TestFixture, IsolateEnvironment]
public class GatewayRateLimitTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _user = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-gwrl-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var res = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name = "GwRl", email = "gwrl@local.test",
                password = "senha123!" });
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _user = (await res.Content.ReadFromJsonAsync<AuthResponse>())!;
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
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    [Test]
    public async Task Webhook_429_Apos_Limite_Mesmo_Sem_Ratelimit_Global()
    {
        // Garante o bucket dedicado (30/min) independente da ordem dos
        // testes — outros testes da fixture podem ativar o toggle global.
        using (var scope = _factory.Services.CreateScope())
        {
            var config = scope.ServiceProvider
                .GetRequiredService<ConfigService>();
            await config.SetAsync("ratelimit",
                new RateLimitConfig(false, 60, 60, 5, 300), default);
        }
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _user.Token);
        var res = await _client.PostAsJsonAsync("/api/v1/gateway/channels",
            new { platform = "telegram", botToken = "123:abc" });
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var doc = System.Text.Json.JsonDocument.Parse(
            await res.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetString()!;
        _client.DefaultRequestHeaders.Authorization = null;

        // 30 req/min no bucket dedicado — todas rejeitadas por assinatura,
        // a 31ª nem chega ao service.
        HttpStatusCode last = HttpStatusCode.OK;
        for (var n = 0; n < 31; n++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/gateway/telegram/{id}")
            {
                Content = new StringContent("{}", Encoding.UTF8,
                    "application/json"),
            };
            last = (await _client.SendAsync(req)).StatusCode;
        }
        Assert.That(last, Is.EqualTo(HttpStatusCode.TooManyRequests));
        await using var db = NewDb();
        Assert.That(await db.GatewayDeliveries.CountAsync(
            d => d.ChannelId == id), Is.LessThanOrEqualTo(30),
            "request rate-limited não grava auditoria");
    }

    [Test]
    public async Task Webhook_Com_Ratelimit_Global_Ativo_Usa_Config()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var config = scope.ServiceProvider
                .GetRequiredService<ConfigService>();
            await config.SetAsync("ratelimit",
                new RateLimitConfig(true, 60, 60, 5, 300), default);
        }
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _user.Token);
        var res = await _client.PostAsJsonAsync("/api/v1/gateway/channels",
            new { platform = "telegram", botToken = "123:abc" });
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var doc = System.Text.Json.JsonDocument.Parse(
            await res.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetString()!;
        _client.DefaultRequestHeaders.Authorization = null;

        // Config global ativa cobre o braço `rateConfig.Enabled` — a
        // chamada chega ao service e falha a assinatura (401), não 429.
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/gateway/telegram/{id}")
        {
            Content = new StringContent("{}", Encoding.UTF8,
                "application/json"),
        };
        var inbound = await _client.SendAsync(req);
        Assert.That(inbound.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Inbound_Chunked_Grande_413_Pela_Leitura_Limitada()
    {
        // Limite alto para isolar do bucket consumido por outros testes.
        using (var scope = _factory.Services.CreateScope())
        {
            var config = scope.ServiceProvider
                .GetRequiredService<ConfigService>();
            await config.SetAsync("ratelimit",
                new RateLimitConfig(true, 100_000, 60, 5, 300), default);
        }
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _user.Token);
        var res = await _client.PostAsJsonAsync("/api/v1/gateway/channels",
            new { platform = "telegram", botToken = "123:abc" });
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var doc = System.Text.Json.JsonDocument.Parse(
            await res.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetString()!;
        _client.DefaultRequestHeaders.Authorization = null;

        // Sem Content-Length honesto → o cap cai na leitura limitada.
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/gateway/telegram/{id}")
        {
            Content = new UnknownLengthContent(
                new string('x', 300 * 1024)),
        };
        var inbound = await _client.SendAsync(req);
        Assert.That(inbound.StatusCode,
            Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    /// <summary>Content sem tamanho declarado — força o caminho de leitura limitada.</summary>
    private sealed class UnknownLengthContent(string text) : HttpContent
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(text);

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Test]
    public async Task Pair_429_Apos_10_Tentativas_Por_Usuario()
    {
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", _user.Token);
        HttpStatusCode last = HttpStatusCode.OK;
        for (var n = 0; n < 11; n++)
        {
            last = (await _client.PostAsJsonAsync("/api/v1/gateway/pair",
                new { code = $"9{n:D5}" })).StatusCode;
        }
        Assert.That(last, Is.EqualTo(HttpStatusCode.TooManyRequests));
    }
}
