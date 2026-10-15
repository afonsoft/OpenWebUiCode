using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de webhooks de notificação (por usuário e globais/admin).</summary>
public static class NotificationEndpoints
{
    /// <summary>Mapeia as rotas de notifications.</summary>
    public static RouteGroupBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/notifications").RequireAuthorization();

        // Nota: lambdas precisam de 2+ parâmetros para não casar com o overload
        // RequestDelegate, que descarta o IResult retornado (ASP0016).
        group.MapGet("/webhook", (HttpContext http, AppDbContext db) => GetAsync(http, db, false));
        group.MapPost("/webhook", (HttpContext http, AppDbContext db) => SaveAsync(http, db, false));
        group.MapDelete("/webhook", (HttpContext http, AppDbContext db) => DeleteAsync(http, db, false));
        group.MapPost("/webhook/test",
            (HttpContext http, AppDbContext db, NotificationService notifications) =>
                TestAsync(http, db, notifications, false));

        group.MapGet("/admin/webhook", (HttpContext http, AppDbContext db) => GetAsync(http, db, true));
        group.MapPost("/admin/webhook", (HttpContext http, AppDbContext db) => SaveAsync(http, db, true));
        group.MapDelete("/admin/webhook", (HttpContext http, AppDbContext db) => DeleteAsync(http, db, true));
        group.MapPost("/admin/webhook/test",
            (HttpContext http, AppDbContext db, NotificationService notifications) =>
                TestAsync(http, db, notifications, true));

        // SPEC-20261007-chat-notifications RF-004: subscriptions Web Push
        // (aviso de run concluída com a aba fechada).
        group.MapGet("/push/vapid-key",
            (HttpContext http, AppDbContext db, VapidKeyService vapid) =>
                GetVapidKeyAsync(http, db, vapid));
        group.MapPost("/push/subscriptions",
            (HttpContext http, AppDbContext db) => SavePushSubscriptionAsync(http, db));
        group.MapDelete("/push/subscriptions",
            (HttpContext http, AppDbContext db, string endpoint) =>
                DeletePushSubscriptionAsync(http, db, endpoint));

        // SPEC-20261009-notification-feed D3: feed in-app do usuário.
        // Parâmetros de página/filtro são int?/bool? — int não-nulável
        // tornaria o parâmetro obrigatório (400 antes do handler).
        group.MapGet("/",
            (HttpContext http, AppDbContext db, int? page, int? pageSize, bool? unread) =>
                ListFeedAsync(http, db, page, pageSize, unread));
        group.MapPost("/{id}/read",
            (HttpContext http, AppDbContext db, string id) =>
                MarkFeedReadAsync(http, db, id));
        group.MapPost("/read-all",
            (HttpContext http, AppDbContext db) => MarkAllFeedReadAsync(http, db));

