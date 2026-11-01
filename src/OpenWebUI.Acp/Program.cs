// OpenWebUI.Acp — host ACP (Agent Client Protocol) sobre stdio
// (SPEC-20261021-harness-seams RF-C3). JSON-RPC 2.0 newline-delimited:
//   initialize      → exige OPENWEBUI_ACP_TOKEN; devolve capabilities
//   session/new     → cria chat via POST /api/v1/rpc (chats.create)
//   session/prompt  → dispara run (runs.start) e faz relay do stream SSE
//                     da run como notificações session/update; responde
//                     com stopReason=end_turn ao terminal.
// Env: OPENWEBUI_ACP_TOKEN (obrigatório) · OPENWEBUI_URL (default
// http://localhost:8080).
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var baseUrl = (Environment.GetEnvironmentVariable("OPENWEBUI_URL")
    ?? "http://localhost:8080").TrimEnd('/');
var token = Environment.GetEnvironmentVariable("OPENWEBUI_ACP_TOKEN");
const string EndTurnReason = "end_turn";
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
if (!string.IsNullOrEmpty(token))
{
    http.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", token);
}

var outGate = new object();
void Send(object msg)
{
    lock (outGate)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(msg, jsonOptions));
        Console.Out.Flush();
    }
}

void Reply(JsonElement? id, object result) =>
    Send(new { jsonrpc = "2.0", id, result });

void ReplyError(JsonElement? id, int code, string message) =>
    Send(new { jsonrpc = "2.0", id, error = new { code, message } });

async Task<JsonElement?> RpcAsync(
    string method, object? parms, CancellationToken ct)
{
    var resp = await http.PostAsJsonAsync("/api/v1/rpc",
        new { jsonrpc = "2.0", id = 1, method, @params = parms },
        jsonOptions, ct);
    var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(
        jsonOptions, ct);
    if (doc.ValueKind != JsonValueKind.Object)
    {
        return null;
    }
    if (doc.TryGetProperty("error", out var err))
    {
        var msg = err.TryGetProperty("message", out var m)
            ? m.GetString() : "rpc error";
        throw new InvalidOperationException(msg);
    }
    return doc.TryGetProperty("result", out var r) ? r.Clone() : null;
}

// Detalhe do domínio vaza via data.detail quando presente.
string Detail(JsonElement? resultOrError) =>
    resultOrError is { ValueKind: JsonValueKind.Object } el
    && el.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";

