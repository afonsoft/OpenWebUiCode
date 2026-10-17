using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura dos callers do <see cref="AutomationService"/> no nível de
/// serviço (SPEC-20261016-automation-actions): dependências ausentes
/// (skills/mcp/httpFactory/combos null), webhook com handler fake
/// (GET sem body, headers com Host filtrado, HTTP 500, timeout), MCP
/// com servidor cadastrado e reuso de chat em execuções seguintes.
/// </summary>
[TestFixture]
public class AutomationCallersTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"owui-callers-{Guid.NewGuid():N}.db");
        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@c.local", Role = "user" });
        db.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private static Automation NewAutomation(
        string actionKind = "prompt", string? actionJson = null,
        string model = "fake:1", string schedule = "interval", string? chatId = null) =>
        new()
        {
            UserId = "u1",
            Name = "auto-teste",
            Prompt = "diga olá",
            ModelId = model,
            ScheduleKind = schedule,
            ActionKind = actionKind,
            ActionJson = actionJson,
            ChatId = chatId,
        };

    /// <summary>
    /// Persiste a automação (FK de <see cref="AutomationRun.AutomationId"/>)
    /// e devolve a entidade para <see cref="AutomationService.RunNowAsync"/>.
    /// </summary>
    private static async Task<Automation> PersistAsync(AppDbContext db, Automation automation)
    {
        db.Automations.Add(automation);
        await db.SaveChangesAsync();
        return automation;
    }

    private static AutomationService NewService(AppDbContext db,
        SkillDiscoveryService? skills = null,
        McpClientService? mcp = null,
        IHttpClientFactory? httpFactory = null,
        ComboService? combos = null) =>
        new(db, NewProviders(db), NewNotifications(db), skills, mcp, httpFactory, combos);

    private static ProviderService NewProviders(AppDbContext db) =>
        new(new FailFactory(), new ConfigService(db, TestCache.Create()),
            NullLogger<ProviderService>.Instance, TestCache.Create(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    /// <summary>Provider com conexão Ollama stub que responde "ok-reply".</summary>
    private static async Task<ProviderService> NewProvidersOkAsync(AppDbContext db)
    {
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []));
        return new ProviderService(new OllamaFactory(), config,
            NullLogger<ProviderService>.Instance, TestCache.Create(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
    }

    /// <summary>Ollama fake: /api/tags lista fake:1, /api/chat responde "ok-reply".</summary>
    private sealed class OllamaFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new OllamaHandler());
    }

    private sealed class OllamaHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}",
                "/api/chat" => "{\"message\":{\"role\":\"assistant\",\"content\":\"ok-reply\"}}",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static NotificationService NewNotifications(AppDbContext db) =>
        new(new FailFactory(), db, NullLogger<NotificationService>.Instance);

    private static McpClientService NewMcp(AppDbContext db) =>
        new(db, TestCache.Create());

    // ---------- dependências opcionais ausentes ----------

    [Test]
    public async Task Skill_SemDiscovery_FalhaComMensagem()
    {
        await using var db = NewDb();
        var run = await NewService(db).RunNowAsync(
            await PersistAsync(db, NewAutomation("skill", "{\"skill\":\"x\"}")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("failed"));
            Assert.That(run.Error, Does.Contain("SkillDiscoveryService indisponível"));
        });
    }

    [Test]
    public async Task Mcp_SemClient_FalhaComMensagem()
    {
        await using var db = NewDb();
        var run = await NewService(db).RunNowAsync(
            await PersistAsync(db, NewAutomation("mcp", "{\"server\":\"s\",\"tool\":\"t\"}")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("failed"));
            Assert.That(run.Error, Does.Contain("McpClientService indisponível"));
        });
    }

    [Test]
    public async Task Webhook_SemFactory_FalhaComMensagem()
    {
        await using var db = NewDb();
        var run = await NewService(db).RunNowAsync(
            await PersistAsync(db, NewAutomation("webhook", "{\"url\":\"https://api.exemplo.com/h\"}")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("failed"));
            Assert.That(run.Error, Does.Contain("HttpClientFactory indisponível"));
        });
    }

    [Test]
    public async Task Prompt_ModeloCombo_SemComboService_Falha()
    {
        await using var db = NewDb();
        var run = await NewService(db).RunNowAsync(
            await PersistAsync(db, NewAutomation(model: "combo:meu-combo")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("failed"));
            Assert.That(run.Error, Does.Contain("ComboService indisponível"));
        });
    }

    // ---------- webhook com handler fake ----------

    [Test]
    public async Task Webhook_Get_ComHeaders_200_Ok()
    {
        CapturingHandler handler = new(HttpStatusCode.OK, "pong");
        await using var db = NewDb();
        var service = NewService(db, httpFactory: new StubFactory(handler));

        var run = await service.RunNowAsync(
            await PersistAsync(db, NewAutomation("webhook", """
            {"url":"https://api.exemplo.com/hook","method":"GET",
             "headers":{"X-A":"1","Host":"evil.example","X-Num":42}}
            """)));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("ok"), run.Error);
            Assert.That(run.Output, Does.Contain("HTTP 200").And.Contain("pong"));
            Assert.That(handler.LastRequest, Is.Not.Null);
        });
        // GET não carrega body; Host é filtrado do allowlist de headers.
        Assert.Multiple(() =>
        {
            Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(handler.LastRequest.Content, Is.Null);
            Assert.That(handler.LastRequest.Headers.Contains("X-A"), Is.True);
            Assert.That(handler.LastRequest.Headers.Contains("Host"), Is.False);
        });
    }

    [Test]
    public async Task Webhook_Put_ErroHttp_Falha()
    {
        CapturingHandler handler = new(HttpStatusCode.InternalServerError, "boom");
        await using var db = NewDb();
        var service = NewService(db, httpFactory: new StubFactory(handler));

        var run = await service.RunNowAsync(
            await PersistAsync(db, NewAutomation("webhook",
            "{\"url\":\"https://api.exemplo.com/hook\",\"method\":\"PUT\"}")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("failed"));
            Assert.That(run.Error, Does.Contain("500").And.Contain("boom"));
            Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Put));
            Assert.That(handler.LastRequest.Content, Is.Not.Null); // PUT carrega JSON
        });
    }

    [Test]
    public async Task Webhook_Timeout_Falha()
    {
        await using var db = NewDb();
        var service = NewService(db, httpFactory: new StubFactory(new TimeoutHandler()));

        var run = await service.RunNowAsync(
            await PersistAsync(db, NewAutomation("webhook",
            "{\"url\":\"https://api.exemplo.com/hook\"}")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("failed"));
            Assert.That(run.Error, Does.Contain("timeout"));
        });
    }

    // ---------- MCP com servidor cadastrado ----------

    [Test]
    public async Task Mcp_ServidorDesabilitado_RetornaErroNoOutput()
    {
        await using var db = NewDb();
        db.McpServers.Add(new McpServer
        {
            Id = "srv-off",
            Name = "fs-off",
            Enabled = false,
            Transport = "http",
            Url = "http://localhost:1/mcp",
        });
        await db.SaveChangesAsync();

        var run = await NewService(db, mcp: NewMcp(db)).RunNowAsync(
            await PersistAsync(db, NewAutomation("mcp", "{\"server\":\"srv-off\",\"tool\":\"read\"}")));

        Assert.Multiple(() =>
        {
            // Server encontrado → CallToolAsync devolve o erro como output.
            Assert.That(run.Status, Is.EqualTo("ok"), run.Error);
            Assert.That(run.Output, Does.Contain("desabilitado"));
        });
    }

    [Test]
    public async Task Mcp_ServidorEnv_TentaConexao_ERetornaErroNoOutput()
    {
        await using var db = NewDb();
        db.McpServers.Add(new McpServer
        {
            Id = "env:fs-dead",
            Name = "fs-dead",
            Enabled = true,
            Transport = "http",
            Url = "http://localhost:1/mcp",
        });
        await db.SaveChangesAsync();

        // Lookup por "fs-dead" cai no fallback "env:{id}".
        var run = await NewService(db, mcp: NewMcp(db)).RunNowAsync(
            await PersistAsync(db, NewAutomation("mcp", "{\"server\":\"fs-dead\",\"tool\":\"read\",\"args\":{\"p\":\"/\"}}")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("ok"), run.Error);
            Assert.That(run.Output, Does.Contain("Erro"));
        });
    }

    // ---------- reuso de chat / once+reminder ----------

    [Test]
    public async Task Prompt_ChatReuso_AnexaNaPosicaoCerta()
    {
        await using var db = NewDb();
        var chat = new Chat
        {
            UserId = "u1",
            Title = "reuso",
            CreatedAt = 1,
            UpdatedAt = 1,
        };
        chat.Messages.Add(new ChatMessage
        {
            ChatId = chat.Id, Role = "user", Content = "seed",
            Position = 0, Timestamp = 1,
        });
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
        var chatId = chat.Id;

        var providers = await NewProvidersOkAsync(db);
        var service = new AutomationService(db, providers, NewNotifications(db));
        var run = await service.RunNowAsync(
            await PersistAsync(db, NewAutomation(chatId: chatId)));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("ok"), run.Error);
            Assert.That(run.ChatId, Is.EqualTo(chatId));
        });
        await using var verify = NewDb();
        var msgs = await verify.ChatMessages
            .Where(m => m.ChatId == chatId)
            .OrderBy(m => m.Position)
            .ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(msgs, Has.Count.EqualTo(3));
            Assert.That(msgs[1].Position, Is.EqualTo(1));
            Assert.That(msgs[1].Role, Is.EqualTo("user"));
            Assert.That(msgs[2].Content, Is.EqualTo("ok-reply"));
        });
    }

    [Test]
    public async Task Webhook_OutputGrande_Trunca4096()
    {
        // Output > MaxOutputChars → run.Output truncado (branch do ternário).
        CapturingHandler handler = new(HttpStatusCode.OK, new string('x', 5000));
        await using var db = NewDb();
        var service = NewService(db, httpFactory: new StubFactory(handler));

        var run = await service.RunNowAsync(
            await PersistAsync(db, NewAutomation("webhook",
                "{\"url\":\"https://api.exemplo.com/big\"}")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("ok"), run.Error);
            Assert.That(run.Output, Has.Length.EqualTo(4096));
        });
    }

    [Test]
    public async Task Once_Reminder_Ok_NaoCriaChat()
    {
        await using var db = NewDb();
        var automation = await PersistAsync(db, NewAutomation("reminder",
            "{\"title\":\"único\"}", schedule: "once"));
        var run = await NewService(db).RunNowAsync(automation);

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("ok"), run.Error);
            Assert.That(run.ChatId, Is.Null);
        });
    }

    [Test]
    public async Task Skill_ActionJsonInvalidoNoDb_Catch_Generico()
    {
        await using var db = NewDb();
        // Bypass da validação — ActionJson malformado direto na entidade.
        var run = await NewService(db, skills: new SkillDiscoveryService())
            .RunNowAsync(
            await PersistAsync(db, NewAutomation("skill", "isto-não-é-json")));

        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.EqualTo("failed"));
            Assert.That(run.Error, Is.Not.Null.And.Not.Empty);
        });
    }

    // ---------- fakes ----------

    private sealed class CapturingHandler(HttpStatusCode status, string body)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Copia a request antes de o caller descartá-la.
            LastRequest = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = request.Content is null
                    ? null
                    : new StringContent(
                        await request.Content.ReadAsStringAsync(cancellationToken)),
            };
            foreach (var h in request.Headers)
            {
                LastRequest.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
        }
    }

    /// <summary>Simula o timeout de 15s do caller: cancela e não responde.</summary>
    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class FailFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FailHandler());
    }

    private sealed class FailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }
}
