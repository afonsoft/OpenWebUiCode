using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de autenticação, espelhando <c>/api/v1/auths</c> do Open WebUI.</summary>
public static class AuthEndpoints
{
    /// <summary>Mapeia as rotas de autenticação.</summary>
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auths");

        group.MapPost("/signup", SignUpAsync).AllowAnonymous();
        group.MapPost("/signin", SignInAsync).AllowAnonymous();
        group.MapPost("/signout", SignOutAsync);
        group.MapGet("/", GetCurrentUserAsync).RequireAuthorization();
        group.MapPut("/profile", UpdateProfileAsync).RequireAuthorization();
        group.MapPost("/update/profile", UpdateProfileAsync).RequireAuthorization();
        group.MapPost("/update/password", UpdatePasswordAsync).RequireAuthorization();
        group.MapPost("/update/timezone", UpdateTimezoneAsync).RequireAuthorization();
        group.MapPost("/api_key", CreateApiKeyAsync).RequireAuthorization();
        group.MapGet("/api_key", GetApiKeyAsync).RequireAuthorization();
        group.MapDelete("/api_key", DeleteApiKeyAsync).RequireAuthorization();
        group.MapPost("/add", AddUserAsync).RequireAuthorization();
        group.MapGet("/admin/config", GetAdminConfigAsync).RequireAuthorization();
        group.MapPost("/admin/config", UpdateAdminConfigAsync).RequireAuthorization();

