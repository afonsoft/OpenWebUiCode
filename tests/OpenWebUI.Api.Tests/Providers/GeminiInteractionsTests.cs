using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests.Providers;

/// <summary>
/// SPEC-20261103-gemini-interactions-api: flag <c>interactions_api</c> por
/// conexão roteia o path Google para <c>POST {base}/interactions</c>
/// (RF-005); wire novo — input/system_instruction/steps tipados
/// (RF-001), tools function/natives (RF-002), parse de steps/usage/
/// thought (RF-003), stream step.* → SSE OpenAI (RF-004) e erros na
/// mesma taxonomia ProviderHttpException (RF-006). Flag ausente mantém
/// generateContent (regressão zero).
/// </summary>
[TestFixture, IsolateEnvironment]
public class GeminiInteractionsTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-git-{Guid.NewGuid():N}.db");
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

    private ProviderService NewService(StubHttpHandler handler) =>
        new(new StubHttpFactory(handler), _config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());

    private Task SetProvidersAsync(params ProviderConnection[] providers) =>
        _config.SetAsync("connections",
            new ConnectionsConfig([], [], [], Providers: providers));

    private static JsonObject InteractionsSettings(object? flag) =>
        flag switch
        {
            bool b => new() { ["interactions_api"] = b },
            string s => new() { ["interactions_api"] = s },
            _ => new(),
        };

    private static ProviderConnection GoogleConn(object? flag) =>
        new("google", "https://goog.test", "gk", Settings: InteractionsSettings(flag));

    private static ProviderConnection GoogleConn() =>
        new("google", "https://goog.test", "gk",
            Settings: InteractionsSettings(true));

    private static ChatCompletionRequest Req(
        IReadOnlyList<ChatCompletionMessage>? messages = null,
        bool stream = false,
        Dictionary<string, object>? extraParams = null,
        IReadOnlyList<JsonElement>? tools = null) =>
        new("gemini-3.8-flash",
            messages ?? [new ChatCompletionMessage("user", "oi")],
            Stream: stream, Connection: "google",
            Params: extraParams, Tools: tools);

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

    private async Task<RecordedHttpRequest> CaptureLastRequestAsync(
        StubHttpHandler http, ChatCompletionRequest req, string responseBody)
    {
        http.Enqueue(HttpStatusCode.OK, responseBody);
        await NewService(http).CompleteAsync(req);
        return http.Requests[^1];
    }

    private const string BufferedOk =
        "{\"id\":\"v1_x\",\"status\":\"completed\",\"object\":\"interaction\"," +
        "\"model\":\"gemini-3.8-flash\"," +
        "\"steps\":[{\"type\":\"model_output\",\"content\":" +
        "[{\"type\":\"text\",\"text\":\"ok\"}]}]}";

    // ---------- RF-005: flag + fallback ----------

    /// <summary>Covers RF-005/AC-1: flag on → request vai a
    /// <c>{base}/interactions</c> com <c>x-goog-api-key</c> e
    /// <c>Api-Revision: 2026-05-20</c>; body carrega model/input/store:false.</summary>
    [Test]
    public async Task FlagOn_PostEmInteractions_ComApiRevision()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(GoogleConn());

        var wire = await CaptureLastRequestAsync(http, Req(), BufferedOk);

        var body = JsonNode.Parse(wire.Body)!;
        Assert.Multiple(() =>
        {
            Assert.That(wire.Method, Is.EqualTo("POST"));
            Assert.That(wire.Url, Is.EqualTo("https://goog.test/interactions"));
            Assert.That(wire.Headers["x-goog-api-key"], Is.EqualTo("gk"));
            Assert.That(wire.Headers["Api-Revision"], Is.EqualTo("2026-05-20"));
            Assert.That(body["model"]!.GetValue<string>(),
                Is.EqualTo("gemini-3.8-flash"));
            Assert.That(body["store"]!.GetValue<bool>(), Is.False);
        });
    }

    /// <summary>Covers RF-005/AC-5: flag ausente → path legacy
    /// generateContent intacto (zero regressão).</summary>
    [Test]
    public async Task FlagAusente_UsaGenerateContent()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(new ProviderConnection(
            "google", "https://goog.test", "gk"));

        var wire = await CaptureLastRequestAsync(http, Req(),
            "{\"candidates\":[{\"content\":{\"role\":\"model\"," +
            "\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}");

        Assert.That(wire.Url, Does.Contain(":generateContent"));
    }

    /// <summary>Covers RF-005: flag false explícita também cai no legacy;
    /// string "true" ativa (Settings JSON pode vir serializado).</summary>
    [Test]
    public async Task FlagFalseOuStringTrue_DispatchCorreto()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(GoogleConn(false));
        var wireLegacy = await CaptureLastRequestAsync(http, Req(),
            "{\"candidates\":[{\"content\":{\"role\":\"model\"," +
            "\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}");

        await SetProvidersAsync(GoogleConn("true"));
        var wireNovo = await CaptureLastRequestAsync(http, Req(), BufferedOk);

        Assert.Multiple(() =>
        {
            Assert.That(wireLegacy.Url, Does.Contain(":generateContent"));
            Assert.That(wireNovo.Url, Does.EndWith("/interactions"));
        });
    }

    // ---------- RF-001: request shape ----------

    /// <summary>Covers RF-001: pergunta única colapsa para input string
    /// (forma canônica da doc).</summary>
    [Test]
    public async Task PerguntaUnica_InputString()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(GoogleConn());

        var wire = await CaptureLastRequestAsync(http, Req(), BufferedOk);

        Assert.That(JsonNode.Parse(wire.Body)!["input"]!.GetValue<string>(),
            Is.EqualTo("oi"));
    }

    /// <summary>Covers RF-001: histórico multi-turn → system_instruction
    /// top-level + steps[] user_input/model_output; stream:true no body
    /// do path streamed.</summary>
    [Test]
    public async Task Historico_ViraStepsTipados()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(GoogleConn());
        var messages = new List<ChatCompletionMessage>
        {
            new("system", "seja breve"),
            new("user", "primeira"),
            new("assistant", "resposta um"),
            new("user", "segunda"),
        };

        var wire = await CaptureLastRequestAsync(http,
            Req(messages), BufferedOk);

        var body = JsonNode.Parse(wire.Body)!;
        var steps = body["input"]!.AsArray();
        Assert.Multiple(() =>
        {
            Assert.That(body["system_instruction"]!.GetValue<string>(),
                Is.EqualTo("seja breve"));
            Assert.That(steps.Select(s => s!["type"]!.GetValue<string>()),
                Is.EqualTo(new[] { "user_input", "model_output", "user_input" }));
            Assert.That(steps[0]!["content"]![0]!["text"]!.GetValue<string>(),
                Is.EqualTo("primeira"));
            Assert.That(steps[1]!["content"]![0]!["text"]!.GetValue<string>(),
                Is.EqualTo("resposta um"));
        });
    }

    /// <summary>Covers RF-002: tool_calls do assistant → steps
    /// function_call; mensagem tool → function_result com o call_id do
    /// step original (round-trip preservado).</summary>
    [Test]
    public async Task ToolCalls_ViramFunctionCallEFunctionResult()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(GoogleConn());
        var callsJson = "[{\"id\":\"call_1\",\"type\":\"function\"," +
            "\"function\":{\"name\":\"get_weather\"," +
            "\"arguments\":\"{\\\"loc\\\":\\\"sp\\\"}\"}}]";
        var messages = new List<ChatCompletionMessage>
        {
            new("user", "clima?"),
            new("assistant", "", ToolCallsJson: callsJson),
            new("tool", "25C", ToolCallId: "call_1"),
        };

        var wire = await CaptureLastRequestAsync(http,
            Req(messages), BufferedOk);

        var steps = JsonNode.Parse(wire.Body)!["input"]!.AsArray();
        var functionCall = steps.First(s =>
            s!["type"]!.GetValue<string>() == "function_call")!;
        var functionResult = steps.First(s =>
            s!["type"]!.GetValue<string>() == "function_result")!;
        Assert.Multiple(() =>
        {
            Assert.That(functionCall["name"]!.GetValue<string>(),
                Is.EqualTo("get_weather"));
            Assert.That(functionCall["id"]!.GetValue<string>(),
                Is.EqualTo("call_1"));
            Assert.That(functionCall["arguments"]!["loc"]!.GetValue<string>(),
                Is.EqualTo("sp"));
            Assert.That(functionResult["call_id"]!.GetValue<string>(),
                Is.EqualTo("call_1"));
            Assert.That(functionResult["name"]!.GetValue<string>(),
                Is.EqualTo("get_weather"));
        });
    }

    // ---------- RF-002: tools ----------

    /// <summary>Covers RF-002: tools OpenAI → entries
    /// <c>{type:"function",name,description,parameters}</c> diretas no
    /// tools[] (sem wrapper functionDeclarations).</summary>
    [Test]
    public async Task Tools_ViramFunctionDeclarationsDiretas()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(GoogleConn());
        var toolDef = JsonDocument.Parse(
            "{\"type\":\"function\",\"function\":{\"name\":\"get_time\"," +
            "\"description\":\"hora atual\",\"parameters\":" +
            "{\"type\":\"object\",\"properties\":{\"tz\":{\"type\":\"string\"}}}}}");

        var wire = await CaptureLastRequestAsync(http,
            Req(tools: [toolDef.RootElement.Clone()]), BufferedOk);

        var tools = JsonNode.Parse(wire.Body)!["tools"]!.AsArray();
        var fn = tools.Single()!;
        Assert.Multiple(() =>
        {
            Assert.That(fn["type"]!.GetValue<string>(), Is.EqualTo("function"));
            Assert.That(fn["name"]!.GetValue<string>(), Is.EqualTo("get_time"));
            Assert.That(fn["description"]!.GetValue<string>(),
                Is.EqualTo("hora atual"));
            Assert.That(fn["parameters"]!["properties"]!["tz"],
                Is.Not.Null);
        });
    }

    /// <summary>Covers RF-002: native_tools csv → entries diretas
    /// {type:"google_search"|"code_execution"|"url_context"}.</summary>
    [Test]
    public async Task NativeTools_EntradasDiretas()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(GoogleConn());

        var wire = await CaptureLastRequestAsync(http,
            Req(extraParams: new()
            {
                ["native_tools"] = "google_search;code_execution",
            }), BufferedOk);

        var tools = JsonNode.Parse(wire.Body)!["tools"]!.AsArray();
        var types = tools.Select(t => t!["type"]!.GetValue<string>()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(types, Does.Contain("google_search"));
            Assert.That(types, Does.Contain("code_execution"));
            Assert.That(types, Does.Not.Contain("url_context"));
        });
    }

    /// <summary>Covers RF-001: params de geração → generation_config
    /// snake_case (max_tokens → max_output_tokens; thinking_level
    /// preservado; stop → stop_sequences).</summary>
    [Test]
    public async Task Params_ViramGenerationConfig()
    {
        var http = new StubHttpHandler();
        await SetProvidersAsync(GoogleConn());

        var wire = await CaptureLastRequestAsync(http,
            Req(extraParams: new()
            {
                ["temperature"] = 0.4,
                ["max_tokens"] = 128,
                ["thinking_level"] = "high",
                ["stop"] = "FIM",
            }), BufferedOk);

        var cfg = JsonNode.Parse(wire.Body)!["generation_config"]!;
        Assert.Multiple(() =>
        {
            Assert.That(cfg["temperature"]!.GetValue<double>(), Is.EqualTo(0.4));
            Assert.That(cfg["max_output_tokens"]!.GetValue<int>(), Is.EqualTo(128));
            Assert.That(cfg["thinking_level"]!.GetValue<string>(), Is.EqualTo("high"));
            Assert.That(cfg["stop_sequences"]![0]!.GetValue<string>(), Is.EqualTo("FIM"));
        });
    }

    // ---------- RF-003: response parse ----------

    /// <summary>Covers RF-003: steps[] buffered — model_output → content,
    /// thought.summary → reasoning (fora do content), function_call →
    /// tool_calls OpenAI-shape; usage total_* → ProviderUsage.</summary>
    [Test]
    public async Task Buffered_ParseSteps_UsageReasoningCalls()
    {
        var http = new StubHttpHandler();
        http.Enqueue(HttpStatusCode.OK, Fixture("interactions-complete.json"));
        await SetProvidersAsync(GoogleConn());

        var result = await NewService(http).CompleteWithToolsAsync(Req());

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo("Consultando o clima..."));
            Assert.That(result.Reasoning,
                Does.Contain("temperatura"));
            Assert.That(result.Reasoning, Does.Contain("get_current_temperature"));
            Assert.That(result.ToolCalls, Has.Count.EqualTo(1));
            Assert.That(result.ToolCalls[0].Id, Is.EqualTo("call_abc123"));
            Assert.That(result.ToolCalls[0].Name,
                Is.EqualTo("get_current_temperature"));
            Assert.That(result.ToolCalls[0].ArgumentsJson,
                Does.Contain("London"));
            var callsJson = JsonNode.Parse(result.ToolCallsJson)!.AsArray();
            Assert.That(callsJson[0]!["function"]!["name"]!.GetValue<string>(),
                Is.EqualTo("get_current_temperature"));
            Assert.That(result.Usage!.PromptTokens, Is.EqualTo(150));
            Assert.That(result.Usage.CompletionTokens, Is.EqualTo(12));
            Assert.That(result.Usage.ReasoningTokens, Is.EqualTo(35));
            Assert.That(result.FinishReason, Is.EqualTo("requires_action"));
        });
    }

    // ---------- RF-004: streaming ----------

    /// <summary>Covers RF-004: fixture SSE step.* — delta text → content
    /// chunks, thought_summary/signature → reasoning, termina em
    /// [DONE].</summary>
    [Test]
    public async Task Stream_TextEThought_ChunksOpenAI()
    {
        var http = new StubHttpHandler();
        http.Enqueue(HttpStatusCode.OK, Fixture("interactions-stream.sse"));
        await SetProvidersAsync(GoogleConn());

        var lines = await DrainAsync(NewService(http).StreamCompletionAsync(
            Req(stream: true)));

        var all = string.Concat(lines);
        Assert.Multiple(() =>
        {
            Assert.That(all, Does.Contain("\"content\":\"oi \""));
            Assert.That(all, Does.Contain("\"content\":\"mundo\""));
            Assert.That(all, Does.Contain("raciocinando "));
            Assert.That(all, Does.Contain("reasoning"));
            Assert.That(lines[^1], Is.EqualTo("data: [DONE]"));
        });
    }

    /// <summary>Covers RF-004/RF-002: step.start function_call abre a
    /// call, arguments_delta acumula o JSON e step.stop fecha —
    /// ProviderCompletion carrega call id/name/args completos.</summary>
    [Test]
    public async Task Stream_FunctionCall_ArgsDeltaAcumulados()
    {
        var http = new StubHttpHandler();
        http.Enqueue(HttpStatusCode.OK,
            Fixture("interactions-stream-tools.sse"));
        await SetProvidersAsync(GoogleConn());

        var result = await NewService(http).CompleteWithToolsStreamingAsync(
            Req(stream: true), onDelta: null);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.ToolCalls, Has.Count.EqualTo(1));
            Assert.That(result.ToolCalls[0].Id, Is.EqualTo("call_xyz789"));
            Assert.That(result.ToolCalls[0].Name, Is.EqualTo("get_weather"));
            Assert.That(result.ToolCalls[0].ArgumentsJson,
                Is.EqualTo("{\"location\": \"San Francisco\"}"));
            Assert.That(result.Usage!.PromptTokens, Is.EqualTo(50));
            Assert.That(result.Usage.CompletionTokens, Is.EqualTo(10));
            Assert.That(result.FinishReason, Is.EqualTo("requires_action"));
        });
    }

    /// <summary>Covers RF-004: deltas de texto no stream com tools também
    /// chegam ao callback onDelta e ao content agregado.</summary>
    [Test]
    public async Task StreamTools_TextDelta_VaiParaOnDelta()
    {
        var http = new StubHttpHandler();
        http.Enqueue(HttpStatusCode.OK, Fixture("interactions-stream.sse"));
        await SetProvidersAsync(GoogleConn());
        var deltas = new List<string>();
        var reasoning = new List<string>();

        var result = await NewService(http).CompleteWithToolsStreamingAsync(
            Req(stream: true),
            onDelta: (d, _) => { deltas.Add(d); return Task.CompletedTask; },
            onReasoning: (r, _) => { reasoning.Add(r); return Task.CompletedTask; });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Content, Is.EqualTo("oi mundo"));
            Assert.That(string.Concat(deltas), Is.EqualTo("oi mundo"));
            Assert.That(string.Concat(reasoning), Does.Contain("raciocinando "));
            Assert.That(result.Usage!.PromptTokens, Is.EqualTo(10));
        });
    }

    // ---------- RF-006: erros ----------

    /// <summary>Covers RF-006: evento error mid-stream →
    /// ProviderHttpException MidStream (rate/quota → 429 pela
    /// heurística).</summary>
    [Test]
    public async Task Stream_EventoError_ViraMidStream()
    {
        var http = new StubHttpHandler();
        http.Enqueue(HttpStatusCode.OK, Fixture("interactions-error.sse"));
        await SetProvidersAsync(GoogleConn());

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(async () =>
            await DrainAsync(NewService(http).StreamCompletionAsync(
                Req(stream: true))));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.MidStream, Is.True);
            Assert.That(ex.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(ex.Body, Does.Contain("Rate limit exceeded"));
        });
    }

    /// <summary>Covers RF-006: 429 HTTP na resposta inicial sobe como
    /// ProviderHttpException (não-MidStream) no path buffered.</summary>
    [Test]
    public async Task Http429_PropagaProviderHttpException()
    {
        var http = new StubHttpHandler();
        http.Enqueue(HttpStatusCode.TooManyRequests,
            "{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\"}}");
        await SetProvidersAsync(GoogleConn());

        var ex = await Assert.ThrowsAsync<ProviderHttpException>(async () =>
            await NewService(http).CompleteAsync(Req()));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(ex.MidStream, Is.False);
        });
    }
}
