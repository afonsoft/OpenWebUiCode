using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Api.Completions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261103-agent-turn-guard: nudge mecânico quando o turno fecha
/// sem tool-call com texto de plano/promessa (RF-001), cap de 1 nudge
/// por turno (RF-001/anti-loop) e encerramento normal sem padrão
/// (RF-005).
/// </summary>
[TestFixture]
public class AgentTurnGuardTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"owui-tg-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        _db.Database.EnsureCreated();
        _config = new ConfigService(_db, TestCache.Create());
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private sealed class QueueHandler(List<string> bodies,
        params string[] responses) : HttpMessageHandler
    {
        private int _calls;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                bodies.Add(await request.Content.ReadAsStringAsync(ct));
            }
            var body = responses[Math.Min(_calls++, responses.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }

    private sealed class QueueFactory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h);
    }

    private async Task<ProviderService> NewProvidersAsync(HttpMessageHandler h)
    {
        await _config.SetAsync("connections", new ConnectionsConfig(
            [], ["http://x.test"], ["k"]));
        return new ProviderService(new QueueFactory(h), _config,
            NullLogger<ProviderService>.Instance, TestCache.Create(),
            new ConfigurationBuilder().Build());
    }

    private ToolExecutor NewExecutor() =>
        new(_db, new QueueFactory(new QueueHandler([])),
            new PythonToolExecutor(new ConfigurationBuilder().Build()),
            new McpClientService(_db, TestCache.Create()),
            new OpenWebUI.Infrastructure.ChatTools.BuiltinToolRegistry(
                [], new ConfigurationBuilder().Build()),
            _config);

    private static string Sse(string text) =>
        $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{text}\"}}}}]}}\n\n"
        + "data: [DONE]\n\n";

    private static ChatCompletionRequest Req() =>
        new("m1", [new ChatCompletionMessage("user", "conserta o bug")],
            Stream: true, Connection: "openai",
            Tools: [JsonDocument.Parse(
                "{\"type\":\"function\",\"function\":{\"name\":\"t\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}")
                .RootElement.Clone()]);

    // Covers RF-001: texto de promessa sem tool-call → reminder injetado + re-prompt.
    [Test]
    public async Task Nudge_PlanoPromessa_RePrompta()
    {
        var bodies = new List<string>();
        var providers = await NewProvidersAsync(new QueueHandler(bodies,
            Sse("I will now fix the bug"), Sse("pronto")));
        var callbacks = new ChatPipeline.ToolLoopCallbacks(
            OnTurnEndGuardAsync: (text, _) =>
                Task.FromResult<string?>(
                    text.Contains("I will") ? "[turn_reminder] DO IT" : null));

        var outcome = await ChatPipeline.RunToolLoopAsync(Req(), [],
            NewExecutor(), providers, default,
            new ChatPipeline.ToolLoopOptions(callbacks, UserId: "u1"));

        Assert.Multiple(() =>
        {
            Assert.That(bodies, Has.Count.EqualTo(2),
                "1ª chamada com promessa → reminder → 2ª chamada");
            Assert.That(bodies[1], Does.Contain("turn_reminder"));
            Assert.That(bodies[1], Does.Contain("I will now fix the bug"),
                "o texto prometido entra no histórico como assistant interim");
        });
        Assert.That(outcome, Is.Not.Null);
    }

    // Covers RF-001 anti-loop: guard sempre retorna reminder → só 1 nudge por turno.
    [Test]
    public async Task Nudge_Max1PorTurno()
    {
        var bodies = new List<string>();
        var providers = await NewProvidersAsync(new QueueHandler(bodies,
            Sse("I will fix it"), Sse("I will fix it")));
        var callbacks = new ChatPipeline.ToolLoopCallbacks(
            OnTurnEndGuardAsync: (text, _) =>
                Task.FromResult<string?>("[turn_reminder] DO IT"));

        await ChatPipeline.RunToolLoopAsync(Req(), [],
            NewExecutor(), providers, default,
            new ChatPipeline.ToolLoopOptions(callbacks, UserId: "u1"));

        Assert.That(bodies, Has.Count.EqualTo(2),
            "guard disparou 1× e parou — não re-nudgea o 2º turno");
    }

    // Covers RF-005: guard devolve null → turno encerra normal.
    [Test]
    public async Task SemPadrao_Encerra()
    {
        var bodies = new List<string>();
        var providers = await NewProvidersAsync(new QueueHandler(bodies,
            Sse("trabalho concluido")));
        var callbacks = new ChatPipeline.ToolLoopCallbacks(
            OnTurnEndGuardAsync: (text, _) => Task.FromResult<string?>(null));

        var outcome = await ChatPipeline.RunToolLoopAsync(Req(), [],
            NewExecutor(), providers, default,
            new ChatPipeline.ToolLoopOptions(callbacks, UserId: "u1"));

        Assert.Multiple(() =>
        {
            Assert.That(bodies, Has.Count.EqualTo(1));
            Assert.That(outcome?.FinalContent, Does.Contain("concluido"));
        });
    }

    // Covers RF-005: sem callback → comportamento atual (nenhum re-prompt).
    [Test]
    public async Task SemGuard_SemReprompt()
    {
        var bodies = new List<string>();
        var providers = await NewProvidersAsync(new QueueHandler(bodies,
            Sse("I will fix it")));

        var outcome = await ChatPipeline.RunToolLoopAsync(Req(), [],
            NewExecutor(), providers, default,
            new ChatPipeline.ToolLoopOptions(UserId: "u1"));

        Assert.That(bodies, Has.Count.EqualTo(1));
        Assert.That(outcome?.FinalContent, Does.Contain("I will"));
    }
}
