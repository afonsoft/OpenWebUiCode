using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de grupos/RBAC, espelhando <c>/api/v1/groups</c> do upstream.</summary>
public static class GroupEndpoints
{
    private const string AdminRole = "admin";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia as rotas de grupos.</summary>
    public static RouteGroupBuilder MapGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/groups").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapPost("/{id}/members", AddMembersAsync);
        group.MapDelete("/{id}/members/{userId}", RemoveMemberAsync);
        group.MapPut("/{id}/members/{userId}", UpdateMemberRoleAsync);

        return group;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        IQueryable<Group> query = db.Groups.AsNoTracking().Include(g => g.Members);
        if (user.Role != UserRoles.Admin)
        {
            query = query.Where(g => g.Members.Any(m => m.UserId == user.Id));
        }

        var groups = await query.OrderBy(g => g.Name).ToListAsync(ct);
        return Results.Ok(groups.Select(g => ToResponse(g, db)).ToList());
    }

    private static async Task<IResult> GetAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var group = await LoadAsync(id, db, ct);
        if (user is null || group is null)
        {
            return Results.NotFound();
        }

        var isMember = group.Members.Any(m => m.UserId == user.Id);
        return user.Role == UserRoles.Admin || isMember
            ? Results.Ok(ToResponse(group, db))
            : Results.Forbid();
    }

    private static async Task<IResult> CreateAsync(
        CreateGroupRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        // RF-003: somente admin global cria grupos.
        if (user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { detail = "Nome do grupo é obrigatório." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var group = new Group
        {
            Name = request.Name.Trim(),
            Description = request.Description,
            PermissionsJson = Serialize(request.Permissions ?? GroupPermissions.Full),
            AllowedDomainsJson = SerializeDomains(request.AllowedDomains),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(group, db));
    }

    private static async Task<IResult> UpdateAsync(
        string id, UpdateGroupRequest request, HttpContext http, AppDbContext db,
        CancellationToken ct)
    {
        var (_, group, error) = await AuthorizeManageAsync(id, http, db, ct);
        if (error is not null)
        {
            return error;
        }

        if (request.Name is not null)
        {
            group!.Name = request.Name.Trim();
        }

        if (request.Description is not null)
        {
            group!.Description = request.Description;
        }

        if (request.Permissions is not null)
        {
            group!.PermissionsJson = Serialize(request.Permissions);
        }

        if (request.AllowedDomains is not null)
        {
            group!.AllowedDomainsJson = SerializeDomains(request.AllowedDomains);
        }

        group!.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(group, db));
    }

    private static async Task<IResult> DeleteAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var group = await LoadAsync(id, db, ct, tracking: true);
        if (user is null || group is null)
        {
            return Results.NotFound();
        }

        if (user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }

        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> AddMembersAsync(
        string id, UpdateGroupMembersRequest request, HttpContext http, AppDbContext db,
        CancellationToken ct)
    {
        var (_, group, error) = await AuthorizeManageAsync(id, http, db, ct);
        if (error is not null)
        {
            return error;
        }
        if (group is null)
        {
            return Results.NotFound();
        }

        var role = request.Role is AdminRole ? AdminRole : "member";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var userId in request.UserIds.Distinct())
        {
            if (group.Members.Any(m => m.UserId == userId))
            {
                continue;
            }

            if (!await db.Users.AnyAsync(u => u.Id == userId, ct))
            {
                return Results.BadRequest(new { detail = $"Usuário {userId} não existe." });
            }

            db.GroupMembers.Add(new GroupMember
            {
                GroupId = group.Id,
                UserId = userId,
                Role = role,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse((await LoadAsync(id, db, ct))!, db));
    }

    private static async Task<IResult> RemoveMemberAsync(
        string id, string userId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (_, group, error) = await AuthorizeManageAsync(id, http, db, ct);
        if (error is not null)
        {
            return error;
        }

        var member = group!.Members.FirstOrDefault(m => m.UserId == userId);
        if (member is null)
        {
            return Results.NotFound();
        }

        db.GroupMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> UpdateMemberRoleAsync(
        string id, string userId, UpdateGroupMembersRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, group, error) = await AuthorizeManageAsync(id, http, db, ct);
        if (error is not null)
        {
            return error;
        }

        // Somente admin global altera papel no grupo.
        if (user!.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }

        var member = group!.Members.FirstOrDefault(m => m.UserId == userId);
        if (member is null)
        {
            return Results.NotFound();
        }

        member.Role = request.Role is AdminRole ? AdminRole : "member";
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(group, db));
    }

    private static async Task<(User? User, Group? Group, IResult? Error)> AuthorizeManageAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var group = await LoadAsync(id, db, ct, tracking: true);
        if (user is null || group is null)
        {
            return (user, group, Results.NotFound());
        }

        var groupAdmin = group.Members.Any(m => m.UserId == user.Id && m.Role == AdminRole);
        return user.Role == UserRoles.Admin || groupAdmin
            ? (user, group, null)
            : (user, group, Results.Forbid());
    }

    private static Task<Group?> LoadAsync(
        string id, AppDbContext db, CancellationToken ct, bool tracking = false)
    {
        var query = tracking ? db.Groups : db.Groups.AsNoTracking();
        return query.Include(g => g.Members)
            .FirstOrDefaultAsync(g => g.Id == id, ct);
    }

    private static GroupResponse ToResponse(Group group, AppDbContext db)
    {
        var userIds = group.Members.Select(m => m.UserId).ToList();
        var users = db.Users.Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Name, u.Email })
            .ToDictionary(u => u.Id);

        return new GroupResponse(
            group.Id,
            group.Name,
            group.Description,
            Deserialize(group.PermissionsJson),
            DeserializeDomains(group.AllowedDomainsJson),
            group.Members
                .Where(m => users.ContainsKey(m.UserId))
                .Select(m => new GroupMemberResponse(
                    m.UserId, users[m.UserId].Name, users[m.UserId].Email, m.Role))
                .ToList(),
            group.CreatedAt,
            group.UpdatedAt);
    }

    private static string SerializeDomains(IReadOnlyList<string>? domains) =>
        JsonSerializer.Serialize(
            (domains ?? []).Select(d => d.Trim().ToLowerInvariant())
                .Where(d => d.Length > 0).Distinct().ToList(), JsonOptions);

    private static List<string> DeserializeDomains(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Serialize(GroupPermissions permissions) =>
        JsonSerializer.Serialize(permissions, JsonOptions);

    private static GroupPermissions Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GroupPermissions>(json, JsonOptions)
                ?? GroupPermissions.Full;
        }
        catch (JsonException)
        {
            return GroupPermissions.Full;
        }
    }
}
