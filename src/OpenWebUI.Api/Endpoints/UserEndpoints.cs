using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de usuários, espelhando <c>/api/v1/users</c> do Open WebUI.</summary>
public static class UserEndpoints
{
    /// <summary>Mapeia as rotas de usuários.</summary>
    public static RouteGroupBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users").RequireAuthorization();

        group.MapGet("/", ListUsersAsync);
        group.MapGet("/all", ListAllUsersAsync);
        group.MapGet("/search", SearchUsersAsync);
        group.MapGet("/active", ListActiveAsync);
        group.MapGet("/permissions", GetPermissionsAsync);
        group.MapGet("/user/settings", GetUserSettingsAsync);
        group.MapPost("/user/settings", UpdateUserSettingsAsync);
        group.MapPost("/user/settings/update", UpdateUserSettingsAsync);
        group.MapGet("/user/tool-approvals", GetToolApprovalsAsync);
        group.MapPut("/user/tool-approvals", UpdateToolApprovalsAsync);
        group.MapGet("/{id}", GetUserAsync);
        group.MapPost("/{id}/update", UpdateUserAsync);
        group.MapPost("/{id}/update/role", UpdateUserRoleAsync);
        group.MapGet("/{id}/permissions", GetUserPermissionsAsync);
        group.MapPut("/{id}/permissions", UpdateUserPermissionsAsync);
        group.MapGet("/{id}/oauth/sessions", ListOAuthSessionsAsync);
        group.MapDelete("/{id}/oauth/sessions/{sessionId}", RevokeOAuthSessionAsync);
        group.MapDelete("/{id}", DeleteUserAsync);

