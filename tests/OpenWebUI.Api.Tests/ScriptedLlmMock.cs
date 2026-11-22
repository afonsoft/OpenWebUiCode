using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Mock de provider programável (SPEC-20261122-programmable-provider):
/// HttpListener respondendo Ollama (<c>/api/chat</c>, <c>/api/tags</c>)
/// e OpenAI (<c>/chat/completions</c>, <c>/v1/models</c>). Cada request
/// consulta a lista de handlers registrada pelo teste — o primeiro cujo
/// predicado casa com o body responde; sem match, o
/// <see cref="Fallback"/> responde (default: completion de texto).
/// Body NDJSON de objeto único serve stream e non-stream no Ollama.
/// </summary>
public sealed class ScriptedLlmMock : IDisposable
{
    /// <summary>Resposta HTTP roteável — status, body e delay opcional.</summary>
    public sealed record MockResponse(int Status, string Body, int DelayMs = 0);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<(Func<string, bool> When, MockResponse Response)> _handlers = new();
    private readonly string _model;

    /// <summary>Bodies recebidos (inspeção de prompts/requests).</summary>
    public ConcurrentBag<string> Bodies { get; } = new();

    /// <summary>Resposta quando nenhum handler casa — substituível.</summary>
    public MockResponse Fallback { get; set; }

    /// <summary>URL base do mock (registrar como conexão Ollama/OpenAI).</summary>
    public string BaseUrl { get; }

    public ScriptedLlmMock(string model = "fake:1")
    {
        _model = model;
        var port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        BaseUrl = $"http://localhost:{port}";
        Fallback = Text("resposta final");
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Registra um handler: responde <paramref name="response"/>
    /// quando o predicado casa com o body da request.</summary>
    public void On(Func<string, bool> when, MockResponse response) =>
        _handlers.Add((when, response));

    /// <summary>Atalho: casa quando o body contém <paramref name="marker"/>.</summary>
    public void OnContains(string marker, MockResponse response) =>
        On(b => b.Contains(marker, StringComparison.Ordinal), response);

    /// <summary>Completion de texto (Ollama NDJSON de objeto único —
    /// válido para stream e non-stream).</summary>
    public MockResponse Text(string content) => new(200,
        $"{{\"model\":\"{_model}\",\"created_at\":\"2026-01-01T00:00:00Z\"," +
        $"\"message\":{{\"role\":\"assistant\",\"content\":{Json(content)}}}," +
        "\"done\":true}");

    /// <summary>Completion com tool_call — o executor executa a builtin
    /// e volta ao provider com o resultado no round seguinte.</summary>
    public MockResponse ToolCall(string name, string argsJson) => new(200,
        $"{{\"model\":\"{_model}\",\"created_at\":\"2026-01-01T00:00:00Z\"," +
        "\"message\":{\"role\":\"assistant\",\"content\":\"\"," +
        $"\"tool_calls\":[{{\"id\":\"call-1\",\"function\":{{\"name\":{Json(name)}," +
        $"\"arguments\":{argsJson}}}}}]}}," +
        "\"done\":true}");

    /// <summary>Falha HTTP — vira ProviderHttpException (a cadeia de
    /// modelos avança).</summary>
    public static MockResponse Fail(int status, string body = "{}") =>
        new(status, body);

    /// <summary>Aviso de billing forte estilo Pollinations — fake-200
    /// com usage positivo; o executor converte em 402 e a cadeia avança.</summary>
    public MockResponse BillingNotice() => Text(
        "The account behind this API key doesn't have enough credits. "
        + "Please top up or complete a quest, then try again.");

    private static string Json(string s) =>
        System.Text.Json.JsonSerializer.Serialize(s);

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(
            IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            try
            {
                var body = string.Empty;
                if (ctx.Request.HasEntityBody)
                {
                    using var reader = new StreamReader(ctx.Request.InputStream);
                    body = await reader.ReadToEndAsync(ct);
                }
                var path = ctx.Request.Url!.AbsolutePath;
                var response = Route(path, body);
                if (response.DelayMs > 0)
                {
                    await Task.Delay(response.DelayMs, ct);
                }
                var bytes = Encoding.UTF8.GetBytes(response.Body + "\n");
                ctx.Response.StatusCode = response.Status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            }
            catch (Exception)
            {
                // Request abortado pelo cliente — ignora e segue o loop.
            }
            finally
            {
                ctx.Response.Close();
            }
        }
    }

    private MockResponse Route(string path, string body)
    {
        if (path is "/api/tags")
        {
            return new(200,
                $"{{\"models\":[{{\"model\":{Json(_model)},\"name\":{Json(_model)}}}]}}");
        }
        if (path is "/v1/models" or "/models")
        {
            return new(200,
                $"{{\"data\":[{{\"id\":{Json(_model)}}}]}}");
        }
        if (path is "/api/chat" or "/chat/completions")
        {
            Bodies.Add(body);
            var hit = _handlers.FirstOrDefault(h => h.When(body));
            var r = hit.Response ?? Fallback;
            // OpenAI quer envelope chat.completion, não Ollama.
            if (path == "/chat/completions" && r.Status == 200
                && !r.Body.Contains("\"choices\""))
            {
                return WrapOpenAi(r.Body);
            }
            return r;
        }
        return new(404, "{}");
    }

    /// <summary>Converte a resposta Ollama (message/tool_calls) no
    /// envelope <c>choices[0].message</c> da OpenAI.</summary>
    private MockResponse WrapOpenAi(string ollamaBody)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(ollamaBody);
            var msg = doc.RootElement.GetProperty("message");
            var msgJson = msg.GetRawText();
            return new(200,
                "{\"choices\":[{\"message\":" + msgJson
                + ",\"finish_reason\":\"stop\"}],"
                + "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5}}");
        }
        catch (Exception)
        {
            return new(200, ollamaBody);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }
}