        return group;
    }

    private static async Task<IResult> SignUpAsync(
        SignUpRequest request,
        AppDbContext db,
        ConfigService config,
        JwtTokenService tokens,
        NotificationService notifications,
        CancellationToken ct)
    {
        var adminConfig = await config.GetAdminConfigAsync(ct);
        var anyUser = await db.Users.AnyAsync(ct);
        if (anyUser && !adminConfig.EnableSignup)
        {
            return Results.Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.BadRequest(new { detail = "Nome, e-mail e senha são obrigatórios.", error_code = "invalid_fields" });
        }

        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return Results.BadRequest(new { detail = "E-mail já cadastrado.", error_code = "email_taken" });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var user = new User
        {
            Name = request.Name.Trim(),
            Email = email,
            Role = !anyUser ? UserRoles.Admin : NormalizeRole(adminConfig.DefaultUserRole),
            PermissionsJson = "{}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        await JoinDomainGroupsAsync(email, user.Id, db, ct);

        if (user.Role == UserRoles.Pending)
        {
            await notifications.DispatchAsync("user.pending",
                new { user.Id, user.Name, user.Email }, user.Id, ct);
            return Results.Ok(new AuthResponse(
                string.Empty, "Bearer", DateTimeOffset.UtcNow, ToResponse(user)));
        }

        return await IssueAuthResponseAsync(user, tokens, config, ct);
    }

    private static async Task<IResult> SignInAsync(
        SignInRequest request,
        HttpContext http,
        AppDbContext db,
        ConfigService config,
        JwtTokenService tokens,
        RateLimitService limits,
        CancellationToken ct)
    {
        var adminConfig = await config.GetAdminConfigAsync(ct);
        if (!adminConfig.EnableLoginForm)
        {
            return Results.BadRequest(new { detail = "Formulário de login desabilitado.", error_code = "login_form_disabled" });
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var rateConfig = await config.GetAsync("ratelimit", RateLimitConfig.Default, ct);
        var lockKey = $"{email}|{http.Connection.RemoteIpAddress}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (limits.IsLoginLocked(lockKey, rateConfig, now))
        {
            return Results.Json(
                new { detail = "Muitas tentativas de login. Tente novamente mais tarde.", error_code = "login_locked" },
                statusCode: 429);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            return await TryLdapSignInAsync(request, email, new LdapDeps(db, config, tokens, limits), lockKey, rateConfig, ct);
        }

        var result = new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            // RF-002: fallback LDAP quando habilitado — falha de bind retorna erro genérico.
            return await TryLdapSignInAsync(request, email, new LdapDeps(db, config, tokens, limits), lockKey, rateConfig, ct);
        }

        if (user.Role == UserRoles.Pending)
        {
            return Results.BadRequest(new { detail = "Conta aguardando aprovação do administrador.", error_code = "pending_approval" });
        }

        user.LastActiveAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        limits.ResetLogin(lockKey);
        await db.SaveChangesAsync(ct);
        return await IssueAuthResponseAsync(user, tokens, config, ct);
    }

    private static IResult SignOutAsync() => Results.Ok(new StatusResponse(true));

    private static async Task<IResult> TryLdapSignInAsync(
        SignInRequest request, string email, LdapDeps deps,
        string lockKey, RateLimitConfig rateConfig, CancellationToken ct)
    {
        var identity = await LdapService.TryBindAsync(request.Email, request.Password, ct);
        if (identity is null)
        {
            deps.Limits.RecordLoginFailure(lockKey, rateConfig, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return Results.BadRequest(new { detail = "Credenciais inválidas.", error_code = "invalid_credentials" });
        }

        // Sem e-mail do diretório não dá para identificar a conta local.
        var ldapEmail = (identity.Email ?? email).Trim().ToLowerInvariant();
        var user = await deps.Db.Users.FirstOrDefaultAsync(u => u.Email == ldapEmail, ct);
        if (user is null)
        {
            var adminConfig = await deps.Config.GetAdminConfigAsync(ct);
            var anyUser = await deps.Db.Users.AnyAsync(ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            user = new User
            {
                Name = identity.Name ?? ldapEmail,
                Email = ldapEmail,
                Role = !anyUser ? UserRoles.Admin : NormalizeRole(adminConfig.DefaultUserRole),
                PermissionsJson = "{}",
                PasswordHash = string.Empty, // autenticação delegada ao LDAP
                CreatedAt = now,
                UpdatedAt = now,
            };
            deps.Db.Users.Add(user);
            await deps.Db.SaveChangesAsync(ct);
        }

        if (user.Role == UserRoles.Pending)
        {
            return Results.BadRequest(new { detail = "Conta aguardando aprovação do administrador.", error_code = "pending_approval" });
        }

        deps.Limits.ResetLogin(lockKey);
        return await IssueAuthResponseAsync(user, deps.Tokens, deps.Config, ct);
    }

    private static async Task<IResult> GetCurrentUserAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await FindUserAsync(http, db, ct);
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> UpdateProfileAsync(
        UpdateProfileRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            user.Name = request.Name.Trim();
        }

        user.ProfileImageUrl = request.ProfileImageUrl;
        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> UpdatePasswordAsync(
        UpdatePasswordRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var result = new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return Results.BadRequest(new { detail = "Senha atual incorreta.", error_code = "wrong_password" });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 4)
        {
            return Results.BadRequest(new { detail = "A nova senha precisa de ao menos 4 caracteres.", error_code = "weak_password" });
        }

        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, request.NewPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> UpdateTimezoneAsync(
        UpdateTimezoneRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        user.Timezone = request.Timezone;
        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> CreateApiKeyAsync(
        HttpContext http,
        AppDbContext db,
        ConfigService config,
        HybridCache cache,
        CancellationToken ct)
    {
        var user = await FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var adminConfig = await config.GetAdminConfigAsync(ct);
        if (!adminConfig.EnableApiKeys)
        {
            return Results.BadRequest(new { detail = "Chaves de API desabilitadas.", error_code = "api_keys_disabled" });
        }

        var key = $"sk-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Rotação: invalida a identidade cacheada da chave substituída.
        var oldHashes = await db.ApiKeys.Where(k => k.UserId == user.Id)
            .Select(k => k.KeyHash).ToListAsync(ct);
        db.ApiKeys.RemoveRange(db.ApiKeys.Where(k => k.UserId == user.Id));
        await ApiKeyAuthCache.EvictAsync(cache, oldHashes);
        db.ApiKeys.Add(new ApiKey
        {
            UserId = user.Id,
            KeyHash = HashApiKey(key),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new ApiKeyCreatedResponse(key));
    }

    private static async Task<IResult> GetApiKeyAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var key = await db.ApiKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.UserId == user.Id, ct);
        return key is null
            ? Results.NotFound()
            : Results.Ok(new ApiKeyInfoResponse(key.CreatedAt, key.UpdatedAt));
    }

    private static async Task<IResult> DeleteApiKeyAsync(
        HttpContext http, AppDbContext db, HybridCache cache, CancellationToken ct)
    {
        var user = await FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var hashes = await db.ApiKeys.Where(k => k.UserId == user.Id)
            .Select(k => k.KeyHash).ToListAsync(ct);
        await db.ApiKeys.Where(k => k.UserId == user.Id).ExecuteDeleteAsync(ct);
        await ApiKeyAuthCache.EvictAsync(cache, hashes);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> AddUserAsync(
        AddUserRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return Results.BadRequest(new { detail = "E-mail já cadastrado.", error_code = "email_taken" });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var user = new User
        {
            Name = request.Name.Trim(),
            Email = email,
            Role = NormalizeRole(request.Role),
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, request.Password);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> GetAdminConfigAsync(
        HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        return Results.Ok(await config.GetAdminConfigAsync(ct));
    }

    private static async Task<IResult> UpdateAdminConfigAsync(
        AdminConfig request,
        HttpContext http,
        ConfigService config,
        CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        await config.SetAsync("admin.config", request, ct);
        return Results.Ok(request);
    }

    private static string NormalizeRole(string? role) =>
        role is UserRoles.Admin or UserRoles.User ? role : UserRoles.Pending;

    /// <summary>Calcula o hash SHA-256 de uma chave de API.</summary>
    public static string HashApiKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    internal static async Task<User?> FindUserAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var id = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return id is null ? null : await db.Users.FindAsync([id], ct);
    }

    /// <summary>
    /// Resolve o usuário e aplica a permissão granular ("seção.flag") do
    /// <see cref="PermissionService"/>: usuário ausente → Unauthorized, sem a
    /// permissão → Forbid. Retorna <c>(user, null)</c> quando autorizado.
    /// </summary>
    internal static async Task<(User? User, IResult? Error)> RequirePermissionAsync(
        HttpContext http, AppDbContext db, PermissionService permissions,
        string permission, CancellationToken ct)
    {
        var user = await FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, Results.Unauthorized());
        }
        return await permissions.HasAsync(user, permission, ct)
            ? (user, null)
            : (null, Results.Forbid());
    }

    /// <summary>
    /// Filtra todo um grupo (ou rota) pela permissão granular "seção.flag" —
    /// 401 sem usuário, 403 sem permissão. Funciona em <see cref="RouteGroupBuilder"/>
    /// e <see cref="RouteHandlerBuilder"/>.
    /// </summary>
    internal static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var permissions = http.RequestServices.GetRequiredService<PermissionService>();
            var (_, error) = await RequirePermissionAsync(
                http, db, permissions, permission, http.RequestAborted);
            return error ?? await next(ctx);
        });

    internal static UserResponse ToResponse(User user) =>
        new(user.Id, user.Name, user.Email, user.Role, user.ProfileImageUrl, user.Timezone);

    private static async Task<IResult> IssueAuthResponseAsync(
        User user, JwtTokenService tokens, ConfigService config, CancellationToken ct)
    {
        var (token, expires) = await tokens.CreateTokenAsync(user, ct);
        return Results.Ok(new AuthResponse(token, "Bearer", expires, ToResponse(user)));
    }

    /// <summary>Vincula o usuário recém-criado aos grupos cujo e-mail bate em allowed_domains.</summary>
    private static async Task JoinDomainGroupsAsync(
        string email, string userId, AppDbContext db, CancellationToken ct)
    {
        var at = email.LastIndexOf('@');
        if (at < 0)
        {
            return;
        }
        var domain = email[(at + 1)..];

        var groups = await db.Groups
            .Where(g => g.AllowedDomainsJson != "[]")
            .ToListAsync(ct);
        foreach (var group in groups)
        {
            List<string>? domains;
            try
            {
                domains = System.Text.Json.JsonSerializer.Deserialize<List<string>>(group.AllowedDomainsJson);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }
            if (domains?.Any(d => string.Equals(
                    d.Trim(), domain, StringComparison.OrdinalIgnoreCase)) == true)
            {
                db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = userId });
            }
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Bundle DI do fluxo LDAP (S107).</summary>
internal sealed record LdapDeps(
    AppDbContext Db, ConfigService Config,
    JwtTokenService Tokens, RateLimitService Limits);