        return group;
    }

    private static async Task<IResult> GetAsync(HttpContext http, AppDbContext db, bool global)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();
        if (global && user.Role != UserRoles.Admin) return Results.Forbid();

        var webhook = await FindAsync(db, user, global, http.RequestAborted);
        return webhook is null ? Results.NotFound() : Results.Ok(Map(webhook));
    }

    private static async Task<IResult> SaveAsync(HttpContext http, AppDbContext db, bool global)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();
        if (global && user.Role != UserRoles.Admin) return Results.Forbid();

        var request = await JsonSerializer.DeserializeAsync<NotificationWebhookRequest>(
            http.Request.Body, JsonSerializerOptions.Web, http.RequestAborted);
        if (request is null || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return Results.BadRequest(new { detail = "URL http/https válida obrigatória." });
        }

        var webhook = await FindAsync(db, user, global, http.RequestAborted);
        if (webhook is null)
        {
            webhook = new NotificationWebhook
            {
                OwnerId = global ? null : user.Id,
                Secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                    .ToLowerInvariant(),
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            db.NotificationWebhooks.Add(webhook);
        }
        webhook.Url = request.Url;
        webhook.Events = string.Join(",", request.Events);
        webhook.Enabled = request.Enabled;

        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(Map(webhook));
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, AppDbContext db, bool global)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();
        if (global && user.Role != UserRoles.Admin) return Results.Forbid();

        var webhook = await FindAsync(db, user, global, http.RequestAborted);
        if (webhook is null) return Results.NotFound();
        db.NotificationWebhooks.Remove(webhook);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> TestAsync(
        HttpContext http, AppDbContext db, NotificationService notifications, bool global)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();
        if (global && user.Role != UserRoles.Admin) return Results.Forbid();

        var webhook = await FindAsync(db, user, global, http.RequestAborted);
        if (webhook is null) return Results.NotFound();

        var body = JsonSerializer.Serialize(new { @event = "test", data = new { }, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        var status = await notifications.SendSafelyAsync(webhook, "test", body);
        return Results.Ok(new WebhookTestResponse(status, status is >= 200 and < 300));
    }

    private static async Task<IResult> GetVapidKeyAsync(
        HttpContext http, AppDbContext db, VapidKeyService vapid)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();

        var publicKey = await vapid.GetPublicKeyAsync(http.RequestAborted);
        return publicKey is null
            ? Results.NotFound(new { detail = "Web Push não configurado neste servidor." })
            : Results.Ok(new VapidPublicKeyResponse(publicKey));
    }

    private static async Task<IResult> SavePushSubscriptionAsync(HttpContext http, AppDbContext db)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();

        var request = await JsonSerializer.DeserializeAsync<PushSubscriptionRequest>(
            http.Request.Body, JsonSerializerOptions.Web, http.RequestAborted);
        if (request is null || string.IsNullOrWhiteSpace(request.Endpoint)
            || string.IsNullOrWhiteSpace(request.Keys?.P256dh)
            || string.IsNullOrWhiteSpace(request.Keys.Auth))
        {
            return Results.BadRequest(new { detail = "endpoint, keys.p256dh e keys.auth são obrigatórios." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Upsert por endpoint: re-subscribe do mesmo navegador só rotaciona
        // as chaves — e sempre sob o UserId do dono autenticado.
        var existing = await db.ChatPushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint, http.RequestAborted);
        if (existing is not null)
        {
            existing.UserId = user.Id;
            existing.P256dh = request.Keys.P256dh;
            existing.Auth = request.Keys.Auth;
            existing.UserAgent = request.UserAgent;
            existing.UpdatedAt = now;
        }
        else
        {
            db.ChatPushSubscriptions.Add(new ChatPushSubscription
            {
                UserId = user.Id,
                Endpoint = request.Endpoint.Trim(),
                P256dh = request.Keys.P256dh,
                Auth = request.Keys.Auth,
                UserAgent = request.UserAgent,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { subscribed = true });
    }

    private static async Task<IResult> DeletePushSubscriptionAsync(
        HttpContext http, AppDbContext db, string endpoint)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();

        // Só o dono remove a própria subscription.
        var existing = await db.ChatPushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == endpoint && s.UserId == user.Id,
                http.RequestAborted);
        if (existing is null) return Results.NotFound();

        db.ChatPushSubscriptions.Remove(existing);
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Já removida por outro caminho (ex.: o WebPushChatRunNotifier
            // pruneou a subscription morta entre o SELECT e o DELETE).
            return Results.NotFound();
        }
        return Results.Ok(new { subscribed = false });
    }

    private static async Task<IResult> ListFeedAsync(
        HttpContext http, AppDbContext db, int? page, int? pageSize, bool? unread)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();

        var pageNum = page is > 0 ? page.Value : 1;
        var size = pageSize is > 0 ? Math.Min(pageSize.Value, 50) : 20;

        var baseQuery = db.Notifications.AsNoTracking().Where(n => n.UserId == user.Id);
        var filtered = unread == true ? baseQuery.Where(n => n.ReadAt == null) : baseQuery;

        var total = await filtered.CountAsync(http.RequestAborted);
        var unreadCount = await baseQuery.CountAsync(n => n.ReadAt == null, http.RequestAborted);
        var items = await filtered
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip((pageNum - 1) * size)
            .Take(size)
            .Select(n => new NotificationItemResponse(
                n.Id, n.Kind, n.Title, n.Body, n.Link, n.ReadAt, n.CreatedAt))
            .ToListAsync(http.RequestAborted);

        return Results.Ok(new NotificationPageResponse(items, total, unreadCount, pageNum));
    }

    private static async Task<IResult> MarkFeedReadAsync(
        HttpContext http, AppDbContext db, string id)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();

        // Leitura idempotente: reler uma já lida responde 200, não 404.
        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == user.Id,
                http.RequestAborted);
        if (notification is null) return Results.NotFound();

        notification.ReadAt ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> MarkAllFeedReadAsync(HttpContext http, AppDbContext db)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, http.RequestAborted);
        if (user is null) return Results.Unauthorized();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var updated = await db.Notifications
            .Where(n => n.UserId == user.Id && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now),
                http.RequestAborted);
        return Results.Ok(new { ok = true, read = updated });
    }

    private static Task<NotificationWebhook?> FindAsync(AppDbContext db, User user, bool global, CancellationToken ct) =>
        db.NotificationWebhooks.FirstOrDefaultAsync(
            w => global ? w.OwnerId == null : w.OwnerId == user.Id, ct);

    private static NotificationWebhookResponse Map(NotificationWebhook w) => new(
        w.Id, w.Url,
        w.Events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        w.Enabled);
}
