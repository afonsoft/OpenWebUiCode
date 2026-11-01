using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Provisionamento SCIM 2.0 (RFC 7643/7644, subconjunto): mapeia Users para a
/// entidade User e Groups para Group. Autenticação por token dedicado
/// (`scim.token` em config) — nunca JWT de usuário.
/// </summary>
public class ScimService(AppDbContext db, ConfigService config)
{
    /// <summary>Se o endpoint SCIM está habilitado.</summary>
    public Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        config.GetAsync("scim.enabled", false, ct);

    /// <summary>Valida o bearer token dedicado (config `scim.token`).</summary>
    public async Task<bool> ValidateTokenAsync(string? token, CancellationToken ct = default)
    {
        var expected = await config.GetAsync<string?>("scim.token", null, ct);
        return !string.IsNullOrEmpty(expected)
            && string.Equals(expected, token, StringComparison.Ordinal);
    }

    /// <summary>Lista usuários com filtro opcional `userName eq "x"` e paginação SCIM.</summary>
    public async Task<ScimListResponse<ScimUser>> ListUsersAsync(
        string? filter, int startIndex, int count, string baseUrl, CancellationToken ct = default)
    {
        var query = db.Users.AsQueryable();
        var userName = ParseUserNameFilter(filter);
        if (userName is not null)
        {
            query = query.Where(u => u.Email == userName);
        }

        var total = await query.CountAsync(ct);
        var users = await query.OrderBy(u => u.CreatedAt)
            .Skip(Math.Max(0, startIndex - 1)).Take(count).ToListAsync(ct);
        return new ScimListResponse<ScimUser>(
            ["urn:ietf:params:scim:api:messages:2.0:ListResponse"],
            total, startIndex, users.Count,
            users.Select(u => ToScimUser(u, baseUrl)).ToList());
    }

    /// <summary>Busca um usuário por id.</summary>
    public async Task<ScimUser?> GetUserAsync(string id, string baseUrl, CancellationToken ct = default)
    {
        var user = await db.Users.FindAsync([id], ct);
        return user is null ? null : ToScimUser(user, baseUrl);
    }

    /// <summary>Cria um usuário (papel = default do admin config; sem senha local).</summary>
    public async Task<ScimUser> CreateUserAsync(
        ScimUser input, string baseUrl, CancellationToken ct = default)
    {
        var email = (input.Emails?.FirstOrDefault()?.Value ?? input.UserName).Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            var adminConfig = await config.GetAdminConfigAsync(ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            user = new User
            {
                Email = email,
                Name = ResolveDisplayName(input, email),
                Role = NormalizeRole(adminConfig.DefaultUserRole),
                PermissionsJson = "{}",
                PasswordHash = string.Empty, // login via IdP
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Users.Add(user);
        }
        else
        {
            ApplyAttributes(user, input);
            user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        await db.SaveChangesAsync(ct);
        return ToScimUser(user, baseUrl);
    }

    /// <summary>Substitui atributos de um usuário (PUT).</summary>
    public async Task<ScimUser?> ReplaceUserAsync(
        string id, ScimUser input, string baseUrl, CancellationToken ct = default)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return null;
        }

        ApplyAttributes(user, input);
        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return ToScimUser(user, baseUrl);
    }