string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonDocument req;
    try
    {
        req = JsonDocument.Parse(line);
    }
    catch (JsonException)
    {
        Send(new { jsonrpc = "2.0", id = (object?)null,
            error = new { code = -32700, message = "Parse error" } });
        continue;
    }

    using (req)
    {
        var root = req.RootElement;
        JsonElement? id = root.TryGetProperty("id", out var idEl)
            ? idEl.Clone() : null;
        var method = root.TryGetProperty("method", out var m)
            && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var parms = root.TryGetProperty("params", out var p)
            && p.ValueKind == JsonValueKind.Object ? p.Clone() : (JsonElement?)null;

        try
        {
            switch (method)
            {
                case "initialize":
                    if (string.IsNullOrEmpty(token))
                    {
                        ReplyError(id, -32001,
                            "unauthorized: OPENWEBUI_ACP_TOKEN ausente no ambiente");
                        break;
                    }
                    Reply(id, new
                    {
                        protocolVersion = 1,
                        agentCapabilities = new
                        {
                            loadSession = false,
                            promptCapabilities = new { image = false, audio = false },
                        },
                        agentInfo = new { name = "open-webui", version = "1.0" },
                    });
                    break;

                case "session/new":
                    {
                        var created = await RpcAsync("chats.create",
                            new { title = "ACP session" }, CancellationToken.None);
                        var sessionId = created is { } c
                            && c.TryGetProperty("chatId", out var cid)
                            ? cid.GetString() : null;
                        if (sessionId is null)
                        {
                            ReplyError(id, -32603, "falha ao criar sessão");
                            break;
                        }
                        Reply(id, new { sessionId });
                        break;
                    }

                case "session/prompt":
                    {
                        var sessionId = parms is { } p1
                            && p1.TryGetProperty("sessionId", out var s)
                            ? s.GetString() : null;
                        var prompt = ExtractPrompt(parms);
                        if (sessionId is null || prompt is null)
                        {
                            ReplyError(id, -32602,
                                "params: sessionId e prompt são obrigatórios");
                            break;
                        }
                        // Modelo vem do chat; RPC exige params.model — usa
                        // o primeiro modelo do chat via runs.start é
                        // simplificado: o cliente ACP pode passar
                        // params.model opcional.
                        var model = parms is { } p2
                            && p2.TryGetProperty("model", out var mm)
                            && mm.ValueKind == JsonValueKind.String
                            ? mm.GetString() : "default";
                        var started = await RpcAsync("runs.start",
                            new { chatId = sessionId, prompt, model },
                            CancellationToken.None);
                        var runId = started is { } sr
                            && sr.TryGetProperty("runId", out var rid)
                            ? rid.GetString() : null;
                        if (runId is null)
                        {
                            ReplyError(id, -32603,
                                $"run não iniciada{Detail(started)}");
                            break;
                        }
                        var stop = await RelayRunAsync(
                            sessionId, runId, CancellationToken.None);
                        Reply(id, new { stopReason = stop });
                        break;
                    }

                case "session/cancel":
                case "cancel":
                    Reply(id, new { });
                    break;

                default:
                    if (id is not null)
                    {
                        ReplyError(id, -32601, $"Method not found: {method}");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            ReplyError(id, -32603, ex.Message);
        }
    }
}

static string? ExtractPrompt(JsonElement? parms)
{
    if (parms is not { } p
        || !p.TryGetProperty("prompt", out var arr)
        || arr.ValueKind != JsonValueKind.Array)
    {
        return null;
    }
    var parts = new List<string>();
    foreach (var block in arr.EnumerateArray())
    {
        if (block.TryGetProperty("type", out var t)
            && t.GetString() == "text"
            && block.TryGetProperty("text", out var txt))
        {
            parts.Add(txt.GetString() ?? "");
        }
    }
    return parts.Count == 0 ? null : string.Join("\n", parts);
}

// Relê o stream SSE da run e reemite deltas como session/update.
async Task<string> RelayRunAsync(
    string sessionId, string runId, CancellationToken ct)
{
    try
    {
        using var resp = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get,
                $"/api/v1/chats/{sessionId}/runs/{runId}/stream"),
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? sline;
        while ((sline = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!sline.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            var data = sline[5..].Trim();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }
            try
            {
                var evt = JsonDocument.Parse(data);
                var type = evt.RootElement.TryGetProperty("type", out var t)
                    ? t.GetString() : null;
                if (type is "content.delta" or "content_delta"
                    && evt.RootElement.TryGetProperty("delta", out var d))
                {
                    Send(new
                    {
                        jsonrpc = "2.0",
                        method = "session/update",
                        @params = new
                        {
                            sessionId,
                            update = new
                            {
                                sessionUpdate = "agent_message_chunk",
                                content = new
                                {
                                    type = "text",
                                    text = d.GetString() ?? "",
                                },
                            },
                        },
                    });
                }
                else if (type is "run.completed" or "completed")
                {
                    return EndTurnReason;
                }
                else if (type is "run.failed" or "failed")
                {
                    return "error";
                }
                else if (type is "run.stopped" or "stopped")
                {
                    return "cancelled";
                }
            }
            catch (JsonException)
            {
                // linha não-JSON — ignora
            }
        }
        return EndTurnReason;
    }
    catch (Exception)
    {
        // Fallback: polling do status até terminal.
        for (var i = 0; i < 720; i++)
        {
            await Task.Delay(500, ct);
            try
            {
                var r = await RpcAsync("runs.get",
                    new { runId }, ct);
                var status = r is { } el
                    && el.TryGetProperty("status", out var s)
                    ? s.GetString() : null;
                if (status is "completed") return EndTurnReason;
                if (status is "failed") return "error";
                if (status is "stopped" or "interrupted") return "cancelled";
            }
            catch (Exception)
            {
                // tenta de novo no próximo tick
            }
        }
        return EndTurnReason;
    }
}
