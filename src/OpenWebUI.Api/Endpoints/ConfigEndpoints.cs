using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Endpoints administrativos de configuração por domínio (/api/v1/configs/*),
/// espelhando o configs.py do upstream.
/// </summary>
public static class ConfigEndpoints
{
    /// <summary>Mapeia as rotas de configuração.</summary>
    public static RouteGroupBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/configs").RequireAuthorization();
        group.MapGet("/banners", ListBannersAsync);
        group.MapPost("/banners", CreateBannerAsync);
        group.MapPut("/banners/{id}", UpdateBannerAsync);
        group.MapDelete("/banners/{id}", DeleteBannerAsync);

        group.MapGet("/models", GetModelsConfigAsync);
        group.MapPost("/models", UpdateModelsConfigAsync);

        group.MapGet("/signup", GetSignupAsync);
        group.MapPost("/signup", UpdateSignupAsync);

        group.MapGet("/api_key", GetApiKeyToggleAsync);
        group.MapPost("/api_key", UpdateApiKeyToggleAsync);

        group.MapGet("/channels", GetChannelsToggleAsync);
        group.MapPost("/channels", UpdateChannelsToggleAsync);

        group.MapGet("/direct_connections", GetDirectConnectionsAsync);
        group.MapPost("/direct_connections", UpdateDirectConnectionsAsync)
            .AddEndpointFilter(ApiEndpoints.InvalidateModelListCacheAsync);

        group.MapGet("/code_execution", GetCodeExecutionAsync);
        group.MapPost("/code_execution", UpdateCodeExecutionAsync);

        group.MapGet("/jwt", GetJwtExpiryAsync);
        group.MapPost("/jwt", UpdateJwtExpiryAsync);

        // Papéis de modelo do próprio usuário (SPEC-20261013-model-roles).
        group.MapGet("/user/model-roles", GetUserModelRolesAsync);
        group.MapPut("/user/model-roles", UpdateUserModelRolesAsync);

        group.MapGet("/ratelimit", GetRateLimitAsync);
        group.MapPost("/ratelimit", UpdateRateLimitAsync);
        group.MapPost("/ratelimit/reset", ResetLoginLockoutAsync);

        return group;
    }

    private static bool IsAdmin(HttpContext http) => http.User.IsInRole(UserRoles.Admin);

    // ---------------- Banners ----------------

    private static async Task<IResult> ListBannersAsync(AppDbContext db, CancellationToken ct)
    {
        var banners = await db.Banners.AsNoTracking()
            .OrderByDescending(b => b.Timestamp)
            .Select(b => new BannerResponse(
                b.Id, b.Type, b.Title, b.Content, b.Dismissible, b.Timestamp))
            .ToListAsync(ct);
        return Results.Ok(banners);
    }

    private static async Task<IResult> CreateBannerAsync(
        BannerRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        if (!IsValidType(request.Type) || string.IsNullOrWhiteSpace(request.Title))
        {
            return Results.BadRequest(new { detail = "Tipo ou título inválido." });
        }

        var banner = new Banner
        {
            Type = request.Type,
            Title = request.Title.Trim(),
            Content = request.Content ?? string.Empty,
            Dismissible = request.Dismissible ?? true,
        };
        db.Banners.Add(banner);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(banner));
    }

    private static async Task<IResult> UpdateBannerAsync(
        string id, BannerRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        var banner = await db.Banners.FindAsync([id], ct);
        if (banner is null)
        {
            return Results.NotFound();
        }
        if (!IsValidType(request.Type))
        {
            return Results.BadRequest(new { detail = "Tipo inválido." });
        }

        banner.Type = request.Type;
        banner.Title = request.Title.Trim();
        banner.Content = request.Content ?? string.Empty;
        banner.Dismissible = request.Dismissible ?? banner.Dismissible;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(banner));
    }

    private static async Task<IResult> DeleteBannerAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        var banner = await db.Banners.FindAsync([id], ct);
        if (banner is null)
        {
            return Results.NotFound();
        }

        db.Banners.Remove(banner);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static bool IsValidType(string type) =>
        type is "info" or "warning" or "error" or "success";

    private static BannerResponse ToResponse(Banner banner) =>
        new(banner.Id, banner.Type, banner.Title, banner.Content, banner.Dismissible, banner.Timestamp);

    // ---------------- Models (defaults + sugestões) ----------------

    private static async Task<IResult> GetModelsConfigAsync(ConfigService config, CancellationToken ct) =>
        Results.Ok(await GetModelsConfigInternalAsync(config, ct));

    internal static async Task<ModelsConfig> GetModelsConfigInternalAsync(
        ConfigService config, CancellationToken ct) =>
        await config.GetAsync("ui.models", ModelsConfig.Empty, ct);

    private static async Task<IResult> UpdateModelsConfigAsync(
        ModelsConfig request, HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        var sanitized = new ModelsConfig(
            request.DefaultModels?.Where(m => !string.IsNullOrWhiteSpace(m)).ToList() ?? [],
            request.PromptSuggestions?
                .Where(s => !string.IsNullOrWhiteSpace(s.Title) || !string.IsNullOrWhiteSpace(s.Content))
                .ToList() ?? []);
        await config.SetAsync("ui.models", sanitized, ct);
        return Results.Ok(sanitized);
    }

    // ---------------- Toggles ----------------

    private static async Task<IResult> GetSignupAsync(ConfigService config, CancellationToken ct)
    {
        var admin = await config.GetAdminConfigAsync(ct);
        return Results.Ok(new SignupConfig(admin.EnableSignup, admin.DefaultUserRole));
    }

    private static async Task<IResult> UpdateSignupAsync(
        SignupConfig request, HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        var admin = await config.GetAdminConfigAsync(ct);
        var updated = admin with
        {
            EnableSignup = request.EnableSignup,
            DefaultUserRole = request.DefaultUserRole is "admin" or "user" or "pending"
                ? request.DefaultUserRole
                : admin.DefaultUserRole,
        };
        await config.SetAsync("admin.config", updated, ct);
        return Results.Ok(new SignupConfig(updated.EnableSignup, updated.DefaultUserRole));
    }

    private static async Task<IResult> GetApiKeyToggleAsync(ConfigService config, CancellationToken ct)
    {
        var admin = await config.GetAdminConfigAsync(ct);
        return Results.Ok(new FeatureToggle(admin.EnableApiKeys));
    }

    private static async Task<IResult> UpdateApiKeyToggleAsync(
        FeatureToggle request, HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        var admin = await config.GetAdminConfigAsync(ct);
        await config.SetAsync("admin.config", admin with { EnableApiKeys = request.Enabled }, ct);
        return Results.Ok(request);
    }

    private static async Task<IResult> GetChannelsToggleAsync(ConfigService config, CancellationToken ct) =>
        Results.Ok(new FeatureToggle(await config.GetAsync("features.channels", true, ct)));

    private static async Task<IResult> UpdateChannelsToggleAsync(
        FeatureToggle request, HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        await config.SetAsync("features.channels", request.Enabled, ct);
        return Results.Ok(request);
    }

    private static async Task<IResult> GetDirectConnectionsAsync(ConfigService config, CancellationToken ct) =>
        Results.Ok(new FeatureToggle(await config.GetAsync("features.direct_connections", false, ct)));

    private static async Task<IResult> UpdateDirectConnectionsAsync(
        FeatureToggle request, HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        await config.SetAsync("features.direct_connections", request.Enabled, ct);
        return Results.Ok(request);
    }

    private static async Task<IResult> GetCodeExecutionAsync(ConfigService config, CancellationToken ct) =>
        Results.Ok(new CodeExecutionConfig(
            await config.GetAsync("code_execution.engines", new List<string> { "pyodide" }, ct),
            await config.GetAsync("features.direct_connections", false, ct)));

    private static async Task<IResult> UpdateCodeExecutionAsync(
        CodeExecutionConfig request, HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        var engines = (request.Engines ?? [])
            .Where(e => e is "pyodide" or "jupyter").Distinct().ToList();
        await config.SetAsync("code_execution.engines", engines, ct);
        await config.SetAsync("features.direct_connections", request.DirectConnections, ct);
        return Results.Ok(new CodeExecutionConfig(engines, request.DirectConnections));
    }

    private static async Task<IResult> GetJwtExpiryAsync(ConfigService config, CancellationToken ct) =>
        Results.Ok(new JwtExpiryConfig(await config.GetAsync("webui.jwt.expires_in", "7d", ct)));

    private static async Task<IResult> UpdateJwtExpiryAsync(
        JwtExpiryConfig request, HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        if (JwtTokenService.ParseLifetime(request.ExpiresIn) is null)
        {
            return Results.BadRequest(new { detail = "Duração inválida (use 30m, 24h, 7d ou segundos)." });
        }
        await config.SetAsync("webui.jwt.expires_in", request.ExpiresIn.Trim(), ct);
        return Results.Ok(request);
    }

    // ---------------- Papéis de modelo (por usuário) ----------------

    /// <summary>GET /api/v1/configs/user/model-roles — cadeias por papel do usuário.</summary>
    private static async Task<IResult> GetUserModelRolesAsync(
        HttpContext http, AppDbContext db, ModelRoleResolver roles, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        return Results.Ok(new ModelRolesResponse(await roles.GetRolesAsync(user.Id, ct)));
    }

    /// <summary>PUT /api/v1/configs/user/model-roles — substitui o mapa;
    /// ids validados contra o catálogo de providers (400 em id/role inválido).</summary>
    private static async Task<IResult> UpdateUserModelRolesAsync(
        UpdateModelRolesRequest request, HttpContext http, AppDbContext db,
        ModelRoleResolver roles, ProviderService providers, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var payload = request.Roles ?? [];
        var unknownRole = payload.Keys
            .FirstOrDefault(k => !ModelRoleResolver.IsKnownRole(k));
        if (unknownRole is not null)
        {
            return Results.BadRequest(new { detail = $"Papel desconhecido: '{unknownRole}'." });
        }

        var ids = payload.Values.SelectMany(v => v ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count > 0)
        {
            var known = (await providers.ListModelsAsync(ct))
                .Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var invalid = ids.Where(id => !known.Contains(id)).ToList();
            if (invalid.Count > 0)
            {
                return Results.BadRequest(new
                {
                    detail = $"Modelo(s) não encontrado(s) no catálogo: {string.Join(", ", invalid)}.",
                });
            }
        }

        await roles.SetRolesAsync(user.Id, payload, ct);
        return Results.Ok(new ModelRolesResponse(await roles.GetRolesAsync(user.Id, ct)));
    }

    // ---------------- Rate limiting ----------------

    private static async Task<IResult> GetRateLimitAsync(ConfigService config, CancellationToken ct) =>
        Results.Ok(await config.GetAsync("ratelimit", RateLimitConfig.Default, ct));

    private static async Task<IResult> UpdateRateLimitAsync(
        RateLimitConfig request, HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        var sanitized = request with
        {
            PermitLimit = Math.Clamp(request.PermitLimit, 1, 100_000),
            WindowSeconds = Math.Clamp(request.WindowSeconds, 1, 86_400),
            LoginMaxFailures = Math.Clamp(request.LoginMaxFailures, 1, 1_000),
            LoginLockoutSeconds = Math.Clamp(request.LoginLockoutSeconds, 10, 86_400),
        };
        await config.SetAsync("ratelimit", sanitized, ct);
        return Results.Ok(sanitized);
    }

    private static Task<IResult> ResetLoginLockoutAsync(
        LoginLockoutResetRequest request, HttpContext http, RateLimitService limits)
    {
        if (!IsAdmin(http) || string.IsNullOrWhiteSpace(request.Email))
        {
            return Task.FromResult<IResult>(
                !IsAdmin(http) ? Results.Forbid() : Results.BadRequest(new { detail = "E-mail obrigatório." }));
        }
        var removed = limits.ResetLoginByEmail(request.Email.Trim().ToLowerInvariant());
        return Task.FromResult<IResult>(Results.Ok(new { status = true, removed }));
    }
}