    /// <summary>Aplica um PATCH SCIM (op replace suportada para active/name/emails/userName).</summary>
    public async Task<ScimUser?> PatchUserAsync(
        string id, System.Text.Json.Nodes.JsonNode patch, string baseUrl, CancellationToken ct = default)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return null;
        }

        var ops = patch["Operations"] ?? patch["operations"];
        foreach (var op in ops?.AsArray() ?? [])
        {
            var opName = op?["op"]?.GetValue<string>();
            if (!string.Equals(opName, "replace", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = op?["value"];
            var active = value?["active"]?.GetValue<bool?>();
            if (active is not null)
            {
                ApplyActive(user, active.Value);
            }
        }

        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return ToScimUser(user, baseUrl);
    }

    /// <summary>Remove o usuário (DELETE físico — como o upstream).</summary>
    public async Task<bool> DeleteUserAsync(string id, CancellationToken ct = default)
    {
        var user = await db.Users.FindAsync([id], ct);
        if (user is null)
        {
            return false;
        }

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Lista grupos como recursos SCIM Group.</summary>
    public async Task<ScimListResponse<ScimGroup>> ListGroupsAsync(
        int startIndex, int count, string baseUrl, CancellationToken ct = default)
    {
        var total = await db.Groups.CountAsync(ct);
        var groups = await db.Groups.Include(g => g.Members)
            .OrderBy(g => g.CreatedAt).Skip(Math.Max(0, startIndex - 1))
            .Take(count).ToListAsync(ct);
        return new ScimListResponse<ScimGroup>(
            ["urn:ietf:params:scim:api:messages:2.0:ListResponse"],
            total, startIndex, groups.Count,
            groups.Select(g => ToScimGroup(g, baseUrl)).ToList());
    }

    /// <summary>Cria um grupo com membros referenciados por user id.</summary>
    public async Task<ScimGroup?> CreateGroupAsync(
        ScimGroup input, string baseUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.DisplayName))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var group = new Group { Name = input.DisplayName.Trim(), CreatedAt = now, UpdatedAt = now };
        db.Groups.Add(group);
        var wantedIds = (input.Members ?? []).Select(m => m.Value).ToList();
        var validIds = await db.Users.Where(u => wantedIds.Contains(u.Id))
            .Select(u => u.Id).ToListAsync(ct);
        foreach (var id in validIds)
        {
            group.Members.Add(new GroupMember { GroupId = group.Id, UserId = id });
        }

        await db.SaveChangesAsync(ct);
        return ToScimGroup(group, baseUrl);
    }

    /// <summary>Converte User para o recurso SCIM User.</summary>
    public static ScimUser ToScimUser(User user, string baseUrl)
    {
        var active = user.Role is not UserRoles.Pending;
        return new ScimUser(
            ["urn:ietf:params:scim:schemas:core:2.0:User"],
            user.Id, user.Email, user.Name,
            new ScimName(user.Name, null, null),
            [new ScimEmail(user.Email, "work", true)],
            active,
            new ScimMeta("User", $"{baseUrl}/Users/{user.Id}",
                Iso(user.CreatedAt), Iso(user.UpdatedAt)));
    }

    private static ScimGroup ToScimGroup(Group group, string baseUrl) =>
        new(
            ["urn:ietf:params:scim:schemas:core:2.0:Group"],
            group.Id, group.Name,
            group.Members.Select(m => new ScimGroupMember(m.UserId, null)).ToList(),
            new ScimMeta("Group", $"{baseUrl}/Groups/{group.Id}",
                Iso(group.CreatedAt), Iso(group.UpdatedAt)));

    private static void ApplyAttributes(User user, ScimUser input)
    {
        var email = (input.Emails?.FirstOrDefault()?.Value ?? input.UserName)?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(email))
        {
            user.Email = email;
        }

        user.Name = ResolveDisplayName(input, user.Name);
        ApplyActive(user, input.Active);
    }

    private static void ApplyActive(User user, bool active)
    {
        // pending = não autentica; reativar volta a "user" (admin nunca é rebaixado por SCIM).
        if (!active && user.Role != UserRoles.Pending)
        {
            user.Role = UserRoles.Pending;
        }
        else if (active && user.Role == UserRoles.Pending)
        {
            user.Role = UserRoles.User;
        }
    }

    private static string ResolveDisplayName(ScimUser input, string fallback)
    {
        var name = input.DisplayName ?? input.Name?.Formatted
            ?? string.Join(' ', new[] { input.Name?.GivenName, input.Name?.FamilyName }
                .Where(p => !string.IsNullOrEmpty(p))).Trim();
        return string.IsNullOrEmpty(name) ? fallback : name;
    }

    private static string? ParseUserNameFilter(string? filter)
    {
        // Formato suportado: userName eq "valor"
        if (filter is null)
        {
            return null;
        }

        const string prefix = "userName eq \"";
        if (!filter.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !filter.EndsWith('"'))
        {
            return null;
        }

        return filter[prefix.Length..^1];
    }

    private static string NormalizeRole(string? role) =>
        role is UserRoles.Admin or UserRoles.User ? role : UserRoles.Pending;

    private static string Iso(long epoch) =>
        DateTimeOffset.FromUnixTimeSeconds(epoch).ToString("o");
}
