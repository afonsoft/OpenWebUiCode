// Dispatch de um frame JSON-RPC do ACP (extraído do loop de Program
// para reduzir a complexidade cognitiva — S3776).
using System.Text.Json;

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
}
