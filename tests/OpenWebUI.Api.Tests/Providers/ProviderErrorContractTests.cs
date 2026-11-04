using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests.Providers;

/// <summary>
/// Contract tests do error-path dos providers tipados (SPEC-20261103-
/// provider-contract-tests, escopo refinado): erros HTTP classificados,
/// <c>{"error":…}</c> mid-stream → <see cref="ProviderHttpException"/>
/// com <c>MidStream=true</c>, e replay de golden fixtures SSE file-based.
/// Cobertura de payload/stream feliz já existe em
/// <see cref="ProviderServiceTests"/>.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ProviderErrorContractTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-pct-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(_db);
        _config = new ConfigService(_db, TestCache.Create());
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    // ---------- helpers ----------

    private ProviderService NewService(StubHandler handler) =>
        new(new StubFactory(handler), _config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());

    private Task SetProvidersAsync(params ProviderConnection[] providers) =>
        _config.SetAsync("connections",
            new ConnectionsConfig([], [], [], Providers: providers));

    private static ChatCompletionRequest Req(
        string model, string connection, bool stream = false) =>
        new(model, [new ChatCompletionMessage("user", "oi")],
            Stream: stream, Connection: connection);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures", "Providers", name));

    private static async Task<List<string>> DrainAsync(
        IAsyncEnumerable<string> lines)
    {
        var result = new List<string>();
        await foreach (var line in lines)
        {
            result.Add(line);
        }
        return result;
    }

    // ---------- RF-004: erro HTTP classificado ----------

    /// <summary>Covers RF-004: 429 + Retry-After do Claude sobem como
    /// ProviderHttpException com status e delay capturados.</summary>
    [Test]
    public async Task Anthropic_Http429_PropagaProviderHttpExceptionComRetryAfter()
    {
        var http = new StubHandler();
        http.Enqueue(HttpStatusCode.TooManyRequests,
            "{\"error\":{\"type\":\"rate_limit_error\",\"message\":\"rate limit\"}}",
            retryAfter: TimeSpan.FromSeconds(30));
        await SetProvidersAsync(
            new ProviderConnection("anthropic", "https://ant.test", "sk-ant"));

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(async () =>
            await NewService(http).CompleteAsync(
                Req("claude-sonnet-4", "anthropic")));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(ex.MidStream, Is.False);
            Assert.That(ex.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(ex.Body, Does.Contain("rate_limit_error"));
        });
    }

    /// <summary>Covers RF-004: 5xx do Google sobe com body truncado e sem
    /// Retry-After quando o header não vem.</summary>
    [Test]
    public async Task Google_Http503_PropagaProviderHttpException()
    {
        var http = new StubHandler();
        http.Enqueue(HttpStatusCode.ServiceUnavailable,
            "{\"error\":{\"code\":503,\"status\":\"UNAVAILABLE\"}}");
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(async () =>
            await NewService(http).CompleteAsync(
                Req("gemini-2.0-flash", "google")));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(ex.MidStream, Is.False);
            Assert.That(ex.RetryAfter, Is.Null);
        });
    }

    // ---------- RF-004: {"error":…} mid-stream ----------

    /// <summary>Covers RF-004: evento error do stream Claude vira
    /// ProviderHttpException MidStream (overloaded → 502 pela heurística
    /// atual, classificável como transitório pela retry policy).</summary>
    [Test]
    public async Task Anthropic_Stream_OverloadedError_ViraMidStream()
    {
        var http = new StubHandler();
        http.Enqueue(HttpStatusCode.OK,
            Fixture("anthropic-error-overloaded.sse.txt"));
        await SetProvidersAsync(
            new ProviderConnection("anthropic", "https://ant.test", "k"));

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(async () =>
            await DrainAsync(NewService(http).StreamCompletionAsync(
                Req("claude-sonnet-4", "anthropic", stream: true))));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.MidStream, Is.True);
            Assert.That(ex.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(ex.Body, Does.Contain("overloaded_error"));
        });
    }

    /// <summary>Covers RF-004: mensagem de rate limit mid-stream classifica
    /// 429 (heurística rate/quota → TooManyRequests).</summary>
    [Test]
    public async Task Anthropic_Stream_RateLimitError_MidStream429()
    {
        var http = new StubHandler();
        http.Enqueue(HttpStatusCode.OK,
            "data: {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\"," +
            "\"message\":\"rate limit exceeded\"}}\n\n");
        await SetProvidersAsync(
            new ProviderConnection("anthropic", "https://ant.test", "k"));

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(async () =>
            await DrainAsync(NewService(http).StreamCompletionAsync(
                Req("claude-sonnet-4", "anthropic", stream: true))));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.MidStream, Is.True);
            Assert.That(ex.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        });
    }

    /// <summary>Covers RF-004: RESOURCE_EXHAUSTED do Gemini mid-stream deve
    /// classificar 429 — hoje cai no 502 default (gap a corrigir na
    /// heurística do MidStreamProviderError).</summary>
    [Test]
    public async Task Google_Stream_ResourceExhausted_MidStream429()
    {
        var http = new StubHandler();
        http.Enqueue(HttpStatusCode.OK,
            Fixture("google-error-exhausted.sse.txt"));
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(async () =>
            await DrainAsync(NewService(http).StreamCompletionAsync(
                Req("gemini-2.0-flash", "google", stream: true))));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.MidStream, Is.True);
            Assert.That(ex.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        });
    }

    /// <summary>Covers RF-004: response 200 buffered com {"error":…} do
    /// Gemini vira MidStreamProviderError (não "resposta vazia").</summary>
    [Test]
    public async Task Google_Buffered200_ComError_ViraMidStream()
    {
        var http = new StubHandler();
        http.Enqueue(HttpStatusCode.OK,
            "{\"error\":{\"code\":400,\"message\":\"API key invalid\"}}");
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(async () =>
            await NewService(http).CompleteAsync(
                Req("gemini-2.0-flash", "google")));

        Assert.That(ex!.MidStream, Is.True);
    }

    /// <summary>Covers RF-004: response 200 buffered com {"error":…} do
    /// Claude deve virar MidStreamProviderError, não resposta vazia.</summary>
    [Test]
    public async Task Anthropic_Buffered200_ComError_ViraMidStream()
    {
        var http = new StubHandler();
        http.Enqueue(HttpStatusCode.OK,
            "{\"type\":\"error\",\"error\":{\"type\":\"api_error\"," +
            "\"message\":\"Internal error\"}}");
        await SetProvidersAsync(
            new ProviderConnection("anthropic", "https://ant.test", "k"));

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(async () =>
            await NewService(http).CompleteAsync(
                Req("claude-sonnet-4", "anthropic")));

        Assert.That(ex!.MidStream, Is.True);
    }

    // ---------- RF-003: replay de golden fixtures ----------

    /// <summary>Covers RF-001/RF-003: fixture SSE Claude (thinking + text +
    /// message_delta + stop) produz chunks OpenAI ordenados com raciocínio
    /// no canal próprio e [DONE] ao fim.</summary>
    [Test]
    public async Task Anthropic_Stream_GoldenFixture_ChunksOrdenados()
    {
        var http = new StubHandler();
        http.Enqueue(HttpStatusCode.OK,
            Fixture("anthropic-stream-happy.sse.txt"));
        await SetProvidersAsync(
            new ProviderConnection("anthropic", "https://ant.test", "k"));

        var lines = await DrainAsync(NewService(http).StreamCompletionAsync(
            Req("claude-sonnet-4", "anthropic", stream: true)));

        var all = string.Concat(lines);
        Assert.Multiple(() =>
        {
            Assert.That(all, Does.Contain("vou responder"));
            Assert.That(all, Does.Contain("reasoning"));
            Assert.That(all, Does.Contain("\"content\":\"ola\""));
            Assert.That(all, Does.Contain("\"content\":\" mundo\""));
            Assert.That(lines[^1], Is.EqualTo("data: [DONE]"));
        });
    }

    /// <summary>Covers RF-001/RF-003: fixture SSE Gemini (thought + text)
    /// produz chunks OpenAI com thought fora do content.</summary>
    [Test]
    public async Task Google_Stream_GoldenFixture_ChunksOrdenados()
    {
        var http = new StubHandler();
        http.Enqueue(HttpStatusCode.OK,
            Fixture("google-stream-happy.sse.txt"));
        await SetProvidersAsync(
            new ProviderConnection("google", "https://goog.test", "gk"));

        var lines = await DrainAsync(NewService(http).StreamCompletionAsync(
            Req("gemini-2.0-flash", "google", stream: true)));

        var all = string.Concat(lines);
        Assert.Multiple(() =>
        {
            Assert.That(all, Does.Contain("pensando antes de responder"));
            Assert.That(all, Does.Contain("reasoning"));
            Assert.That(all, Does.Contain("\"content\":\"oi\""));
            Assert.That(all, Does.Contain("\"content\":\" la\""));
            Assert.That(lines[^1], Is.EqualTo("data: [DONE]"));
        });
    }

    // ---------- infra: stub HTTP sem listener ----------

    /// <summary>Respostas enfileiradas por ordem de chamada; registra o
    /// wire (método, url, body) de cada request para asserts de payload.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body,
            TimeSpan? RetryAfter)> _responses = new();

        public List<(string Method, string Url, string Body)> Requests { get; } =
            new();

        public void Enqueue(
            HttpStatusCode status, string body, TimeSpan? retryAfter = null) =>
            _responses.Enqueue((status, body, retryAfter));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(ct)));

            var (status, body, retryAfter) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.NotFound, "{}", (TimeSpan?)null);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8,
                    body.StartsWith("data:", StringComparison.Ordinal)
                        ? "text/event-stream" : "application/json"),
            };
            if (retryAfter is not null)
            {
                response.Headers.RetryAfter =
                    new RetryConditionHeaderValue(retryAfter.Value);
            }
            return response;
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
