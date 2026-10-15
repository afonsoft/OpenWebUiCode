using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Webhooks de automação (SPEC-20261007-chat-agent-parity RF-020/RF-021):
/// <c>POST /api/v1/hooks/{token}</c> — rota anônima e rate-limited —
/// transforma o corpo da chamada numa mensagem de usuário no chat
/// vinculado e enfileira uma run desacoplada (o resultado chega ao chat
/// e às notificações existentes). n8n usa o alias
/// <c>POST /api/v1/hooks/n8n/{token}</c>. CRUD dos hooks é autenticado;
/// o token é o próprio Id (GUID) do hook.
/// </summary>
public static class AutomationHookEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const int MaxNameChars = 120;
    private const int MaxPromptChars = 4000;

    /// <summary>Mapeia CRUD autenticado e rotas públicas de disparo.</summary>
    public static IEndpointRouteBuilder MapAutomationHookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hooks").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapDelete("/{id}", DeleteAsync);

        // Disparo — anônimo (o token é o segredo), com rate limit.
        app.MapPost("/api/v1/hooks/{token}", FireAsync).AllowAnonymous();
        app.MapPost("/api/v1/hooks/n8n/{token}", FireAsync).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var hooks = await db.AutomationHooks.AsNoTracking()
            .Where(h => h.UserId == user.Id)
            .OrderByDescending(h => h.CreatedAt)
            .Select(h => new AutomationHookResponse(
                h.Id, h.Name, h.ChatId, h.Enabled, h.CreatedAt, h.LastFiredAt,
                $"/api/v1/hooks/{h.Id}"))
            .ToListAsync(ct);
        return Results.Ok(hooks);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var request = await JsonSerializer.DeserializeAsync<AutomationHookCreateRequest>(
            http.Request.Body, JsonOptions, ct);
        var name = (request?.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxNameChars)
        {
            return Results.BadRequest(new { detail = "name é obrigatório (máx. 120)." });
        }

        Chat? chat = null;
        if (request?.ChatId is { } chatId)
        {
            chat = await db.Chats.FirstOrDefaultAsync(
                c => c.Id == chatId && c.UserId == user.Id, ct);
            if (chat is null)
            {
                return Results.NotFound(new { detail = "chatId não encontrado." });
            }
        }
        else
        {
            var now0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            chat = new Chat
            {
                UserId = user.Id,
                Title = $"webhook: {name}",
                CreatedAt = now0,
                UpdatedAt = now0,
            };
            db.Chats.Add(chat);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hook = new AutomationHook
        {
            UserId = user.Id,
            ChatId = chat.Id,
            Name = name,
            CreatedAt = now,
        };
        db.AutomationHooks.Add(hook);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new AutomationHookResponse(
            hook.Id, hook.Name, hook.ChatId, hook.Enabled, hook.CreatedAt,
            hook.LastFiredAt, $"/api/v1/hooks/{hook.Id}"));
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var deleted = await db.AutomationHooks
            .Where(h => h.Id == id && h.UserId == user.Id)
            .ExecuteDeleteAsync(ct);
        return deleted > 0 ? Results.Ok(new { deleted = true }) : Results.NotFound();
    }

    private static async Task<IResult> FireAsync(
        HttpContext http, AppDbContext db, IChatRunDispatcher dispatcher,
        ConfigService config, RateLimitService limits, string token,
        CancellationToken ct)
    {
        var rateConfig = await config.GetAsync("ratelimit", RateLimitConfig.Default, ct);
        if (rateConfig.Enabled)
        {
            var key = $"hook:{http.Connection.RemoteIpAddress?.ToString() ?? "anon"}";
            var retryAfter = limits.TryAcquire(
                key, rateConfig, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (retryAfter is { } retry)
            {
                http.Response.Headers.RetryAfter = retry.ToString();
                return Results.Json(
                    new { detail = "Limite de disparos excedido." }, statusCode: 429);
            }
        }

        var hook = await db.AutomationHooks
            .Include(h => h.Chat)
            .FirstOrDefaultAsync(h => h.Id == token && h.Enabled, ct);
        if (hook?.Chat is null)
        {
            return Results.NotFound(new { detail = "webhook não encontrado." });
        }

        var (content, modelOverride) = await ReadPromptAsync(http.Request, ct);
        if (string.IsNullOrWhiteSpace(content))
        {
            return Results.BadRequest(new { detail = "corpo vazio — envie prompt ou payload JSON." });
        }

        var model = modelOverride ?? FirstModel(hook.Chat.ModelsJson);
        if (model is null)
        {
            return Results.UnprocessableEntity(new
            {
                detail = "sem modelo: configure um modelo no chat vinculado "
                         + "ou envie 'model' no corpo do webhook.",
            });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var leaf = await db.ChatMessages
            .Where(m => m.ChatId == hook.ChatId)
            .OrderByDescending(m => m.Position)
            .Select(m => new { m.Id, m.Position })
            .FirstOrDefaultAsync(ct);
        // Leaf explícito só vale se ainda existe (podem ter sido deletados);
        // senão cai no topo legado por Position.
        string? leafId = hook.Chat.LeafMessageId;
        if (leafId is not null && leaf?.Id != leafId
            && !await db.ChatMessages.AnyAsync(
                m => m.ChatId == hook.ChatId && m.Id == leafId, ct))
        {
            leafId = null;
        }
        leafId ??= leaf?.Id;
        var message = new ChatMessage
        {
            ChatId = hook.ChatId,
            Role = "user",
            Content = content,
            Position = (leaf?.Position ?? -1) + 1,
            Timestamp = now,
            ParentId = leafId,
        };
        db.ChatMessages.Add(message);
        hook.Chat.LeafMessageId = message.Id;

        var run = new ChatRun
        {
            ChatId = hook.ChatId,
            UserId = hook.UserId,
            Model = model,
            RequestJson = JsonSerializer.Serialize(
                new ChatCompletionRequest(
                    model,
                    [new ChatCompletionMessage("user", content)],
                    Stream: true),
                JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(run);
        hook.Chat.UpdatedAt = now;
        hook.LastFiredAt = now;
        await db.SaveChangesAsync(ct);

        dispatcher.Enqueue(run.Id);
        return Results.Accepted(value: new AutomationHookFireResponse(
            run.Id, hook.ChatId, ChatRunStatus.Queued));
    }

    /// <summary>
    /// Lê o corpo do webhook: JSON com <c>prompt</c>/<c>payload</c>/<c>model</c>
    /// opcionais, JSON genérico (vira bloco de código) ou texto puro.
    /// </summary>
    private static async Task<(string? Content, string? Model)> ReadPromptAsync(
        HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var raw = await reader.ReadToEndAsync(ct);
        if (raw.Length > MaxPromptChars * 4)
        {
            raw = raw[..(MaxPromptChars * 4)];
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (Truncate(raw), null);
            }

            var model = root.TryGetProperty("model", out var m)
                        && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            var prompt = root.TryGetProperty("prompt", out var p)
                         && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
            root.TryGetProperty("payload", out var payload);

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                var content = payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? prompt
                    : $"{prompt}\n\n```json\n{Truncate(payload.GetRawText(), MaxPromptChars)}\n```";
                return (Truncate(content), model);
            }

            return (Truncate($"```json\n{Truncate(raw, MaxPromptChars)}\n```"), model);
        }
        catch (JsonException)
        {
            return (Truncate(raw), null);
        }
    }

    private static string? FirstModel(string modelsJson)
    {
        try
        {
            var models = JsonSerializer.Deserialize<List<string>>(modelsJson, JsonOptions);
            return models?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max = MaxPromptChars) =>
        value.Length <= max ? value : value[..max] + "…";

    private static Task<User?> CurrentUserAsync(
        HttpContext http, AppDbContext db, CancellationToken ct) =>
        AuthEndpoints.FindUserAsync(http, db, ct);
}
