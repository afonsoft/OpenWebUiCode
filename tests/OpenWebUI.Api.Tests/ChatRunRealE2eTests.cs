using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes E2E de <b>runs</b> com provider LLM real (OpenAI-compatível, ex.:
/// OmniRoute). Cobrem o caminho completo <c>POST /chats/{id}/messages</c> →
/// dispatcher → <c>ChatRunExecutor</c> → tool loop → SSE de run — incluindo
/// tool calls de verdade, reasoning/thinking e o markup DSML do DeepSeek.
/// Só rodam quando <c>E2E_CHAT=1</c> + <c>E2E_LLM_BASE_URL</c> /
/// <c>E2E_LLM_API_KEY</c> / <c>E2E_LLM_MODEL</c> (gate do
/// <c>chat-e2e.yml</c>). Cada teste despeja o transcript SSE completo e os
/// logs do app no output — visíveis no job do Actions.
/// </summary>
[TestFixture, IsolateEnvironment]
[Category("e2e-chat")]
public class ChatRunRealE2eTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private AppLogCapture _logs = null!;
    private string _dbPath = null!;
    private string _model = null!;
    private string? _savedBaseUrl;
    private string? _savedApiKey;
    private string? _savedBaseUrls;
    private string? _savedApiKeys;
    private string? _savedConnStr;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var baseUrl = Environment.GetEnvironmentVariable("E2E_LLM_BASE_URL");
        var apiKey = Environment.GetEnvironmentVariable("E2E_LLM_API_KEY");
        _model = Environment.GetEnvironmentVariable("E2E_LLM_MODEL") ?? string.Empty;
        if (Environment.GetEnvironmentVariable("E2E_CHAT") != "1"
            || string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(_model))
        {
            Assert.Ignore(
                "E2E de chat desligado — defina E2E_CHAT=1, E2E_LLM_BASE_URL, "
                + "E2E_LLM_API_KEY e E2E_LLM_MODEL para rodar contra o provider real.");
        }

        _savedBaseUrl = Environment.GetEnvironmentVariable("OPENAI_API_BASE_URL");
        _savedApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _savedBaseUrls = Environment.GetEnvironmentVariable("OPENAI_API_BASE_URLS");
        _savedApiKeys = Environment.GetEnvironmentVariable("OPENAI_API_KEYS");
        _savedConnStr = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-e2e-run-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("OPENAI_API_BASE_URLS", null);
        Environment.SetEnvironmentVariable("OPENAI_API_KEYS", null);
        Environment.SetEnvironmentVariable("OPENAI_API_BASE_URL", baseUrl);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", apiKey);
        // Trace estrutural do stream upstream ligado nos testes — o rollup por
        // round (chunks, finish_reason, dsmlMarker...) sai no log capturado.
        Environment.SetEnvironmentVariable("OPENWEBUI_LOG_UPSTREAM_STREAM", "1");

        _logs = new AppLogCapture();
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.ConfigureLogging(
                lb => lb.AddProvider(_logs)));
        _client = _factory.CreateClient();

        using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            try
            {
                using var probeRequest = new HttpRequestMessage(
                    HttpMethod.Get, $"{baseUrl!.TrimEnd('/')}/models");
                probeRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
                using var probeResponse = await probe.SendAsync(probeRequest);
                if (!probeResponse.IsSuccessStatusCode)
                {
                    Assert.Ignore(
                        $"provider real indisponível ({(int)probeResponse.StatusCode}) — e2e pulado");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Assert.Ignore($"provider real inalcançável ({ex.Message}) — e2e pulado");
            }
        }

        var signup = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("E2E", "e2e-run@test.local", "senha123"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await signup.Content.ReadAsStringAsync());
        var auth = (await signup.Content.ReadFromJsonAsync<AuthResponse>())!;
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
        _logs?.Dispose();
        Environment.SetEnvironmentVariable("OPENAI_API_BASE_URL", _savedBaseUrl);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", _savedApiKey);
        Environment.SetEnvironmentVariable("OPENAI_API_BASE_URLS", _savedBaseUrls);
        Environment.SetEnvironmentVariable("OPENAI_API_KEYS", _savedApiKeys);
        Environment.SetEnvironmentVariable("OPENWEBUI_LOG_UPSTREAM_STREAM", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", _savedConnStr);
        if (_dbPath is not null && File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    /// <summary>
    /// Caminho feliz do tool loop: prompt que obriga a tool call — a run deve
    /// emitir <c>tool_call</c> + <c>tool_result</c> no SSE e fechar com
    /// conteúdo visível. Regressão do stall pós-thinking: reasoning sozinho
    /// não pode encerrar a run vazia.
    /// </summary>
    [Test]
    [CancelAfter(360_000)]
    public async Task Run_ProviderReal_ToolLoop_ExecutaToolEResponde()
    {
        var chat = await CreateChatAsync();
        await SetApprovalAsync(chat.Id, "always-allow");

        var run = await EnqueueAsync(chat.Id,
            "OBRIGATÓRIO: chame agora a ferramenta builtin_todo_write com um "
            + "plano de 2 tarefas (t1: ler README, t2: resumir). "
            + "Depois responda em uma frase o que fez.",
            ["builtin:todo_write", "builtin:file_list"]);

        var t = await ConsumeRunStreamAsync(chat.Id, run.Id);
        DumpTranscript(t);

        // O OmniRoute pode rotear "DeepSeek" para backends sem function
        // calling nativo (ex.: r1-distill no Cloudflare) — nesses o modelo
        // "simula" a tool call escrevendo o nome + JSON no texto. O contrato
        // do app é nunca morrer vazio; tool_call estruturado só é exigível
        // quando o backend emite (e então tool_result precisa voltar).
        var inlineCall = t.Content.ToString().Contains("builtin_",
            StringComparison.Ordinal);
        TestContext.Out.WriteLine(
            $"tool calls estruturadas={t.ToolCalls.Count}, "
            + $"pseudo tool call no texto={inlineCall}");

        Assert.Multiple(() =>
        {
            Assert.That(t.Done, Is.True, "stream da run não emitiu [DONE]");
            Assert.That(t.Error, Is.Null, $"run reportou erro: {t.Error}");
            Assert.That(t.ToolCalls.Count > 0 || t.Content.Length > 0, Is.True,
                "run encerrou sem tool calls e sem conteúdo — stall pós-thinking");
            if (t.ToolCalls.Count > 0)
            {
                Assert.That(t.ToolResults, Is.Not.Empty,
                    "tool_call sem tool_result — a execução não voltou");
            }
            else
            {
                Assert.That(inlineCall, Is.True,
                    "backend não emitiu tool_calls nem escreveu a tool no texto — "
                    + "o modelo ignorou o pedido de tool call");
            }
        });
    }

    /// <summary>
    /// Regressão do bug reportado: histórico contém markup DSML vazado de uma
    /// resposta antiga (como persistido antes do filtro) — a run não pode
    /// terminar com 0 chars e 0 tool calls ("trava depois do pensamento").
    /// </summary>
    [Test]
    [CancelAfter(360_000)]
    public async Task Run_ProviderReal_HistoricoComDsmlVazado_NaoEncerraVazio()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var dsmlLeak = "\n\n<｜DSML｜tool_calls>\n<｜DSML｜invoke name=\"builtin_file_read\">\n"
            + "<｜DSML｜parameter name=\"path\" string=\"true\">README.md</｜DSML｜parameter>\n"
            + "</｜DSML｜invoke>\n</｜DSML｜tool_calls>";
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/",
            new ChatUpsertRequest("E2E dsml", [_model],
            [
                new ChatMessageModel("u1", "user", "Leia o README do workspace", null, now),
                new ChatMessageModel("a1", "assistant", dsmlLeak, _model, now + 1),
            ]));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
        await SetApprovalAsync(chat.Id, "always-allow");

        var run = await EnqueueAsync(chat.Id, "continue",
            ["builtin:todo_write", "builtin:file_list", "builtin:file_read"]);

        var t = await ConsumeRunStreamAsync(chat.Id, run.Id);
        DumpTranscript(t);

        Assert.Multiple(() =>
        {
            Assert.That(t.Done, Is.True, "stream da run não emitiu [DONE]");
            Assert.That(t.Error, Is.Null, $"run reportou erro: {t.Error}");
            // O contrato do fix: a run não pode morrer em silêncio depois do
            // pensamento — tool call executada OU resposta de texto.
            Assert.That(t.ToolCalls.Count > 0 || t.Content.Length > 0, Is.True,
                "run encerrou sem tool calls e sem conteúdo — stall pós-thinking");
        });
        Assert.That(t.Content.ToString(), Does.Not.Contain("DSML"),
            "markup DSML não pode vazar no conteúdo final");
    }

    /// <summary>
    /// Thinking/reasoning streaming: DeepSeek via OmniRoute emite
    /// <c>reasoning_content</c> — a run deve seguir para a resposta (ou
    /// tool call) depois do pensamento, nunca congelar nele.
    /// </summary>
    [Test]
    [CancelAfter(360_000)]
    public async Task Run_ProviderReal_ReasoningDepoisResposta_NaoParaNoThinking()
    {
        var chat = await CreateChatAsync();
        await SetApprovalAsync(chat.Id, "always-allow");

        var run = await EnqueueAsync(chat.Id,
            "Pense passo a passo e responda em uma frase: quanto é 17 + 25?",
            ["builtin:todo_write"]);

        var t = await ConsumeRunStreamAsync(chat.Id, run.Id);
        DumpTranscript(t);

        Assert.Multiple(() =>
        {
            Assert.That(t.Done, Is.True, "stream da run não emitiu [DONE]");
            Assert.That(t.Error, Is.Null, $"run reportou erro: {t.Error}");
            Assert.That(t.Content.ToString(), Does.Contain("42"),
                "a resposta final deveria conter 42");
        });
    }

    /// <summary>
    /// Conversa multi-turno com tools: dois envios na mesma conversa — o
    /// segundo turno reusa o histórico persistido (incluindo tool_calls do
    /// primeiro) e ainda deve responder.
    /// </summary>
    [Test]
    [CancelAfter(480_000)]
    public async Task Run_ProviderReal_DoisTurnosComTools_ContinuaFluxo()
    {
        var chat = await CreateChatAsync();
        await SetApprovalAsync(chat.Id, "always-allow");

        var run1 = await EnqueueAsync(chat.Id,
            "Chame builtin_todo_write com o plano [t1: pesquisar], depois diga 'pronto'.",
            ["builtin:todo_write"]);
        var t1 = await ConsumeRunStreamAsync(chat.Id, run1.Id);
        DumpTranscript(t1, "turno 1");
        Assert.That(t1.Done, Is.True);
        Assert.That(t1.ToolCalls.Count > 0 || t1.Content.Length > 0, Is.True,
            "turno 1 morreu em silêncio");

        var run2 = await EnqueueAsync(chat.Id,
            "Agora marque a tarefa t1 como concluída via builtin_todo_write e confirme.",
            ["builtin:todo_write"]);
        var t2 = await ConsumeRunStreamAsync(chat.Id, run2.Id);
        DumpTranscript(t2, "turno 2");
        Assert.Multiple(() =>
        {
            Assert.That(t2.Done, Is.True);
            Assert.That(t2.Error, Is.Null, $"turno 2 reportou erro: {t2.Error}");
            Assert.That(t2.ToolCalls.Count > 0 || t2.Content.Length > 0, Is.True,
                "turno 2 morreu em silêncio");
        });
    }

    // ---------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------

    private async Task<ChatResponse> CreateChatAsync()
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/",
            new ChatUpsertRequest("E2E run", [_model], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    private async Task SetApprovalAsync(string chatId, string preset)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/v1/chats/{chatId}")
        {
            Content = JsonContent.Create(new ChatPatchRequest(preset)),
        };
        using var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await resp.Content.ReadAsStringAsync());
    }

    private async Task<ChatRunResponse> EnqueueAsync(
        string chatId, string content, IReadOnlyList<string> toolIds)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest(content, _model, ToolIds: toolIds));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<ChatRunResponse>())!;
    }

    /// <summary>Transcript acumulado do SSE de uma run.</summary>
    private sealed class RunTranscript
    {
        public StringBuilder Content { get; } = new();
        public StringBuilder Reasoning { get; } = new();
        public List<string> Events { get; } = [];
        public List<string> ToolCalls { get; } = [];
        public List<string> ToolResults { get; } = [];
        public List<string> Statuses { get; } = [];
        public bool Done { get; set; }
        public string? Error { get; set; }
        public int RawLines { get; set; }
    }

    /// <summary>
    /// Consome o SSE de <c>GET /chats/{id}/runs/{runId}/stream</c> até
    /// <c>[DONE]</c> ou o timeout da run. Cada evento vira linha do
    /// transcript (content/reasoning acumulados; tool_call/tool_result/
    /// status/error nomeados).
    /// </summary>
    private async Task<RunTranscript> ConsumeRunStreamAsync(
        string chatId, string runId)
    {
        var t = new RunTranscript();
        using var req = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/runs/{runId}/stream");
        using var resp = await _client.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        string? pendingEvent = null;
        var deadline = DateTime.UtcNow.AddMinutes(4);
        while (await reader.ReadLineAsync() is { } line)
        {
            t.RawLines++;
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                pendingEvent = line["event:".Length..].Trim();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                t.Done = true;
                break;
            }
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("run não terminou em 4 min — possível stall");
            }

            var label = pendingEvent ?? "data";
            pendingEvent = null;
            t.Events.Add($"{label}: {Truncate(payload, 160)}");

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(payload);
            }
            catch (JsonException)
            {
                continue;
            }
            switch (label)
            {
                case "tool_call":
                    t.ToolCalls.Add(node?["name"]?.GetValue<string>() ?? "?");
                    break;
                case "tool_result":
                    t.ToolResults.Add(
                        $"{node?["name"]?.GetValue<string>()} ok={node?["ok"]}");
                    break;
                case "status":
                    t.Statuses.Add(node?["phase"]?.GetValue<string>() ?? "?");
                    break;
                case "data":
                    var delta = node?["choices"] is JsonArray { Count: > 0 } c
                        ? c[0]?["delta"] : null;
                    if (delta?["content"]?.GetValue<string>() is { Length: > 0 } dc)
                    {
                        t.Content.Append(dc);
                    }
                    if (delta?["reasoning"]?.GetValue<string>() is { Length: > 0 } dr)
                    {
                        t.Reasoning.Append(dr);
                    }
                    if (node?["error"]?.GetValue<string>() is { } err)
                    {
                        t.Error = err;
                    }
                    break;
            }
        }
        return t;
    }

    /// <summary>Despeja o transcript completo + logs do app no output do
    /// teste — aparece no trx e no log do job do GitHub Actions.</summary>
    private void DumpTranscript(RunTranscript t, string tag = "")
    {
        var header = string.IsNullOrEmpty(tag) ? "run" : $"run ({tag})";
        TestContext.Out.WriteLine($"===== transcript {header} =====");
        foreach (var evt in t.Events)
        {
            TestContext.Out.WriteLine(evt);
        }
        TestContext.Out.WriteLine(
            $"--- resumo: lines={t.RawLines} done={t.Done} "
            + $"content={t.Content.Length} chars reasoning={t.Reasoning.Length} chars "
            + $"toolCalls=[{string.Join(", ", t.ToolCalls)}] "
            + $"toolResults={t.ToolResults.Count} status=[{string.Join(" → ", t.Statuses)}]");
        if (t.Reasoning.Length > 0)
        {
            TestContext.Out.WriteLine(
                $"--- reasoning: {Truncate(t.Reasoning.ToString(), 400)}");
        }
        if (t.Content.Length > 0)
        {
            TestContext.Out.WriteLine(
                $"--- content: {Truncate(t.Content.ToString(), 400)}");
        }
        var appLogs = _logs.Drain();
        if (appLogs.Count > 0)
        {
            TestContext.Out.WriteLine($"===== logs do app ({appLogs.Count}) =====");
            foreach (var l in appLogs)
            {
                TestContext.Out.WriteLine(l);
            }
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Captura logs da app (<c>OpenWebUI.*</c>) em memória para despejar no
    /// transcript do teste — em-processo via WebApplicationFactory.
    /// </summary>
    private sealed class AppLogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public ILogger CreateLogger(string categoryName) =>
            categoryName.StartsWith("OpenWebUI.", StringComparison.Ordinal)
                ? new CategoryLogger(_lines, categoryName)
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }

        public List<string> Drain()
        {
            var list = new List<string>();
            while (_lines.TryDequeue(out var l))
            {
                list.Add(l);
            }
            return list;
        }

        private sealed class CategoryLogger(
            ConcurrentQueue<string> lines, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }
                var msg = formatter(state, exception);
                if (msg.Length > 0)
                {
                    var shortCat = category["OpenWebUI.".Length..];
                    lines.Enqueue($"{logLevel} {shortCat}: {Truncate(msg, 300)}");
                }
            }
        }
    }
}
