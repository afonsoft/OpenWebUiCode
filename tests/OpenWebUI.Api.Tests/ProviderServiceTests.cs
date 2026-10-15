using Microsoft.Extensions.Caching.Hybrid;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>Cobertura de branches do ProviderService: múltiplas URLs, dedup, erros do provider, timeout, roteamento e payloads de borda.</summary>
[TestFixture, IsolateEnvironment]
public class ProviderServiceTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private HybridCache _cache = null!;
    private ConfigService _config = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _mockUrl = null!;
    private TimeSpan? _clientTimeout;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly ConcurrentDictionary<string, (int Status, string Body, int DelayMs)> _routes = new();

    private sealed record RecordedRequest(
        string Path, string? Authorization, string Body,
        string? XApiKey = null, string? XGoogApiKey = null, string? AnthropicVersion = null);

    private sealed class FakeHttpClientFactory(TimeSpan? timeout) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient();
            if (timeout is not null)
            {
                client.Timeout = timeout.Value;
            }

            return client;
        }
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-provider-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(_db);
        _cache = TestCache.Create();
        _config = new ConfigService(_db, _cache);
        _mockUrl = StartMock();
    }

    [SetUp]
    public void SetUp()
    {
        _routes.Clear();
        _requests.Clear();
        _clientTimeout = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
                _db.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private ProviderService NewService() =>
        new(new FakeHttpClientFactory(_clientTimeout), _config,
            NullLogger<ProviderService>.Instance,
            // Cache novo por serviço: o cache de modelos (TTL 60s) é chaveado
            // pelo fingerprint das conexões — compartilhar entre testes faz um
            // teste ler a resposta em cache de outro.
            TestCache.Create());

    private Task SetConnectionsAsync(
        IReadOnlyList<string> ollama, IReadOnlyList<string> openAi, IReadOnlyList<string> keys) =>
        _config.SetAsync("connections", new ConnectionsConfig(ollama, openAi, keys));

    private static ChatCompletionRequest Req(string model, string? connection = null) =>
        new(model, [new ChatCompletionMessage("user", "oi")], Stream: false, Connection: connection);

    private static async Task<List<string>> DrainAsync(IAsyncEnumerable<string> lines)
    {
        var result = new List<string>();
        await foreach (var line in lines)
        {
            result.Add(line);
        }

        return result;
    }

    private string StartMock()
    {
        var random = new Random();
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(40000, 60000);
            _mock = new HttpListener();
            _mock.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _mock.Start();
                break;
            }
            catch (HttpListenerException)
            {
                _mock.Close();
            }
        }
        if (!_mock.IsListening)
        {
            throw new InvalidOperationException("Nenhuma porta livre para o mock.");
        }
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => MockLoopAsync(_mockCts.Token));
        return $"http://localhost:{port}";
    }

    private async Task MockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _mock.GetContextAsync().WaitAsync(ct);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Cada requisição responde em task própria para que uma rota
            // lenta (timeout) não serialize as demais.
            _ = Task.Run(() => RespondAsync(ctx, ct), ct);
        }
    }

    private async Task RespondAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Request.Url!.AbsolutePath;
            string body;
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                body = await reader.ReadToEndAsync(ct);
            }
            else
            {
                body = string.Empty;
            }
            _requests.Enqueue(new RecordedRequest(
                path, ctx.Request.Headers["Authorization"], body,
                ctx.Request.Headers["x-api-key"],
                ctx.Request.Headers["x-goog-api-key"],
                ctx.Request.Headers["anthropic-version"]));

            var (status, payload, delayMs) =
                _routes.TryGetValue(path, out var route) ? route : (404, "{}", 0);
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct);
            }

            var bytes = Encoding.UTF8.GetBytes(payload);
            ctx.Response.StatusCode = status;
            // Payload SSE (`data:`/`event:`) sai como text/event-stream:
            // o stream OpenAI+tools só entra no caminho streamed com esse
            // content-type — os demais parsers não olham o header.
            ctx.Response.ContentType =
                payload.StartsWith("data:") || payload.StartsWith("event:")
                || payload.Contains("\ndata:")
                    ? "text/event-stream"
                    : "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
        }
        catch (HttpListenerException) { /* cliente desconectou — ignora */ }
        catch (IOException) { /* cliente desconectou — ignora */ }
        catch (ObjectDisposedException) { /* listener parou */ }
    }

    [Test]
    public async Task ListModels_OllamaMultiplasUrls_DedupIgnoraVaziasEForaDoAr()
    {
        _routes["/api/tags"] = (200,
            "{\"models\":[" +
            "{\"model\":\"m1\",\"name\":\"M1\"}," +
            "{\"name\":\"m2\"}," +              // id cai para o campo name
            "{\"model\":\"\",\"name\":\"x\"}," + // id vazio: ignorado
            "{\"size\":1}]}", 0);               // sem id: ignorado
        // Segunda URL é o mesmo mock com barra final: todos os modelos viram duplicatas.
        await SetConnectionsAsync(
            [_mockUrl, _mockUrl + "/", "   ", "http://localhost:1"], [], []);

        var models = await NewService().ListModelsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(models.Select(m => m.Id), Is.EqualTo(new[] { "m1", "m2" }));
            Assert.That(models[0].Name, Is.EqualTo("M1"));
            Assert.That(models[0].Provider, Is.EqualTo("ollama"));
            Assert.That(models[0].OwnedBy, Is.EqualTo("ollama"));
            Assert.That(models[1].Name, Is.EqualTo("m2"));
            // A URL em branco e a fora do ar não geraram erro.
            Assert.That(
                _requests.Count(r => r.Path == "/api/tags"), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ListModels_OpenAiMultiplasUrls_ChavesPorIndiceOwnedByEDedup()
    {
        _routes["/models"] = (200,
            "{\"data\":[" +
            "{\"id\":\"gpt-1\",\"owned_by\":\"org\"}," +
            "{\"id\":\"\"}," +                    // id vazio: ignorado
            "{\"id\":\"gpt-1\"}," +               // duplicata entre URLs: ignorada
            "{\"id\":\"gpt-2\"}]}", 0);           // sem owned_by → OwnedBy null
        // Só uma chave para duas URLs: a segunda sai sem Authorization.
        await SetConnectionsAsync([], [_mockUrl, _mockUrl, "   "], ["sk-a"]);

        var models = await NewService().ListModelsAsync();

        var calls = _requests.Where(r => r.Path == "/models").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(models.Select(m => m.Id), Is.EqualTo(new[] { "gpt-1", "gpt-2" }));
            Assert.That(models[0].Provider, Is.EqualTo("openai"));
            Assert.That(models[0].OwnedBy, Is.EqualTo("org"));
            Assert.That(models[1].OwnedBy, Is.Null);
            Assert.That(calls, Has.Count.EqualTo(2));
            Assert.That(calls[0].Authorization, Is.EqualTo("Bearer sk-a"));
            Assert.That(calls[1].Authorization, Is.Null);
        });
    }

    [Test]
    public async Task ListModels_Providers500_RetornaVazio()
    {
        _routes["/api/tags"] = (500, "{}", 0);
        _routes["/models"] = (500, "{}", 0);
        await SetConnectionsAsync([_mockUrl], [_mockUrl], []);

        var models = await NewService().ListModelsAsync();

        Assert.That(models, Is.Empty);
    }

    [Test]
    public async Task ListModels_JsonInvalido_PropagaJsonException()
    {
        // Objeto sem a chave "models": cai no ?? [] e não gera modelo.
        _routes["/api/tags"] = (200, "{}", 0);
        // JSON malformado no provider OpenAI: JsonException não é capturada.
        _routes["/models"] = (200, "isso-nao-e-json", 0);
        await SetConnectionsAsync([_mockUrl], [_mockUrl], []);

        // JsonNode.Parse lança JsonReaderException (interna, deriva de JsonException).
        await Assert.CatchAsync<JsonException>(async () => await NewService().ListModelsAsync());
    }

    [Test]
    public async Task ListModels_RaizNaoObjeto_LancaInvalidOperation()
    {
        // Raiz JsonArray/JsonValue não tem indexador por nome: o serviço lança
        // InvalidOperationException (comportamento atual, não filtrado pelo catch).
        _routes["/api/tags"] = (200, "[]", 0);
        await SetConnectionsAsync([_mockUrl], [], []);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await NewService().ListModelsAsync());
    }

    [Test]
    public async Task ListModels_OpenAiLento_TimeoutIgnorado()
    {
        _routes["/api/tags"] = (200, "{\"models\":[{\"model\":\"m1\",\"name\":\"m1\"}]}", 0);
        _routes["/models"] = (200, "{\"data\":[]}", 10_000); // nunca responde a tempo
        _clientTimeout = TimeSpan.FromMilliseconds(200);
        await SetConnectionsAsync([_mockUrl], [_mockUrl], []);

        var models = await NewService().ListModelsAsync();

        // TaskCanceledException do timeout é engolida: só o Ollama respondeu.
        Assert.That(models.Select(m => m.Id), Is.EqualTo(new[] { "m1" }));
    }

    [Test]
    public async Task Complete_ModeloOllama_RetornaConteudo_EStringVaziaSemContent()
    {
        _routes["/api/tags"] = (200, "{\"models\":[{\"model\":\"m1\",\"name\":\"m1\"}]}", 0);
        _routes["/api/chat"] = (200, "{\"message\":{\"content\":\"resposta ok\"},\"done\":true}", 0);
        await SetConnectionsAsync([_mockUrl], [], []);
        var svc = NewService();

        var text = await svc.CompleteAsync(Req("m1"));
        Assert.That(text, Is.EqualTo("resposta ok"));

        // Resposta sem message.content cai no ?? string.Empty.
        _routes["/api/chat"] = (200, "{\"done\":true}", 0);
        var empty = await svc.CompleteAsync(Req("m1"));
        Assert.That(empty, Is.EqualTo(string.Empty));
    }

    [Test]
    public async Task Complete_ModeloForaDoOllama_RoteiaParaOpenAiComApiKey()
    {
        _routes["/api/tags"] = (200, "{\"models\":[{\"model\":\"m1\",\"name\":\"m1\"}]}", 0);
        _routes["/chat/completions"] = (200,
            "{\"choices\":[{\"message\":{\"content\":\"oi openai\"}}]}", 0);
        await SetConnectionsAsync([_mockUrl], [_mockUrl], ["sk-b"]);

        var text = await NewService().CompleteAsync(Req("gpt-9"));

        Assert.Multiple(() =>
        {
            Assert.That(text, Is.EqualTo("oi openai"));
            // "gpt-9" não está no Ollama e há URL OpenAI → provider openai.
            Assert.That(
                _requests.Last(r => r.Path == "/chat/completions").Authorization,
                Is.EqualTo("Bearer sk-b"));
        });
    }

    [Test]
    public async Task Resolve_ConexaoExplicita_PulaResolucaoPorModelo()
    {
        _routes["/api/tags"] = (200, "{\"models\":[{\"model\":\"m1\",\"name\":\"m1\"}]}", 0);
        _routes["/chat/completions"] = (200,
            "{\"choices\":[{\"message\":{\"content\":\"oi openai\"}}]}", 0);
        await SetConnectionsAsync([_mockUrl], [_mockUrl], []);

        // m1 existe no Ollama, mas Connection="openai" manda direto pro OpenAI.
        var text = await NewService().CompleteAsync(Req("m1", connection: "openai"));

        Assert.Multiple(() =>
        {
            Assert.That(text, Is.EqualTo("oi openai"));
            // A resolução não consultou a listagem de modelos.
            Assert.That(_requests.Any(r => r.Path == "/api/tags"), Is.False);
        });
    }

    [Test]
    public async Task Resolve_ConexaoInvalida_LancaInvalidOperation()
    {
        await SetConnectionsAsync([_mockUrl], [_mockUrl], []);
        var svc = NewService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.CompleteAsync(Req("m1", connection: "bogus")));
        Assert.That(ex!.Message, Does.Contain("m1"));

        // O mesmo switch lança ao enumerar o stream.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DrainAsync(svc.StreamCompletionAsync(Req("m1", connection: "bogus"))));

        // OpenAI só com URL em branco: FirstOpenAiConnection não acha nenhuma.
        await SetConnectionsAsync([], ["   "], []);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.CompleteAsync(Req("g", connection: "openai")));
    }

    [Test]
    public async Task Resolve_SemNenhumProvider_LancaSemUrlOllama()
    {
        await SetConnectionsAsync([], [], []);
        var svc = NewService();

        // Modelo desconhecido sem OpenAI cai no ramo "ollama" sem URL → lança.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.CompleteAsync(Req("qualquer")));
        Assert.That(ex!.Message, Does.Contain("Ollama"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DrainAsync(svc.StreamCompletionAsync(Req("qualquer"))));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.CompleteWithToolsAsync(Req("qualquer")));
    }

    [Test]
    public async Task Complete_Provider500_PropagaHttpRequestException()
    {
        _routes["/api/chat"] = (500, "{}", 0);
        _routes["/chat/completions"] = (500, "{}", 0);
        await SetConnectionsAsync([_mockUrl], [_mockUrl], []);
        var svc = NewService();

        await Assert.MultipleAsync(async () =>
        {
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
                await svc.CompleteAsync(Req("m1", connection: "ollama")));
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
                await svc.CompleteAsync(Req("g", connection: "openai")));
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
                await svc.CompleteWithToolsAsync(Req("m1", connection: "ollama")));
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
                await DrainAsync(svc.StreamCompletionAsync(Req("g", connection: "openai"))));
        });
    }

    [Test]
    public async Task Stream_Ollama_PulaLinhasVaziasEJsonInvalido_TerminaEmDone()
    {
        _routes["/api/chat"] = (200,
            "{\"message\":{\"content\":\"a\"},\"done\":false}\n" +
            "\n" +
            "linha-que-nao-e-json\n" +
            "{\"done\":true}\n", 0); // sem message: content vazio + done
        await SetConnectionsAsync([_mockUrl], [], []);

        var lines = await DrainAsync(
            NewService().StreamCompletionAsync(Req("m1", connection: "ollama")));

        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Count.EqualTo(3));
            var chunk1 = JsonNode.Parse(lines[0]["data: ".Length..])!;
            Assert.That(
                chunk1["choices"]![0]!["delta"]!["content"]!.GetValue<string>(),
                Is.EqualTo("a"));
            Assert.That(chunk1["object"]!.GetValue<string>(),
                Is.EqualTo("chat.completion.chunk"));
            Assert.That(chunk1["choices"]![0]!["finish_reason"], Is.Null);
            var chunk2 = JsonNode.Parse(lines[1]["data: ".Length..])!;
            Assert.That(chunk2["choices"]![0]!["finish_reason"]!.GetValue<string>(),
                Is.EqualTo("stop"));
            Assert.That(lines[2], Is.EqualTo("data: [DONE]"));
        });
    }

    [Test]
    public async Task Stream_Ollama_ParamsViramOptions_MaxTokensPermaneceNoTopo()
    {
        _routes["/api/chat"] = (200, "{\"done\":true}\n", 0);
        await SetConnectionsAsync([_mockUrl], [], []);
        using var doc = JsonDocument.Parse("[1,2]");
        var request = new ChatCompletionRequest(
            "m1",
            [
                new ChatCompletionMessage("user", "oi", ToolCallId: "tc-9",
                    ToolCallsJson: "[{\"id\":\"c\",\"function\":{\"name\":\"f\",\"arguments\":{}}}]"),
                new ChatCompletionMessage("assistant", "certo"),
            ],
            Stream: true,
            Connection: "ollama",
            Tools: [doc.RootElement],
            Params: new Dictionary<string, object>
            {
                ["temperature"] = 0.7,      // double → options
                ["top_p"] = 0.9,            // double → options
                ["top_k"] = 5,              // int → options
                ["num_predict"] = 16,       // int → options
                ["repeat_penalty"] = 1.1f,  // float → options
                ["seed"] = 7L,              // long → options
                ["stop"] = "fim",           // string → options
                ["max_tokens"] = 99,        // não está na lista de movidos: fica no topo
                ["decimalParam"] = 1.5m,    // decimal
                ["boolParam"] = true,       // bool
                ["nullParam"] = null!,      // null
                ["jsonParam"] = doc.RootElement, // JsonElement
                ["objParam"] = new[] { 1, 2 },   // fallback SerializeToNode
            });

        var lines = await DrainAsync(NewService().StreamCompletionAsync(request));

        var sent = JsonNode.Parse(_requests.Last(r => r.Path == "/api/chat").Body)!;
        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Count.EqualTo(2)); // chunk done + [DONE]
            var options = sent["options"]!;
            Assert.That(options["temperature"]!.GetValue<double>(), Is.EqualTo(0.7));
            Assert.That(options["top_p"]!.GetValue<double>(), Is.EqualTo(0.9));
            Assert.That(options["top_k"]!.GetValue<int>(), Is.EqualTo(5));
            Assert.That(options["num_predict"]!.GetValue<int>(), Is.EqualTo(16));
            Assert.That(options["repeat_penalty"]!.GetValue<float>(), Is.EqualTo(1.1f));
            Assert.That(options["seed"]!.GetValue<long>(), Is.EqualTo(7L));
            Assert.That(options["stop"]!.GetValue<string>(), Is.EqualTo("fim"));
            // Chaves fora da lista ficam no topo (inclui max_tokens).
            Assert.That(sent["temperature"], Is.Null);
            Assert.That(sent["max_tokens"]!.GetValue<int>(), Is.EqualTo(99));
            Assert.That(sent["decimalParam"]!.GetValue<decimal>(), Is.EqualTo(1.5m));
            Assert.That(sent["boolParam"]!.GetValue<bool>(), Is.True);
            // Param null vira propriedade JSON null (JsonObject mantém a chave).
            Assert.That(sent.AsObject().ContainsKey("nullParam"), Is.True);
            Assert.That(sent["nullParam"], Is.Null);
            Assert.That(sent["jsonParam"]!.AsArray(), Has.Count.EqualTo(2));
            Assert.That(sent["objParam"]!.AsArray(), Has.Count.EqualTo(2));
            var msg0 = sent["messages"]![0]!;
            Assert.That(msg0["tool_call_id"]!.GetValue<string>(), Is.EqualTo("tc-9"));
            Assert.That(msg0["tool_calls"], Is.Not.Null);
            Assert.That(sent["messages"]![1]!["tool_call_id"], Is.Null);
            Assert.That(sent["tools"], Is.Not.Null);
            Assert.That(sent["stream"]!.GetValue<bool>(), Is.True);
        });
    }

    [Test]
    public async Task Stream_OpenAi_EncaminhaLinhasEParaEmDone()
    {
        _routes["/chat/completions"] = (200,
            "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n" +
            "\n" +
            "data: [DONE]\n" +
            "data: {\"depois\":true}\n", 0); // linha após DONE não deve sair
        await SetConnectionsAsync([], [_mockUrl], []); // sem chave: sai sem Authorization

        var lines = await DrainAsync(
            NewService().StreamCompletionAsync(Req("g", connection: "openai")));

        Assert.Multiple(() =>
        {
            Assert.That(lines, Is.EqualTo(new[]
            {
                "data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}",
                "data: [DONE]",
            }));
            Assert.That(
                _requests.Last(r => r.Path == "/chat/completions").Authorization,
                Is.Null);
        });
    }

    [Test]
    public async Task CompleteWithTools_OpenAi_NormalizaToolCallsEArguments()
    {
        _routes["/chat/completions"] = (200,
            "{\"choices\":[{\"message\":{\"content\":\"\",\"tool_calls\":[" +
            "{\"id\":\"c1\",\"function\":{\"name\":\"f1\",\"arguments\":\"{\\\"a\\\":1}\"}}," +
            "{\"function\":{\"name\":\"f2\",\"arguments\":{\"b\":2}}}," +
            "{\"id\":\"c3\",\"function\":{\"name\":null}}]}}]}", 0); // sem name: ignorada
        await SetConnectionsAsync([], [_mockUrl], []);

        var result = await NewService().CompleteWithToolsAsync(Req("g", connection: "openai"));

        Assert.Multiple(() =>
        {
            Assert.That(result.ToolCalls, Has.Count.EqualTo(2));
            Assert.That(result.ToolCalls[0].Id, Is.EqualTo("c1"));
            Assert.That(result.ToolCalls[0].Name, Is.EqualTo("f1"));
            // arguments como string JSON (JsonValue): preservada.
            Assert.That(result.ToolCalls[0].ArgumentsJson, Is.EqualTo("{\"a\":1}"));
            // sem id: gera GUID "N"; arguments objeto: serializado.
            Assert.That(result.ToolCalls[1].Id, Does.Match("^[0-9a-f]{32}$"));
            Assert.That(result.ToolCalls[1].Name, Is.EqualTo("f2"));
            Assert.That(result.ToolCalls[1].ArgumentsJson, Is.EqualTo("{\"b\":2}"));
            Assert.That(result.ToolCallsJson, Does.Contain("f1"));
        });
    }

    [Test]
    public async Task CompleteWithTools_Ollama_SemCalls_RetornaCallsJsonVazio()
    {
        _routes["/api/chat"] = (200, "{\"message\":{\"content\":\"so texto\"}}", 0);
        await SetConnectionsAsync([_mockUrl], [], []);

        var result = await NewService().CompleteWithToolsAsync(Req("m1", connection: "ollama"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo("so texto"));
            Assert.That(result.ToolCalls, Is.Empty);
            Assert.That(result.ToolCallsJson, Is.EqualTo("[]"));
        });
    }

    // ---------------- Conexões tipadas (anthropic / google) ----------------

    private Task SetProvidersAsync(params ProviderConnection[] providers) =>
        _config.SetAsync("connections", new ConnectionsConfig([], [], [], Providers: providers));

    [Test]
    public async Task ListModels_Anthropic_ListaNativoComHeaders()
    {
        _routes["/v1/models"] = (200,
            "{\"data\":[{\"id\":\"claude-3-5-sonnet-20241022\",\"display_name\":\"Claude 3.5 Sonnet\"}," +
            "{\"id\":\"claude-3-haiku-20240307\"}]}", 0);
        await SetProvidersAsync(new ProviderConnection("anthropic", _mockUrl, "sk-ant-test"));

        var models = await NewService().ListModelsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(models, Has.Count.EqualTo(2));
            Assert.That(models[0].Id, Is.EqualTo("claude-3-5-sonnet-20241022"));
            Assert.That(models[0].Name, Is.EqualTo("Claude 3.5 Sonnet"));
            Assert.That(models[0].Provider, Is.EqualTo("anthropic"));
            Assert.That(models[1].Name, Is.EqualTo("claude-3-haiku-20240307"));
        });
        var req = _requests.Single(r => r.Path == "/v1/models");
        Assert.Multiple(() =>
        {
            Assert.That(req.XApiKey, Is.EqualTo("sk-ant-test"));
            Assert.That(req.AnthropicVersion, Is.EqualTo("2023-06-01"));
            Assert.That(req.Authorization, Is.Null);
        });
    }

    [Test]
    public async Task ListModels_Google_ListaNativoStripPrefix()
    {
        _routes["/models"] = (200,
            "{\"models\":[{\"name\":\"models/gemini-2.0-flash\",\"displayName\":\"Gemini 2.0 Flash\"}," +
            "{\"name\":\"models/gemini-1.5-pro\"}]}", 0);
        await SetProvidersAsync(new ProviderConnection("google", _mockUrl, "gk-test"));

        var models = await NewService().ListModelsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(models, Has.Count.EqualTo(2));
            Assert.That(models[0].Id, Is.EqualTo("gemini-2.0-flash"));
            Assert.That(models[0].Name, Is.EqualTo("Gemini 2.0 Flash"));
            Assert.That(models[0].Provider, Is.EqualTo("google"));
        });
        var req = _requests.Single(r => r.Path == "/models");
        Assert.That(req.XGoogApiKey, Is.EqualTo("gk-test"));
    }

    [Test]
    public async Task ListModelsForConnection_AnanthropicEGoogle()
    {
        _routes["/v1/models"] = (200, "{\"data\":[{\"id\":\"c1\"}]}", 0);
        _routes["/models"] = (200, "{\"models\":[{\"name\":\"models/g1\"}]}", 0);
        await SetProvidersAsync(
            new ProviderConnection("anthropic", _mockUrl, "k1"),
            new ProviderConnection("google", _mockUrl, "k2"));

        var service = NewService();
        var anthropic = await service.ListModelsForConnectionAsync("anthropic", 0);
        var google = await service.ListModelsForConnectionAsync("google", 0);
        var outOfRange = await service.ListModelsForConnectionAsync("google", 5);

        Assert.Multiple(() =>
        {
            Assert.That(anthropic.Select(m => m.Id), Is.EqualTo(new[] { "c1" }));
            Assert.That(google.Select(m => m.Id), Is.EqualTo(new[] { "g1" }));
            Assert.That(outOfRange, Is.Empty);
        });
    }

    [Test]
    public async Task ListModels_TypedProvider_SemKeyNaoChama()
    {
        await SetProvidersAsync(new ProviderConnection("anthropic", _mockUrl, null));

        var models = await NewService().ListModelsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(models, Is.Empty);
            Assert.That(_requests, Is.Empty);
        });
    }

    [Test]
    public async Task Complete_AutoResolve_ModeloAnthropic_VaiParaMessages()
    {
        _routes["/v1/models"] = (200, "{\"data\":[{\"id\":\"claude-x\"}]}", 0);
        _routes["/v1/messages"] = (200,
            "{\"content\":[{\"type\":\"text\",\"text\":\"resposta claude\"}]}", 0);
        await SetProvidersAsync(new ProviderConnection("anthropic", _mockUrl, "k"));

        var result = await NewService().CompleteWithToolsAsync(Req("claude-x"));

        Assert.That(result.Content, Is.EqualTo("resposta claude"));
        var req = _requests.Single(r => r.Path == "/v1/messages");
        Assert.Multiple(() =>
        {
            Assert.That(req.XApiKey, Is.EqualTo("k"));
            Assert.That(req.AnthropicVersion, Is.EqualTo("2023-06-01"));
        });
        var body = JsonNode.Parse(req.Body)!;
        Assert.Multiple(() =>
        {
            Assert.That(body["model"]!.GetValue<string>(), Is.EqualTo("claude-x"));
            Assert.That(body["max_tokens"]!.GetValue<int>(), Is.EqualTo(4096));
            Assert.That(body["stream"]!.GetValue<bool>(), Is.False);
        });
    }

    [Test]
    public async Task CompleteAnthropic_Payload_SystemTopLevelEToolResult()
    {
        _routes["/v1/messages"] = (200,
            "{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}," +
            "{\"type\":\"tool_use\",\"id\":\"tu_1\",\"name\":\"get_time\",\"input\":{\"tz\":\"UTC\"}}]}", 0);
        await SetProvidersAsync(new ProviderConnection("anthropic", _mockUrl, "k"));

        var toolCalls = "[{\"id\":\"tu_0\",\"type\":\"function\",\"function\":" +
            "{\"name\":\"get_time\",\"arguments\":\"{\\\"tz\\\":\\\"UTC\\\"}\"}}]";
        var tools = new[] { JsonDocument.Parse(
            "{\"type\":\"function\",\"function\":{\"name\":\"get_time\",\"description\":\"hora\"," +
            "\"parameters\":{\"type\":\"object\",\"properties\":{\"tz\":{\"type\":\"string\"}}}}}").RootElement };
        var request = new ChatCompletionRequest(
            "claude",
            [
                new ChatCompletionMessage("system", "seja direto"),
                new ChatCompletionMessage("user", "que horas?"),
                new ChatCompletionMessage("assistant", "vou ver", ToolCallsJson: toolCalls),
                new ChatCompletionMessage("tool", "12:00", ToolCallId: "tu_0"),
                new ChatCompletionMessage("tool", "UTC", ToolCallId: "tu_0"),
            ],
            Stream: false,
            Connection: "anthropic",
            Tools: tools);

        var result = await NewService().CompleteWithToolsAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo("ok"));
            Assert.That(result.ToolCalls, Has.Count.EqualTo(1));
            Assert.That(result.ToolCalls[0].Id, Is.EqualTo("tu_1"));
            Assert.That(result.ToolCalls[0].ArgumentsJson, Is.EqualTo("{\"tz\":\"UTC\"}"));
            Assert.That(result.ToolCallsJson, Does.Contain("\"name\":\"get_time\""));
        });

        var body = JsonNode.Parse(_requests.Single(r => r.Path == "/v1/messages").Body)!;
        var messages = body["messages"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(body["system"]!.GetValue<string>(), Is.EqualTo("seja direto"));
            // system fora das messages; tool_calls -> blocos tool_use; tools -> input_schema.
            Assert.That(messages.Select(m => m!["role"]!.GetValue<string>()),
                Is.EqualTo(new[] { "user", "assistant", "user" }));
            Assert.That(messages[1]!["content"]!.AsArray()[1]!["type"]!.GetValue<string>(),
                Is.EqualTo("tool_use"));
            Assert.That(messages[1]!["content"]!.AsArray()[1]!["input"]!["tz"]!.GetValue<string>(),
                Is.EqualTo("UTC"));
            // Dois tool results consecutivos agrupados numa única mensagem user.
            Assert.That(messages[2]!["content"]!.AsArray(), Has.Count.EqualTo(2));
            Assert.That(messages[2]!["content"]!.AsArray()[0]!["type"]!.GetValue<string>(),
                Is.EqualTo("tool_result"));
            Assert.That(body["tools"]!.AsArray()[0]!["input_schema"]!["properties"]!["tz"],
                Is.Not.Null);
            Assert.That(body["tools"]!.AsArray()[0]!["description"]!.GetValue<string>(),
                Is.EqualTo("hora"));
        });
    }

    [Test]
    public async Task StreamAnthropic_TextoViraChunkOpenAi()
    {
        _routes["/v1/messages"] = (200,
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"oi\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" mundo\"}}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n", 0);
        await SetProvidersAsync(new ProviderConnection("anthropic", _mockUrl, "k"));

        var lines = await DrainAsync(NewService().StreamCompletionAsync(
            Req("claude", connection: "anthropic")));

        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Does.Contain("\"content\":\"oi\""));
            Assert.That(lines[1], Does.Contain("\"content\":\" mundo\""));
            Assert.That(lines[^1], Is.EqualTo("data: [DONE]"));
        });
        var body = JsonNode.Parse(_requests.Single(r => r.Path == "/v1/messages").Body)!;
        Assert.That(body["stream"]!.GetValue<bool>(), Is.True);
    }

    [Test]
    public async Task StreamAnthropicTools_ToolUseAcumulado()
    {
        _routes["/v1/messages"] = (200,
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"vou chamar\"}}\n\n" +
            "event: content_block_start\n" +
            "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"tu_9\",\"name\":\"get_time\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"tz\\\":\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"UTC\\\"}\"}}\n\n" +
            "event: content_block_stop\n" +
            "data: {\"type\":\"content_block_stop\",\"index\":1}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n", 0);
        await SetProvidersAsync(new ProviderConnection("anthropic", _mockUrl, "k"));

        var deltas = new List<string>();
        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("claude", connection: "anthropic"),
            (d, _) => { deltas.Add(d); return Task.CompletedTask; });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Content, Is.EqualTo("vou chamar"));
            Assert.That(deltas, Is.EqualTo(new[] { "vou chamar" }));
            Assert.That(result.ToolCalls, Has.Count.EqualTo(1));
            Assert.That(result.ToolCalls[0].Id, Is.EqualTo("tu_9"));
            Assert.That(result.ToolCalls[0].Name, Is.EqualTo("get_time"));
            Assert.That(result.ToolCalls[0].ArgumentsJson, Is.EqualTo("{\"tz\":\"UTC\"}"));
            Assert.That(result.ToolCallsJson, Does.Contain("\"id\":\"tu_9\""));
        });
    }

    [Test]
    public async Task CompleteAnthropic_Params_Whitelist()
    {
        _routes["/v1/messages"] = (200, "{\"content\":[{\"type\":\"text\",\"text\":\"x\"}]}", 0);
        await SetProvidersAsync(new ProviderConnection("anthropic", _mockUrl, "k"));
        var request = new ChatCompletionRequest(
            "claude",
            [new ChatCompletionMessage("user", "oi")],
            Stream: false,
            Connection: "anthropic",
            Params: new Dictionary<string, object>
            {
                ["max_tokens"] = 128,
                ["temperature"] = 0.5,
                ["stop"] = "FIM",
                ["frequency_penalty"] = 0.9, // não existe na Messages API — descartado
            });

        await NewService().CompleteAsync(request);

        var body = JsonNode.Parse(_requests.Single(r => r.Path == "/v1/messages").Body)!;
        Assert.Multiple(() =>
        {
            Assert.That(body["max_tokens"]!.GetValue<int>(), Is.EqualTo(128));
            Assert.That(body["temperature"]!.GetValue<double>(), Is.EqualTo(0.5));
            Assert.That(body["stop_sequences"]!.AsArray()[0]!.GetValue<string>(), Is.EqualTo("FIM"));
            Assert.That(body["frequency_penalty"], Is.Null);
        });
    }

    [Test]
    public async Task CompleteGoogle_Payload_ModelRolesEFunctionResponse()
    {
        _routes["/models/gemini-2.0-flash:generateContent"] = (200,
            "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"certo\"}," +
            "{\"functionCall\":{\"name\":\"get_time\",\"args\":{\"tz\":\"UTC\"}}}]}}]}", 0);
        await SetProvidersAsync(new ProviderConnection("google", _mockUrl, "gk"));

        var toolCalls = "[{\"id\":\"c1\",\"type\":\"function\",\"function\":" +
            "{\"name\":\"get_time\",\"arguments\":\"{\\\"tz\\\":\\\"UTC\\\"}\"}}]";
        var tools = new[] { JsonDocument.Parse(
            "{\"type\":\"function\",\"function\":{\"name\":\"get_time\",\"description\":\"hora\"," +
            "\"parameters\":{\"type\":\"object\",\"properties\":{\"tz\":{\"type\":\"string\"}}}}}").RootElement };
        var request = new ChatCompletionRequest(
            "gemini-2.0-flash",
            [
                new ChatCompletionMessage("system", "seja breve"),
                new ChatCompletionMessage("user", "horas?"),
                new ChatCompletionMessage("assistant", "", ToolCallsJson: toolCalls),
                new ChatCompletionMessage("tool", "12:00", ToolCallId: "c1"),
            ],
            Stream: false,
            Connection: "google",
            Tools: tools,
            Params: new Dictionary<string, object> { ["max_tokens"] = 64 });

        var result = await NewService().CompleteWithToolsAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo("certo"));
            Assert.That(result.ToolCalls, Has.Count.EqualTo(1));
            Assert.That(result.ToolCalls[0].Name, Is.EqualTo("get_time"));
            Assert.That(result.ToolCalls[0].ArgumentsJson, Is.EqualTo("{\"tz\":\"UTC\"}"));
        });

        var req = _requests.Single(r => r.Path == "/models/gemini-2.0-flash:generateContent");
        Assert.That(req.XGoogApiKey, Is.EqualTo("gk"));
        var body = JsonNode.Parse(req.Body)!;
        var contents = body["contents"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>(),
                Is.EqualTo("seja breve"));
            Assert.That(contents.Select(c => c!["role"]!.GetValue<string>()),
                Is.EqualTo(new[] { "user", "model", "user" }));
            Assert.That(contents[1]!["parts"]!.AsArray()[0]!["functionCall"]!["name"]!.GetValue<string>(),
                Is.EqualTo("get_time"));
            // functionResponse resolve o nome pelo id registrado nos tool_calls.
            Assert.That(contents[2]!["parts"]!.AsArray()[0]!["functionResponse"]!["name"]!.GetValue<string>(),
                Is.EqualTo("get_time"));
            Assert.That(contents[2]!["parts"]!.AsArray()[0]!["functionResponse"]!["response"]!["result"]!.GetValue<string>(),
                Is.EqualTo("12:00"));
            Assert.That(body["tools"]!.AsArray()[0]!["functionDeclarations"]!.AsArray()[0]!["name"]!.GetValue<string>(),
                Is.EqualTo("get_time"));
            Assert.That(body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>(), Is.EqualTo(64));
        });
    }

    [Test]
    public async Task StreamGoogle_TextoEFunctionCall()
    {
        _routes["/models/g1:streamGenerateContent"] = (200,
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"vou\"}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\" ver\"}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"name\":\"f\",\"args\":{\"a\":1}}}]}}]}\n\n", 0);
        await SetProvidersAsync(new ProviderConnection("google", _mockUrl, "gk"));

        var deltas = new List<string>();
        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("g1", connection: "google"),
            (d, _) => { deltas.Add(d); return Task.CompletedTask; });

        Assert.Multiple(() =>
        {
            Assert.That(result!.Content, Is.EqualTo("vou ver"));
            Assert.That(deltas, Is.EqualTo(new[] { "vou", " ver" }));
            Assert.That(result.ToolCalls, Has.Count.EqualTo(1));
            Assert.That(result.ToolCalls[0].Name, Is.EqualTo("f"));
            Assert.That(result.ToolCalls[0].ArgumentsJson, Is.EqualTo("{\"a\":1}"));
        });
    }

    [Test]
    public async Task Resolve_AutoResolve_ModeloOllama_GanhaDeOpenAi()
    {
        // Mesmo id exposto por ollama e anthropic: precedência ollama primeiro.
        _routes["/api/tags"] = (200, "{\"models\":[{\"model\":\"m1\",\"name\":\"m1\"}]}", 0);
        _routes["/v1/models"] = (200, "{\"data\":[{\"id\":\"m1\"}]}", 0);
        _routes["/api/chat"] = (200, "{\"message\":{\"content\":\"ok ollama\"},\"done\":true}", 0);
        await _config.SetAsync("connections", new ConnectionsConfig(
            [_mockUrl], [], [],
            Providers: [new ProviderConnection("anthropic", _mockUrl, "k")]));

        var content = await NewService().CompleteAsync(Req("m1"));

        Assert.That(content, Is.EqualTo("ok ollama"));
        Assert.That(_requests.Any(r => r.Path == "/api/chat"), Is.True);
    }


    [Test]
    public async Task StreamGoogle_TextoPuro_ChunksFormatoOpenAi()
    {
        _routes["/models/g1:streamGenerateContent"] = (200,
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"oi\"}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\" la\"}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[]}}]}\n\n", 0);
        await SetProvidersAsync(new ProviderConnection("google", _mockUrl, "gk"));

        var lines = await DrainAsync(NewService().StreamCompletionAsync(
            Req("g1", connection: "google")));

        Assert.Multiple(() =>
        {
            Assert.That(lines.Count, Is.EqualTo(3));
            Assert.That(lines[0], Does.Contain("\"delta\":{\"content\":\"oi\"}"));
            Assert.That(lines[1], Does.Contain("\"content\":\" la\""));
            Assert.That(lines[2], Is.EqualTo("data: [DONE]"));
        });
    }

    // ---- SPEC-20261013-chat-media-attachments RF-006: payload multimodal por provider ----

    [Test]
    public async Task CompleteOllama_MensagemComImagem_CampoImages()
    {
        _routes["/api/chat"] = (200, "{\"message\":{\"content\":\"ok\"},\"done\":true}", 0);
        await SetConnectionsAsync([_mockUrl], [], []);
        var b64 = Convert.ToBase64String([1, 2, 3, 4]);
        var request = new ChatCompletionRequest(
            "m1",
            [new ChatCompletionMessage("user", "olha",
                Images: [new ChatImageData("image/png", b64)])],
            Stream: false,
            Connection: "ollama");

        await NewService().CompleteAsync(request);

        var body = JsonNode.Parse(_requests.Single(r => r.Path == "/api/chat").Body)!;
        var msg = body["messages"]!.AsArray()[0]!;
        Assert.Multiple(() =>
        {
            Assert.That(msg["content"]!.GetValue<string>(), Is.EqualTo("olha"));
            Assert.That(msg["images"]!.AsArray()[0]!.GetValue<string>(), Is.EqualTo(b64));
        });
    }

    [Test]
    public async Task CompleteOpenAi_MensagemComImagem_ContentComImageUrl()
    {
        _routes["/chat/completions"] = (200,
            "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);
        var request = new ChatCompletionRequest(
            "m1",
            [new ChatCompletionMessage("user", "olha",
                Images: [new ChatImageData("image/png", "QUJD")])],
            Stream: false,
            Connection: "openai");

        await NewService().CompleteAsync(request);

        var body = JsonNode.Parse(_requests.Single(r => r.Path == "/chat/completions").Body)!;
        var content = body["messages"]!.AsArray()[0]!["content"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(content[0]!["type"]!.GetValue<string>(), Is.EqualTo("text"));
            Assert.That(content[0]!["text"]!.GetValue<string>(), Is.EqualTo("olha"));
            Assert.That(content[1]!["type"]!.GetValue<string>(), Is.EqualTo("image_url"));
            Assert.That(content[1]!["image_url"]!["url"]!.GetValue<string>(),
                Is.EqualTo("data:image/png;base64,QUJD"));
        });
    }

    [Test]
    public async Task CompleteAnthropic_MensagemComImagem_BlocoImageBase64()
    {
        _routes["/v1/messages"] = (200, "{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}", 0);
        await SetProvidersAsync(new ProviderConnection("anthropic", _mockUrl, "k"));
        var request = new ChatCompletionRequest(
            "claude",
            [new ChatCompletionMessage("user", "olha",
                Images: [new ChatImageData("image/png", "QUJD")])],
            Stream: false,
            Connection: "anthropic");

        await NewService().CompleteAsync(request);

        var body = JsonNode.Parse(_requests.Single(r => r.Path == "/v1/messages").Body)!;
        var content = body["messages"]!.AsArray()[0]!["content"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(content[0]!["type"]!.GetValue<string>(), Is.EqualTo("text"));
            Assert.That(content[1]!["type"]!.GetValue<string>(), Is.EqualTo("image"));
            Assert.That(content[1]!["source"]!["type"]!.GetValue<string>(), Is.EqualTo("base64"));
            Assert.That(content[1]!["source"]!["media_type"]!.GetValue<string>(), Is.EqualTo("image/png"));
            Assert.That(content[1]!["source"]!["data"]!.GetValue<string>(), Is.EqualTo("QUJD"));
        });
    }

    [Test]
    public async Task CompleteOllama_MensagemSemImagem_ContentStringSemImages()
    {
        _routes["/api/chat"] = (200, "{\"message\":{\"content\":\"ok\"},\"done\":true}", 0);
        await SetConnectionsAsync([_mockUrl], [], []);

        await NewService().CompleteAsync(Req("m1"));

        var body = JsonNode.Parse(_requests.Single(r => r.Path == "/api/chat").Body)!;
        var msg = body["messages"]!.AsArray()[0]!;
        Assert.Multiple(() =>
        {
            Assert.That(msg["content"]!.GetValue<string>(), Is.EqualTo("oi"));
            Assert.That(msg["images"], Is.Null);
        });
    }

    // ---- SPEC-20261015-chat-thinking-stream: reasoning/thinking por provider ----

    [Test]
    public async Task StreamOpenAiTools_ReasoningVariants_ViramOnReasoning()
    {
        // RF-001: gateways OpenAI-compatíveis emitem reasoning em 3 formatos —
        // reasoning_content (deepseek/qwen), reasoning (openrouter/gpt-oss) e
        // reasoning_details[] (openrouter detalhado). Todos viram onReasoning;
        // nunca se misturam ao content.
        _routes["/chat/completions"] = (200,
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"pensando \"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning\":\"mais \"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_details\":[{\"type\":\"reasoning.text\",\"text\":\"detalhe \"}]}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"resp\"}}]}\n\n" +
            "data: [DONE]\n\n", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var deltas = new List<string>();
        var reasoning = new List<string>();
        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("m1", connection: "openai"),
            (d, _) => { deltas.Add(d); return Task.CompletedTask; },
            (r, _) => { reasoning.Add(r); return Task.CompletedTask; });

        Assert.Multiple(() =>
        {
            Assert.That(reasoning, Is.EqualTo(new[] { "pensando ", "mais ", "detalhe " }));
            Assert.That(deltas, Is.EqualTo(new[] { "resp" }));
            Assert.That(result!.Content, Is.EqualTo("resp"));
        });
    }

    [Test]
    public async Task StreamOpenAiTools_RespostaNaoSSE_ReasoningBuffered()
    {
        // Endpoint que ignora stream:true e devolve JSON: o reasoning da
        // mensagem buffered ainda deve sair no ProviderCompletion.Reasoning.
        _routes["/chat/completions"] = (200,
            "{\"choices\":[{\"message\":{\"content\":\"resp\",\"reasoning_content\":\"pensei\"}}]}", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("m1", connection: "openai"), null, null);

        Assert.Multiple(() =>
        {
            Assert.That(result!.Content, Is.EqualTo("resp"));
            Assert.That(result.Reasoning, Is.EqualTo("pensei"));
        });
    }

    [Test]
    public async Task StreamAnthropicTools_ThinkingDelta_ViraOnReasoning()
    {
        // RF-001: thinking_delta → onReasoning; signature_delta é ignorada
        // silenciosamente (metadado criptográfico, não texto).
        _routes["/v1/messages"] = (200,
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hmm \"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"oi\"}}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n", 0);
        await SetProvidersAsync(new ProviderConnection("anthropic", _mockUrl, "k"));

        var deltas = new List<string>();
        var reasoning = new List<string>();
        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("claude", connection: "anthropic"),
            (d, _) => { deltas.Add(d); return Task.CompletedTask; },
            (r, _) => { reasoning.Add(r); return Task.CompletedTask; });

        Assert.Multiple(() =>
        {
            Assert.That(reasoning, Is.EqualTo(new[] { "hmm " }));
            Assert.That(deltas, Is.EqualTo(new[] { "oi" }));
            Assert.That(result!.Content, Is.EqualTo("oi"));
        });
    }

    [Test]
    public async Task StreamGoogleTools_ThoughtPart_ViraOnReasoning()
    {
        // RF-001: parts com thought:true são a cadeia de pensamento do
        // Gemini — hoje vazam para o content; devem ir para onReasoning.
        _routes["/models/g1:streamGenerateContent"] = (200,
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"pensando \",\"thought\":true}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"oi\"}]}}]}\n\n", 0);
        await SetProvidersAsync(new ProviderConnection("google", _mockUrl, "gk"));

        var deltas = new List<string>();
        var reasoning = new List<string>();
        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("g1", connection: "google"),
            (d, _) => { deltas.Add(d); return Task.CompletedTask; },
            (r, _) => { reasoning.Add(r); return Task.CompletedTask; });

        Assert.Multiple(() =>
        {
            Assert.That(reasoning, Is.EqualTo(new[] { "pensando " }));
            Assert.That(deltas, Is.EqualTo(new[] { "oi" }));
            Assert.That(result!.Content, Is.EqualTo("oi"));
        });
    }

    [Test]
    public async Task StreamOllamaTools_Thinking_ViraOnReasoning()
    {
        // RF-001: Ollama (modelos think) emite message.thinking separado
        // de message.content na mesma linha NDJSON.
        _routes["/api/chat"] = (200,
            "{\"message\":{\"role\":\"assistant\",\"thinking\":\"hmm \",\"content\":\"\"},\"done\":false}\n" +
            "{\"message\":{\"role\":\"assistant\",\"content\":\"oi\"},\"done\":true}\n", 0);
        await SetConnectionsAsync([_mockUrl], [], []);

        var deltas = new List<string>();
        var reasoning = new List<string>();
        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("m1", connection: "ollama"),
            (d, _) => { deltas.Add(d); return Task.CompletedTask; },
            (r, _) => { reasoning.Add(r); return Task.CompletedTask; });

        Assert.Multiple(() =>
        {
            Assert.That(reasoning, Is.EqualTo(new[] { "hmm " }));
            Assert.That(deltas, Is.EqualTo(new[] { "oi" }));
            Assert.That(result!.Content, Is.EqualTo("oi"));
        });
    }

    [Test]
    public async Task StreamAnthropic_ThinkingDelta_ViraReasoningChunk()
    {
        // RF-002 (caminho sem tools): thinking_delta vira delta.reasoning
        // no chunk OpenAI normalizado; signature_delta é ignorada.
        _routes["/v1/messages"] = (200,
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"hmm \"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig\"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"oi\"}}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n", 0);
        await SetProvidersAsync(new ProviderConnection("anthropic", _mockUrl, "k"));

        var lines = await DrainAsync(NewService().StreamCompletionAsync(
            Req("claude", connection: "anthropic")));

        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Does.Contain("\"reasoning\":\"hmm \""));
            Assert.That(lines[1], Does.Contain("\"content\":\"oi\""));
            Assert.That(lines[^1], Is.EqualTo("data: [DONE]"));
            Assert.That(lines, Has.Count.EqualTo(3));
        });
    }

    [Test]
    public async Task StreamGoogle_ThoughtPart_ViraReasoningChunk()
    {
        // RF-002 (sem tools): part thought:true vira delta.reasoning.
        _routes["/models/g1:streamGenerateContent"] = (200,
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"pensando \",\"thought\":true}]}}]}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"oi\"}]}}]}\n\n", 0);
        await SetProvidersAsync(new ProviderConnection("google", _mockUrl, "gk"));

        var lines = await DrainAsync(NewService().StreamCompletionAsync(
            Req("g1", connection: "google")));

        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Does.Contain("\"reasoning\":\"pensando \""));
            Assert.That(lines[1], Does.Contain("\"content\":\"oi\""));
            Assert.That(lines[^1], Is.EqualTo("data: [DONE]"));
        });
    }

    [Test]
    public async Task StreamOllama_Thinking_ViraReasoningChunk()
    {
        // RF-002 (sem tools): message.thinking vira delta.reasoning no
        // chunk traduzido — content vazio não gera lixo.
        _routes["/api/chat"] = (200,
            "{\"message\":{\"role\":\"assistant\",\"thinking\":\"hmm \",\"content\":\"\"},\"done\":false}\n" +
            "{\"message\":{\"role\":\"assistant\",\"content\":\"oi\"},\"done\":true}\n", 0);
        await SetConnectionsAsync([_mockUrl], [], []);

        var lines = await DrainAsync(NewService().StreamCompletionAsync(Req("m1")));

        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Does.Contain("\"reasoning\":\"hmm \""));
            Assert.That(lines[1], Does.Contain("\"content\":\"oi\""));
            Assert.That(lines[^1], Is.EqualTo("data: [DONE]"));
        });
    }

    [Test]
    public async Task StreamOpenAi_ReasoningContent_PassaCruParaOCliente()
    {
        // RF-002 (sem tools, OpenAI-compatível): o pass-through repassa as
        // linhas cruas — o campo nativo do provider chega intacto ao
        // cliente, que faz o mapping (ChatStreamService).
        _routes["/chat/completions"] = (200,
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hmm\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"oi\"}}]}\n\n" +
            "data: [DONE]\n\n", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var lines = await DrainAsync(NewService().StreamCompletionAsync(
            Req("m1", connection: "openai")));

        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Does.Contain("reasoning_content"));
            Assert.That(lines[1], Does.Contain("\"content\":\"oi\""));
        });
    }

    [Test]
    public async Task CompleteGoogle_ThoughtPart_SaiDoContent()
    {
        // Buffered: part thought:true ia parar no Content (vazamento do
        // raciocínio interno na resposta persistida) — agora vai para
        // Reasoning e some do Content.
        _routes["/models/g1:generateContent"] = (200,
            "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[" +
            "{\"text\":\"pensando \",\"thought\":true},{\"text\":\"oi\"}]}}]}", 0);
        await SetProvidersAsync(new ProviderConnection("google", _mockUrl, "gk"));

        var result = await NewService().CompleteWithToolsAsync(
            Req("g1", connection: "google"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo("oi"));
            Assert.That(result.Reasoning, Is.EqualTo("pensando "));
        });
    }

    [Test]
    public async Task CompleteAnthropic_ThinkingBlock_ViraReasoning()
    {
        // Buffered: bloco content[type=thinking] vai para Reasoning, não
        // para o Content persistido.
        _routes["/v1/messages"] = (200,
            "{\"content\":[{\"type\":\"thinking\",\"thinking\":\"pensei \"}," +
            "{\"type\":\"text\",\"text\":\"oi\"}]}", 0);
        await SetProvidersAsync(new ProviderConnection("anthropic", _mockUrl, "k"));

        var result = await NewService().CompleteWithToolsAsync(
            Req("claude", connection: "anthropic"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo("oi"));
            Assert.That(result.Reasoning, Is.EqualTo("pensei "));
        });
    }

    // ---- DeepSeek DSML: tool calls entregues como markup no texto ----

    [Test]
    public async Task StreamOpenAiTools_DsmlNoReasoning_ViraToolCallSemVazar()
    {
        // DeepSeek via gateway compatível emite <｜DSML｜tool_calls> dentro
        // de reasoning_content em vez de delta.tool_calls — o filtro
        // converte em ProviderToolCall e o markup não vaza na UI.
        _routes["/chat/completions"] = (200,
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"preciso ler \"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"<｜DSML｜tool_calls><｜DSML｜invoke name=\\\"file_read\\\"><｜DSML｜parameter name=\\\"path\\\" string=\\\"true\\\">a.md</｜DSML｜parameter><｜DSML｜parameter name=\\\"offset\\\" string=\\\"false\\\">26</｜DSML｜parameter></｜DSML｜invoke>\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"</｜DSML｜tool_calls>\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"}}]}\n\n" +
            "data: [DONE]\n\n", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var deltas = new List<string>();
        var reasoning = new List<string>();
        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("m1", connection: "openai"),
            (d, _) => { deltas.Add(d); return Task.CompletedTask; },
            (r, _) => { reasoning.Add(r); return Task.CompletedTask; });

        Assert.Multiple(() =>
        {
            var call = result!.ToolCalls.Single();
            Assert.That(call.Name, Is.EqualTo("file_read"));
            var args = JsonNode.Parse(call.ArgumentsJson)!;
            Assert.That(args["path"]!.GetValue<string>(), Is.EqualTo("a.md"));
            Assert.That(args["offset"]!.GetValue<int>(), Is.EqualTo(26));
            Assert.That(string.Concat(reasoning), Is.EqualTo("preciso ler "));
            Assert.That(deltas, Is.EqualTo(new[] { "ok" }));
            // Echo no formato tool_calls p/ o modelo ver na próxima rodada.
            Assert.That(result.ToolCallsJson, Does.Contain("\"file_read\""));
        });
    }

    [Test]
    public async Task StreamOpenAiTools_DsmlQuebradoEntreChunks_AindaParseia()
    {
        // O marcador pode chegar partido no meio ("<｜DSML｜tool_" + "calls>")
        // — o filtro segura o sufixo suspeito até o bloco fechar.
        _routes["/chat/completions"] = (200,
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"vou ler <｜DSML｜tool_\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"calls><｜DSML｜invoke name=\\\"search\\\"><｜DSML｜parameter name=\\\"q\\\" string=\\\"true\\\">x</｜DSML｜parameter></｜DSML｜invoke></｜DSML｜tool_calls> pronto\"}}]}\n\n" +
            "data: [DONE]\n\n", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var deltas = new List<string>();
        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("m1", connection: "openai"),
            (d, _) => { deltas.Add(d); return Task.CompletedTask; }, null);

        Assert.Multiple(() =>
        {
            var call = result!.ToolCalls.Single();
            Assert.That(call.Name, Is.EqualTo("search"));
            Assert.That(JsonNode.Parse(call.ArgumentsJson)!["q"]!.GetValue<string>(),
                Is.EqualTo("x"));
            Assert.That(result.Content, Is.EqualTo("vou ler  pronto"));
            Assert.That(string.Concat(deltas), Does.Not.Contain("DSML"));
        });
    }

    [Test]
    public async Task CompleteOpenAi_DsmlNoContentBuffered_ViraToolCall()
    {
        // Resposta buffered (endpoint ignorou stream): markup no content
        // também vira call e sai do texto persistido.
        _routes["/chat/completions"] = (200,
            "{\"choices\":[{\"message\":{\"content\":\"feito<｜DSML｜tool_calls>" +
            "<｜DSML｜invoke name=\\\"git_commit\\\"></｜DSML｜invoke>" +
            "</｜DSML｜tool_calls>\"}}]}", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var result = await NewService().CompleteWithToolsAsync(
            Req("m1", connection: "openai"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo("feito"));
            var call = result.ToolCalls.Single();
            Assert.That(call.Name, Is.EqualTo("git_commit"));
            Assert.That(result.ToolCallsJson, Does.Contain("\"git_commit\""));
        });
    }

    [Test]
    public async Task StreamOpenAi_SemTools_DsmlNaoVazaNaUi()
    {
        // Caminho sem tools: markup nunca vira call (nada a executar), mas
        // também não pode poluir o texto do usuário nem o reasoning.
        _routes["/chat/completions"] = (200,
            "data: {\"choices\":[{\"delta\":{\"content\":\"lendo <｜DSML｜tool_calls><｜DSML｜invoke name=\\\"x\\\"></｜DSML｜invoke></｜DSML｜tool_calls>\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"pens<｜DSML｜tool_calls><｜DSML｜invoke name=\\\"y\\\"></｜DSML｜invoke></｜DSML｜tool_calls>ou\"}}]}\n\n" +
            "data: [DONE]\n\n", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var lines = await DrainAsync(NewService().StreamCompletionAsync(
            Req("m1", connection: "openai")));

        Assert.Multiple(() =>
        {
            var all = string.Concat(lines);
            Assert.That(all, Does.Not.Contain("DSML"));
            Assert.That(all, Does.Contain("lendo "));
            Assert.That(all, Does.Contain("pensou"));
            Assert.That(lines[^1], Is.EqualTo("data: [DONE]"));
        });
    }

    [Test]
    public async Task StreamOpenAiTools_DsmlParamJsonInvalido_ViraString()
    {
        // string="false" com valor não-JSON cai no fallback de texto em vez
        // de quebrar o parse do invoke.
        _routes["/chat/completions"] = (200,
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"<｜DSML｜tool_calls><｜DSML｜invoke name=\\\"w\\\"><｜DSML｜parameter name=\\\"p\\\" string=\\\"false\\\">not[json</｜DSML｜parameter></｜DSML｜invoke></｜DSML｜tool_calls>\"}}]}\n\n" +
            "data: [DONE]\n\n", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("m1", connection: "openai"),
            (d, _) => Task.CompletedTask, null);

        var call = result!.ToolCalls.Single();
        Assert.That(
            JsonNode.Parse(call.ArgumentsJson)!["p"]!.GetValue<string>(),
            Is.EqualTo("not[json"));
    }

    [Test]
    public async Task StreamOpenAiTools_DsmlBlocoTruncado_SalvaInvokesCompletos()
    {
        // Stream morre no meio do bloco: invoke completo ainda vira call,
        // fragmento final é descartado sem vazar markup.
        _routes["/chat/completions"] = (200,
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok <｜DSML｜tool_calls><｜DSML｜invoke name=\\\"a\\\"></｜DSML｜invoke><｜DSML｜invoke name=\\\"b\\\"\"}}]}\n\n" +
            "data: [DONE]\n\n", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var deltas = new List<string>();
        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("m1", connection: "openai"),
            (d, _) => { deltas.Add(d); return Task.CompletedTask; }, null);

        Assert.Multiple(() =>
        {
            Assert.That(result!.ToolCalls.Select(c => c.Name),
                Is.EqualTo(new[] { "a" }));
            Assert.That(string.Concat(deltas), Is.EqualTo("ok "));
        });
    }

    [Test]
    public async Task StreamOpenAiTools_PrefixoDeMarcadorFalso_TextoPreservado()
    {
        // Texto que só parece marcador ("<｜DSML｜tool_x") é segurado um
        // chunk e depois liberado inteiro — nada é perdido.
        _routes["/chat/completions"] = (200,
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"a <｜DSML｜tool_\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"x> b\"}}]}\n\n" +
            "data: [DONE]\n\n", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("m1", connection: "openai"),
            (d, _) => Task.CompletedTask, null);

        Assert.Multiple(() =>
        {
            Assert.That(result!.ToolCalls, Is.Empty);
            Assert.That(result.Content, Is.EqualTo("a <｜DSML｜tool_x> b"));
        });
    }

    [Test]
    public async Task StreamOpenAiTools_DsmlMultiplosInvokes_TodasViramCalls()
    {
        // Payload real observado: 3 invokes num único bloco.
        _routes["/chat/completions"] = (200,
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"<｜DSML｜tool_calls>" +
            "<｜DSML｜invoke name=\\\"f1\\\"><｜DSML｜parameter name=\\\"p\\\" string=\\\"true\\\">a</｜DSML｜parameter></｜DSML｜invoke>" +
            "<｜DSML｜invoke name=\\\"f2\\\"></｜DSML｜invoke>" +
            "<｜DSML｜invoke name=\\\"f3\\\"><｜DSML｜parameter name=\\\"n\\\" string=\\\"false\\\">[1,2]</｜DSML｜parameter></｜DSML｜invoke>" +
            "</｜DSML｜tool_calls>\"}}]}\n\n" +
            "data: [DONE]\n\n", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var result = await NewService().CompleteWithToolsStreamingAsync(
            Req("m1", connection: "openai"),
            (d, _) => Task.CompletedTask, null);

        Assert.Multiple(() =>
        {
            Assert.That(result!.ToolCalls.Select(c => c.Name),
                Is.EqualTo(new[] { "f1", "f2", "f3" }));
            Assert.That(JsonNode.Parse(result.ToolCalls[2].ArgumentsJson)!["n"]!
                .AsArray().Count, Is.EqualTo(2));
            Assert.That(result.ToolCalls.Select(c => c.Id),
                Is.EqualTo(new[] { "dsml_0", "dsml_1", "dsml_2" }));
        });
    }

    [Test]
    public async Task CompleteOpenAi_DsmlNoReasoningBuffered_ViraToolCall()
    {
        // Buffered: markup dentro de reasoning_content também é extraído.
        _routes["/chat/completions"] = (200,
            "{\"choices\":[{\"message\":{\"content\":\"oi\"," +
            "\"reasoning_content\":\"p<｜DSML｜tool_calls><｜DSML｜invoke name=\\\"r\\\"></｜DSML｜invoke></｜DSML｜tool_calls>e\"}}]}", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var result = await NewService().CompleteWithToolsAsync(
            Req("m1", connection: "openai"));

        Assert.Multiple(() =>
        {
            Assert.That(result.ToolCalls.Single().Name, Is.EqualTo("r"));
            Assert.That(result.Reasoning, Is.EqualTo("pe"));
            Assert.That(result.Content, Is.EqualTo("oi"));
        });
    }

    [Test]
    public async Task StreamOpenAi_SemDone_SoltaTailSegurado()
    {
        // Stream termina sem [DONE]: o sufixo segurado pelo filtro ainda
        // chega à UI num delta sintético.
        _routes["/chat/completions"] = (200,
            "data: {\"choices\":[{\"delta\":{\"content\":\"abc <\"}}]}\n\n", 0);
        await SetConnectionsAsync([], [_mockUrl], ["k"]);

        var lines = await DrainAsync(NewService().StreamCompletionAsync(
            Req("m1", connection: "openai")));

        // O "<" chega no delta sintético, escapado pelo encoder JSON.
        var all = string.Concat(lines);
        Assert.Multiple(() =>
        {
            Assert.That(all, Does.Contain("abc "));
            Assert.That(all, Does.Contain("\\u003C"));
        });
    }
}
