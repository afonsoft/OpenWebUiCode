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
using OpenWebUI.Acp;

var baseUrl = (Environment.GetEnvironmentVariable("OPENWEBUI_URL")
    ?? "http://localhost:8080").TrimEnd('/');
var token = Environment.GetEnvironmentVariable("OPENWEBUI_ACP_TOKEN");
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

async Task NewSessionAsync(JsonElement? id)
{
    var created = await RpcAsync("chats.create",
        new { title = "ACP session" }, CancellationToken.None);
    var sessionId = created is { } c
        && c.TryGetProperty("chatId", out var cid)
        ? cid.GetString() : null;
    if (sessionId is null)
    {
        ReplyError(id, -32603, "falha ao criar sessão");
        return;
    }
    Reply(id, new { sessionId });
}

async Task PromptAsync(JsonElement? id, JsonElement? parms)
{
    var (sessionId, prompt, model) = AcpDispatch.ExtractPromptParams(parms);
    if (sessionId is null || prompt is null)
    {
        ReplyError(id, -32602,
            "params: sessionId e prompt são obrigatórios");
        return;
    }
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
        return;
    }
    var stop = await AcpDispatch.RelayRunAsync(
        http, Send, RpcAsync, sessionId, runId, CancellationToken.None);
    Reply(id, new { stopReason = stop });
}

await AcpDispatch.RunStdinLoopAsync(
    Send,
    root => AcpDispatch.HandleAsync(
        root, token, Reply, ReplyError, NewSessionAsync, PromptAsync));
