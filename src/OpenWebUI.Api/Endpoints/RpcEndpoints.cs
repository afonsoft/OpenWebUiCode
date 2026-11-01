using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Endpoint JSON-RPC 2.0 estável (SPEC-20261021-harness-seams RF-C2):
/// <c>POST /api/v1/rpc</c> autenticado, sem batch. Métodos:
/// <c>ping</c>, <c>chats.list</c>, <c>chats.create</c>, <c>runs.start</c>,
/// <c>runs.get</c>. Erros de protocolo usam os códigos JSON-RPC padrão;
/// erros de domínio usam <c>-32000</c> com <c>data.detail</c>.
/// </summary>
public static class RpcEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;
    private const int DomainError = -32000;

    /// <summary>Mapeia <c>POST /api/v1/rpc</c>.</summary>
    public static IEndpointRouteBuilder MapRpcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/rpc", HandleAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http, [AsParameters] ChatRunEndpoints.EnqueueRunDeps deps,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, deps.Db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(http.Request.Body,
                cancellationToken: ct);
        }
        catch (JsonException)
        {
            return RpcError(null, ParseError, "Parse error");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("method", out var methodEl)
                || methodEl.ValueKind != JsonValueKind.String)
            {
                return RpcError(null, InvalidRequest, "Invalid Request");
            }

            JsonElement? id = root.TryGetProperty("id", out var idEl)
                ? idEl.Clone()
                : null;
            var method = methodEl.GetString()!;
            var parms = root.TryGetProperty("params", out var p)
                && p.ValueKind == JsonValueKind.Object
                ? p.Clone()
                : (JsonElement?)null;

            try
            {
                return method switch
                {
                    "ping" => RpcResult(id, "pong"),
                    "chats.list" => await ChatsListAsync(deps.Db, user, parms, id, ct),
                    "chats.create" => await ChatsCreateAsync(
                        deps.Db, user, parms, id, ct),
                    "runs.start" => await RunsStartAsync(http, deps, user, parms, id, ct),
                    "runs.get" => await RunsGetAsync(deps.Db, user, parms, id, ct),
                    _ => RpcError(id, MethodNotFound,
                        $"Method not found: {method}"),
                };
            }
            catch (RpcParamsException ex)
            {
                return RpcError(id, InvalidParams, ex.Message);
            }
            catch (Exception)
            {
                return RpcError(id, InternalError, "Internal error");
            }
        }
    }

    private static async Task<IResult> ChatsListAsync(
        AppDbContext db, User user, JsonElement? p, JsonElement? id,
        CancellationToken ct)
    {
        var limit = p is { } el && el.TryGetProperty("limit", out var l)
            && l.ValueKind == JsonValueKind.Number
            ? Math.Clamp(l.GetInt32(), 1, 200)
            : 50;
        var chats = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(limit)
            .Select(c => new { id = c.Id, title = c.Title, updatedAt = c.UpdatedAt })
            .ToListAsync(ct);
        return RpcResult(id, chats);
    }

    private static async Task<IResult> ChatsCreateAsync(
        AppDbContext db, User user, JsonElement? p, JsonElement? id,
        CancellationToken ct)
    {
        var title = Str(p, "title") ?? "New Chat";
        var model = Str(p, "model");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var chat = new Chat
        {
            UserId = user.Id,
            Title = title,
            ModelsJson = JsonSerializer.Serialize(
                model is null ? Array.Empty<string>() : new[] { model },
                JsonOptions),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Chats.Add(chat);
        await db.SaveChangesAsync(ct);
        return RpcResult(id, new { chatId = chat.Id });
    }

    private static async Task<IResult> RunsStartAsync(
        HttpContext http, ChatRunEndpoints.EnqueueRunDeps deps,
        User user, JsonElement? p, JsonElement? id, CancellationToken ct)
    {
        var chatId = Str(p, "chatId")
            ?? throw new RpcParamsException("params.chatId é obrigatório");
        var prompt = Str(p, "prompt")
            ?? throw new RpcParamsException("params.prompt é obrigatório");
        var model = Str(p, "model")
            ?? throw new RpcParamsException("params.model é obrigatório");

        var request = new EnqueueChatRunRequest(prompt, model);
        var result = await ChatRunEndpoints.EnqueueMessageAsync(
            chatId, request, http, deps, ct);

        // Converte o IResult HTTP em valor JSON-RPC: 2xx → result com o
        // corpo; erro → -32000 com data.detail.
        if (result is IValueHttpResult<ChatRunResponse> ok && ok.Value is { } run)
        {
            return RpcResult(id, new
            {
                runId = run.Id,
                chatId = run.ChatId,
                status = run.Status,
            });
        }
        if (result is IValueHttpResult err && err.Value is not null)
        {
            var detail = JsonSerializer.SerializeToElement(
                err.Value, JsonOptions);
            var msg = detail.ValueKind == JsonValueKind.Object
                && detail.TryGetProperty("detail", out var d)
                ? d.GetString() ?? "erro"
                : "erro";
            return RpcError(id, DomainError, msg,
                detail.ValueKind == JsonValueKind.Object ? detail : null);
        }
        if (result is IStatusCodeHttpResult sc && sc.StatusCode is { } code
            && code >= 400)
        {
            return RpcError(id, DomainError,
                $"enqueue falhou com HTTP {code}");
        }
        return RpcResult(id, new { status = "queued" });
    }

    private static async Task<IResult> RunsGetAsync(
        AppDbContext db, User user, JsonElement? p, JsonElement? id,
        CancellationToken ct)
    {
        var runId = Str(p, "runId")
            ?? throw new RpcParamsException("params.runId é obrigatório");
        var run = await db.ChatRuns.AsNoTracking()
            .Where(r => r.Id == runId && r.UserId == user.Id)
            .Select(r => new
            {
                runId = r.Id,
                chatId = r.ChatId,
                status = r.Status,
                model = r.Model,
                createdAt = r.CreatedAt,
                startedAt = r.StartedAt,
                completedAt = r.CompletedAt,
            })
            .FirstOrDefaultAsync(ct);
        return run is null
            ? RpcError(id, DomainError, "run não encontrada")
            : RpcResult(id, run);
    }

    private static string? Str(JsonElement? p, string name) =>
        p is { } el && el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static IResult RpcResult(JsonElement? id, object result) =>
        Results.Json(new
        {
            jsonrpc = "2.0",
            id = id ?? (object?)null,
            result,
        });

    private static IResult RpcError(
        JsonElement? id, int code, string message, JsonElement? data = null) =>
        Results.Json(new
        {
            jsonrpc = "2.0",
            id = id ?? (object?)null,
            error = new
            {
                code,
                message,
                data = data ?? (object?)null,
            },
        });

    /// <summary>Erro de validação de params → -32602.</summary>
    public sealed class RpcParamsException(string message)
        : Exception(message);
}
