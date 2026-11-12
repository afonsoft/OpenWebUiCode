// OpenWebUI.Acp — host JSON-RPC del protocolo ACP: conexión HTTP al
// API, helpers de respuesta sobre stdout y handlers de session/new +
// session/prompt. Extraído de Program.cs (S3776 file-level).
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenWebUI.Acp;

/// <summary>Host ACP: encapsula el HttpClient al API de OpenWebUI, la
/// serialización de respuestas JSON-RPC con gate de stdout y los
/// handlers de los métodos de sesión.</summary>
internal sealed class AcpHost
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly System.Threading.Lock _outGate = new();

    /// <summary>Token Bearer exigido a los clientes (OPENWEBUI_ACP_TOKEN).</summary>
    public string? Token { get; }

    public AcpHost(string baseUrl, string? token)
    {
        Token = token;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>Escribe una línea JSON-RPC en stdout (serializado).</summary>
    public void Send(object msg)
    {
        lock (_outGate)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(msg, _jsonOptions));
            Console.Out.Flush();
        }
    }

    public void Reply(JsonElement? id, object result) =>
        Send(new { jsonrpc = "2.0", id, result });

    public void ReplyError(JsonElement? id, int code, string message) =>
        Send(new { jsonrpc = "2.0", id, error = new { code, message } });

    /// <summary>RPC al API interno (POST /api/v1/rpc). Lanza
    /// InvalidOperationException si el API devuelve error.</summary>
    public async Task<JsonElement?> RpcAsync(
        string method, object? parms, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("/api/v1/rpc",
            new { jsonrpc = "2.0", id = 1, method, @params = parms },
            _jsonOptions, ct);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(
            _jsonOptions, ct);
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

    /// <summary>Detalle del dominio via data.detail cuando presente.</summary>
    private static string Detail(JsonElement? resultOrError) =>
        resultOrError is { ValueKind: JsonValueKind.Object } el
        && el.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";

    /// <summary>session/new — crea chat via chats.create.</summary>
    public async Task NewSessionAsync(JsonElement? id)
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

    /// <summary>session/prompt — dispara run y hace relay del stream
    /// SSE como notificaciones session/update.</summary>
    public async Task PromptAsync(JsonElement? id, JsonElement? parms)
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
            _http, Send, RpcAsync, sessionId, runId, CancellationToken.None);
        Reply(id, new { stopReason = stop });
    }
}
