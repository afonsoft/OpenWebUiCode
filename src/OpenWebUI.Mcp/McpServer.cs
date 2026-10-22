using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenWebUI.Mcp;

/// <summary>
/// Dispatch do protocolo MCP sobre stdio (JSON-RPC 2.0, newline-delimited):
/// <c>initialize</c>, <c>ping</c>, <c>tools/list</c>, <c>tools/call</c>.
/// Tools expõem chats/runs/teams do OpenWebUI via
/// <see cref="OwuiApiClient"/> — agentes externos (Claude Code, OpenCode,
/// outro Devin) dirigem o produto sem UI.
/// </summary>
public sealed class McpServer
{
    /// <summary>Versão do protocolo MCP anunciada no handshake.</summary>
    public const string ProtocolVersion = "2024-11-05";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Processa uma linha de request JSON-RPC e devolve a resposta
    /// serializada — null para notifications (sem resposta no protocolo).
    /// </summary>
    public async Task<string?> HandleLineAsync(
        string line, OwuiApiClient api, CancellationToken ct)
    {
        JsonNode? id;
        string? method;
        JsonNode? @params;
        try
        {
            var request = JsonNode.Parse(line);
            id = request?["id"]?.DeepClone();
            method = request?["method"]?.GetValue<string>();
            @params = request?["params"];
        }
        catch (JsonException)
        {
            return Serialize(Error(null, -32700, "Parse error"));
        }

        if (method is null)
        {
            return Serialize(Error(id, -32600, "Invalid request"));
        }

        // Notifications: sem id → sem resposta (spec MCP).
        if (id is null)
        {
            return null;
        }

        var result = method switch
        {
            "initialize" => Ok(id, InitializeResult()),
            "ping" => Ok(id, new JsonObject()),
            "tools/list" => Ok(id, ToolsList()),
            "tools/call" => await ToolsCallAsync(id, @params, api, ct),
            _ => Error(id, -32601, $"Method not found: {method}"),
        };
        return result;
    }

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject(),
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "openwebui-mcp",
            ["version"] = "0.1.0",
        },
    };

    private static JsonObject ToolsList() => new()
    {
        ["tools"] = new JsonArray(
            Tool("chats.list",
                "List the user's chats (id, title, updatedAt).",
                Schema(new JsonObject
                {
                    ["limit"] = Prop("integer", "Max chats (default 50)."),
                })),
            Tool("chats.create",
                "Create a new empty chat and return it.",
                Schema(new JsonObject
                {
                    ["title"] = Prop("string", "Chat title."),
                    ["model"] = Prop("string", "Model id (required by the app)."),
                }, required: ["title", "model"])),
            Tool("runs.start",
                "Enqueue a user message as a background run on a chat.",
                Schema(new JsonObject
                {
                    ["chatId"] = Prop("string", "Target chat id."),
                    ["content"] = Prop("string", "User message content."),
                    ["model"] = Prop("string", "Model id."),
                }, required: ["chatId", "content", "model"])),
            Tool("runs.status",
                "Get the status of a run (queued|running|stopped|completed|failed).",
                Schema(new JsonObject
                {
                    ["chatId"] = Prop("string", "Chat id."),
                    ["runId"] = Prop("string", "Run id."),
                }, required: ["chatId", "runId"])),
            Tool("runs.stop",
                "Request cancellation of a running run.",
                Schema(new JsonObject
                {
                    ["chatId"] = Prop("string", "Chat id."),
                    ["runId"] = Prop("string", "Run id."),
                }, required: ["chatId", "runId"])),
            Tool("teams.status",
                "Roster, roles, active runs and shared tasks of an agent team.",
                Schema(new JsonObject
                {
                    ["chatId"] = Prop("string", "Any member chat id."),
                }, required: ["chatId"])),
            Tool("attention.list",
                "Attention inbox: pending approvals, finished/failed runs and "
                + "closed terminals waiting to be seen.",
                Schema(new JsonObject()))),
    };

    private static JsonObject Tool(
        string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    private static JsonObject Schema(
        JsonObject properties, string[]? required = null) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = required is null
            ? new JsonArray()
            : new JsonArray([.. required.Select(r => (JsonNode)r)]),
    };

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private async Task<string> ToolsCallAsync(
        JsonNode? id, JsonNode? @params, OwuiApiClient api, CancellationToken ct)
    {
        var name = @params?["name"]?.GetValue<string>();
        var args = @params?["arguments"];
        if (name is null)
        {
            return Serialize(Error(id, -32602, "tools/call sem 'name'."));
        }

        if (!api.HasCredentials)
        {
            return Serialize(Ok(id, CallError(
                "OPENWEBUI_API_KEY ausente — gere uma chave sk-* no app.")));
        }

        var (body, error) = name switch
        {
            "chats.list" => await api.GetAsync(
                "api/v1/chats/", ct),
            "chats.create" => await api.PostAsync(
                "api/v1/chats/", new
                {
                    title = Str(args, "title") ?? "MCP chat",
                    models = new[] { Str(args, "model") ?? "" },
                    messages = Array.Empty<object>(),
                }, ct),
            "runs.start" => await api.PostAsync(
                $"api/v1/chats/{Uri.EscapeDataString(Req(args, "chatId"))}/messages",
                new
                {
                    content = Str(args, "content"),
                    model = Str(args, "model"),
                }, ct),
            "runs.status" => await api.GetAsync(
                $"api/v1/chats/{Uri.EscapeDataString(Req(args, "chatId"))}/runs/" +
                $"{Uri.EscapeDataString(Req(args, "runId"))}", ct),
            "runs.stop" => await api.PostAsync(
                $"api/v1/chats/{Uri.EscapeDataString(Req(args, "chatId"))}/runs/" +
                $"{Uri.EscapeDataString(Req(args, "runId"))}/stop", null, ct),
            "teams.status" => await api.GetAsync(
                $"api/v1/chats/{Uri.EscapeDataString(Req(args, "chatId"))}/team", ct),
            "attention.list" => await api.GetAsync("api/v1/attention", ct),
            _ => (null, $"tool desconhecida: {name}"),
        };

        return Serialize(error is not null
            ? Ok(id, CallError(error))
            : Ok(id, CallText(body ?? "null")));
    }

    private static string? Str(JsonNode? args, string key) =>
        args?[key]?.GetValue<string>();

    /// <summary>Arg obrigatório — devolve marcador inválido quando ausente
    /// (a API responde 404/400 e o texto do erro vai na resposta).</summary>
    private static string Req(JsonNode? args, string key) =>
        Str(args, key) ?? $"__missing_{key}__";

    private static JsonObject CallText(string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = text,
        }),
    };

    private static JsonObject CallError(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = message,
        }),
        ["isError"] = true,
    };

    private static string Ok(JsonNode? id, JsonObject result) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result,
        }.ToJsonString(Json);

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        }.ToJsonString(Json);

    private static string Serialize(string json) => json;
}