        return group;
    }

    private static async Task<IResult> ListUsersAsync(
        HttpContext http, AppDbContext db, CancellationToken ct,
        string? query = null, string? filter = null,
        string? order_by = null, string? direction = null,
        int page = 1, int per_page = 50)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var users = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLowerInvariant();
            users = users.Where(u => u.Name.ToLower().Contains(term) || u.Email.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var role = filter.Trim().ToLowerInvariant();
            users = users.Where(u => u.Role == role);
        }

        var ascending = string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase);
        users = order_by?.ToLowerInvariant() switch
        {
            "name" => ascending ? users.OrderBy(u => u.Name) : users.OrderByDescending(u => u.Name),
            "email" => ascending ? users.OrderBy(u => u.Email) : users.OrderByDescending(u => u.Email),
            "role" => ascending ? users.OrderBy(u => u.Role) : users.OrderByDescending(u => u.Role),
            "updated_at" or "last_active_at" => ascending
                ? users.OrderBy(u => u.UpdatedAt)
                : users.OrderByDescending(u => u.UpdatedAt),
            _ => ascending ? users.OrderBy(u => u.CreatedAt) : users.OrderByDescending(u => u.CreatedAt),
        };

        var total = await users.CountAsync(ct);
        page = Math.Max(1, page);
        per_page = Math.Clamp(per_page, 1, 100);
        var list = await users
            .Skip((page - 1) * per_page)
            .Take(per_page)
            .Select(u => new AdminUserResponse(
                u.Id, u.Name, u.Email, u.Role, u.ProfileImageUrl,
                u.CreatedAt, u.LastActiveAt ?? u.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(new { users = list, total, page });
    }

    private static IResult ListActiveAsync(HttpContext http)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        return Results.Ok(new { ids = Hubs.ChatHub.ConnectedUserIds() });
    }

    private static async Task<IResult> ListAllUsersAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var users = await db.Users.AsNoTracking()
            .Select(u => AuthEndpoints.ToResponse(u))
            .ToListAsync(ct);

        return Results.Ok(users);
    }

    private static async Task<IResult> SearchUsersAsync(
        HttpContext http, AppDbContext db, CancellationToken ct, string? query = null)
    {
        var users = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLower();
            users = users.Where(u => u.Name.ToLower().Contains(term) || u.Email.Contains(term));
        }

        var list = await users
            .Select(u => new { u.Id, u.Name, u.Email, u.ProfileImageUrl })
            .Take(20)
            .ToListAsync(ct);

        return Results.Ok(list);
    }

    private static IResult GetPermissionsAsync() => Results.Ok(new
    {
        workspace = new
        {
            models = true,
            knowledge = true,
            prompts = true,
            tools = true,
        },
        sharing = new { public_models = true, public_knowledge = true, public_prompts = true, public_tools = true },
        chat = new
        {
            controls = true,
            valves = true,
            system_prompt = true,
            @params = true,
            file_upload = true,
            delete = true,
            edit = true,
            share = true,
            export = true,
            stt = true,
            tts = true,
            call = true,
            multiple_models = true,
            temporary = true,
            temporary_enforced = false,
        },
        features = new { direct_tool_servers = false, web_search = false, image_generation = false, code_interpreter = false, notes = true },
    });

    private static async Task<IResult> GetUserSettingsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
            user.SettingsJson) ?? [];
        return Results.Ok(settings);
    }

    private static async Task<IResult> UpdateUserSettingsAsync(
        System.Text.Json.JsonElement request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        user.SettingsJson = request.GetRawText();
        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        return Results.Ok(request);
    }

    /// <summary>
    /// Catálogo de aprovações por tool (SPEC-20261013-approval-tiers):
    /// cada built-in com o tier declarado e o override vigente do usuário.
    /// </summary>
    private static async Task<IResult> GetToolApprovalsAsync(
        HttpContext http, AppDbContext db,
        OpenWebUI.Infrastructure.ChatTools.BuiltinToolRegistry builtins,
        ToolApprovalOverrideService overrides,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var map = await overrides.GetAsync(user.Id, ct);
        var tools = builtins.All
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new ToolApprovalEntry(
                t.Name,
                t.Tier.ToString().ToLowerInvariant(),
                map.TryGetValue(t.Name, out var action) ? action : null))
            .ToList();
        return Results.Ok(new ToolApprovalsResponse(tools));
    }

    /// <summary>
    /// Mescla overrides de aprovação por tool
    /// (<c>allow|deny|prompt</c>; null/"" remove). Devolve o catálogo
    /// resultante, idêntico ao GET.
    /// </summary>
    private static async Task<IResult> UpdateToolApprovalsAsync(
        UpdateToolApprovalsRequest request,
        HttpContext http, AppDbContext db,
        OpenWebUI.Infrastructure.ChatTools.BuiltinToolRegistry builtins,
        ToolApprovalOverrideService overrides,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (request.Overrides is null || request.Overrides.Count > 200)
        {
            return Results.BadRequest(
                new { detail = "overrides deve ter até 200 entradas." });
        }
        var invalid = request.Overrides
            .Where(kv => kv.Value is { Length: > 0 } v
                && !ToolApprovalOverrideService.ValidActions.Contains(v))
            .Select(kv => kv.Key).ToList();
        if (invalid.Count > 0)
        {
            return Results.BadRequest(new
            {
                detail = $"ação inválida em: {string.Join(", ", invalid)} "
                    + "(válidas: allow, deny, prompt).",
            });
        }

        await overrides.MergeAsync(user.Id, request.Overrides, ct);
        return await GetToolApprovalsAsync(http, db, builtins, overrides, ct);
    }

    private static async Task<IResult> GetUserAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        return user is null ? Results.NotFound() : Results.Ok(AuthEndpoints.ToResponse(user));
    }

    private static async Task<IResult> UpdateUserAsync(
        string id,
        AdminUpdateUserRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            user.Name = request.Name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.Role)
            && request.Role is UserRoles.Admin or UserRoles.User or UserRoles.Pending)
        {
            user.Role = request.Role;
        }

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>()
                .HashPassword(user, request.Password);
        }

        if (request.ProfileImageUrl is not null)
        {
            user.ProfileImageUrl = request.ProfileImageUrl;
        }

        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(AuthEndpoints.ToResponse(user));
    }

    private static async Task<IResult> UpdateUserRoleAsync(
        string id,
        UpdateUserRoleRequest request,
        HttpContext http,
        AppDbContext db,
        NotificationService notifications,
        CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }
        if (request.Role is not (UserRoles.Admin or UserRoles.User or UserRoles.Pending))
        {
            return Results.BadRequest(new { detail = "Papel inválido." });
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        var wasPending = user.Role == UserRoles.Pending;
        user.Role = request.Role;
        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        if (wasPending && request.Role != UserRoles.Pending)
        {
            await notifications.DispatchAsync("user.approved",
                new { user.Id, user.Name, user.Email, user.Role }, user.Id, ct);
        }
        return Results.Ok(AuthEndpoints.ToResponse(user));
    }

    private static async Task<IResult> GetUserPermissionsAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        // Retorna o JSON cru para preservar flags desconhecidas do contrato atual.
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(user.PermissionsJson);
            return Results.Json(doc.RootElement.Clone());
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.Json(new { });
        }
    }

    private static async Task<IResult> UpdateUserPermissionsAsync(
        string id, System.Text.Json.JsonElement permissions,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }
        if (permissions.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return Results.BadRequest(new { detail = "Permissões devem ser um objeto JSON." });
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        user.PermissionsJson = permissions.GetRawText();
        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(AuthEndpoints.ToResponse(user));
    }

    private static async Task<IResult> ListOAuthSessionsAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var selfId = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (selfId != id && !http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var sessions = await db.OAuthAccounts.AsNoTracking()
            .Where(a => a.UserId == id)
            .Select(a => new { a.Id, a.Provider, a.ProviderAccountId, a.Email, a.CreatedAt })
            .ToListAsync(ct);
        return Results.Ok(new { sessions });
    }

    private static async Task<IResult> RevokeOAuthSessionAsync(
        string id, string sessionId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var selfId = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (selfId != id && !http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var session = await db.OAuthAccounts
            .FirstOrDefaultAsync(a => a.Id == sessionId && a.UserId == id, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        db.OAuthAccounts.Remove(session);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> DeleteUserAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var selfId = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (id == selfId)
        {
            return Results.BadRequest(new { detail = "Não é possível excluir a própria conta." });
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }
}
