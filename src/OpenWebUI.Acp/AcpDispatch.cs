// Dispatch de um frame JSON-RPC do ACP (extraído do loop de Program
// para reduzir a complexidade cognitiva — S3776).
using System.Text.Json;
using System.Net.Http.Json;

namespace OpenWebUI.Acp;

internal static class AcpDispatch
{
    /// <summary>Despacha um request já parseado: initialize /
    /// session/new / session/prompt / cancel; desconhecido → -32601
    /// (só quando tem id), erro de handler → -32603.</summary>
    internal static async Task HandleAsync(
        JsonElement root, string? token,
        Action<JsonElement?, object> reply,
        Action<JsonElement?, int, string> replyError,
        Func<JsonElement?, Task> newSessionAsync,
        Func<JsonElement?, JsonElement?, Task> promptAsync)
    {
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
                    HandleInitialize(id, token, reply, replyError);
                    break;
                case "session/new":
                    await newSessionAsync(id);
                    break;
                case "session/prompt":
                    await promptAsync(id, parms);
                    break;
                case "session/cancel":
                case "cancel":
                    reply(id, new { });
                    break;
                default:
                    if (id is not null)
                    {
                        replyError(id, -32601, $"Method not found: {method}");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            replyError(id, -32603, ex.Message);
        }
    }

    private static void HandleInitialize(
        JsonElement? id, string? token,
        Action<JsonElement?, object> reply,
        Action<JsonElement?, int, string> replyError)
    {
        if (string.IsNullOrEmpty(token))
        {
            replyError(id, -32001,
                "unauthorized: OPENWEBUI_ACP_TOKEN ausente no ambiente");
            return;
        }
        reply(id, new
        {
            protocolVersion = 1,
            agentCapabilities = new
            {
                loadSession = false,
                promptCapabilities = new { image = false, audio = false },
            },
            agentInfo = new { name = "open-webui", version = "1.0" },
        });
    }
internal static string? ExtractPrompt(JsonElement? parms)
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
internal static async Task<string> RelayRunAsync(
    HttpClient http, Action<object> send,
    Func<string, object?, CancellationToken, Task<JsonElement?>> rpcAsync,
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
                    send(new
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
                    return "end_turn";
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
        return "end_turn";
    }
    catch (Exception)
    {
        // Fallback: polling do status até terminal.
        for (var i = 0; i < 720; i++)
        {
            await Task.Delay(500, ct);
            try
            {
                var r = await rpcAsync("runs.get",
                    new { runId }, ct);
                var status = r is { } el
                    && el.TryGetProperty("status", out var s)
                    ? s.GetString() : null;
                if (status is "completed") return "end_turn";
                if (status is "failed") return "error";
                if (status is "stopped" or "interrupted") return "cancelled";
            }
            catch (Exception)
            {
                // tenta de novo no próximo tick
            }
        }
        return "end_turn";
    }
}

}
