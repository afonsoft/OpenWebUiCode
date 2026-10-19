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

        // Camadas de config (SPEC-20261014-config-layers): defaults globais
        // do admin + overrides por usuário.
        group.MapGet("/model-roles", GetGlobalModelRolesAsync);
        group.MapPut("/model-roles", UpdateGlobalModelRolesAsync);
        group.MapGet("/github", GetGlobalGitHubAsync);
        group.MapPut("/github", SetGlobalGitHubAsync);
        group.MapDelete("/github", ClearGlobalGitHubAsync);
        group.MapGet("/user_provider_keys", GetUserProviderKeysToggleAsync);
        group.MapPost("/user_provider_keys", UpdateUserProviderKeysToggleAsync);
        group.MapGet("/user/provider-keys", GetUserProviderKeysAsync);
        group.MapPut("/user/provider-keys/{provider}", SetUserProviderKeyAsync);
        group.MapDelete("/user/provider-keys/{provider}", ClearUserProviderKeyAsync);
        // Auto-sync de memória (SPEC-20261014-memory-pipeline RF-004):
        // usuário ajusta seus overrides; admin fixa a camada global
        // (`memsync.enabled=false` global é negativa absoluta de custo).
        group.MapGet("/user/memory-sync", GetUserMemorySyncAsync);
        group.MapPut("/user/memory-sync", UpdateUserMemorySyncAsync);

        // Overrides do loop do agente por usuário (SPEC-20261019-
        // run-autonomy-continue RF-004) — u:{id}:agentLoop.* sobrescreve
        // o agentLoop.* global; null na camada do usuário = herda.
        group.MapGet("/user/agent-loop", GetUserAgentLoopAsync);
        group.MapPut("/user/agent-loop", UpdateUserAgentLoopAsync);
        group.MapPut("/memory-sync", UpdateGlobalMemorySyncAsync);

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
        return Results.Ok(new ModelRolesResponse(
            await roles.GetRolesAsync(user.Id, ct),
            await roles.GetGlobalRolesAsync(ct)));
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
        return Results.Ok(new ModelRolesResponse(
            await roles.GetRolesAsync(user.Id, ct),
            await roles.GetGlobalRolesAsync(ct)));
    }

    // ---------------- Camadas globais (admin) ----------------

    /// <summary>GET /api/v1/configs/model-roles — cadeias globais por papel (admin).</summary>
    private static async Task<IResult> GetGlobalModelRolesAsync(
        HttpContext http, ModelRoleResolver roles, CancellationToken ct) =>
        IsAdmin(http)
            ? Results.Ok(new ModelRolesResponse(await roles.GetGlobalRolesAsync(ct)))
            : Results.Forbid();

    /// <summary>PUT /api/v1/configs/model-roles — substitui as cadeias globais (admin).</summary>
    private static async Task<IResult> UpdateGlobalModelRolesAsync(
        UpdateModelRolesRequest request, HttpContext http,
        ModelRoleResolver roles, ProviderService providers, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
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

        await roles.SetGlobalRolesAsync(payload, ct);
        return Results.Ok(new ModelRolesResponse(await roles.GetGlobalRolesAsync(ct)));
    }

    /// <summary>GET /api/v1/configs/github — status do PAT global (admin; valor nunca exposto).</summary>
    private static async Task<IResult> GetGlobalGitHubAsync(
        HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        var configured = await config.GetAsync<string?>(
            GitHubService.GlobalTokenKey, null, ct) is { Length: > 0 };
        var login = configured
            ? await config.GetAsync<string?>("github.login", null, ct)
            : null;
        return Results.Ok(new GitHubConfigResponse(configured, login, "global"));
    }

    /// <summary>PUT /api/v1/configs/github — valida e grava o PAT default global (admin).</summary>
    private static async Task<IResult> SetGlobalGitHubAsync(
        GitHubTokenRequest request, HttpContext http,
        GitHubService github, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return Results.BadRequest(new { detail = "Token é obrigatório." });
        }
        var login = await github.SetGlobalTokenAsync(request.Token.Trim(), ct);
        return login is null
            ? Results.BadRequest(new { detail = "Token inválido ou GitHub indisponível." })
            : Results.Ok(new GitHubConfigResponse(true, login, "global"));
    }

    /// <summary>DELETE /api/v1/configs/github — remove o PAT default global (admin).</summary>
    private static async Task<IResult> ClearGlobalGitHubAsync(
        HttpContext http, GitHubService github, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        await github.ClearGlobalTokenAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    // ---------------- BYOK — chaves de provider por usuário ----------------

    /// <summary>GET /api/v1/configs/user_provider_keys — flag BYOK (admin, default off).</summary>
    private static async Task<IResult> GetUserProviderKeysToggleAsync(
        ConfigService config, CancellationToken ct) =>
        Results.Ok(new FeatureToggle(
            await config.GetAsync("features.userProviderKeys", false, ct)));

    /// <summary>POST /api/v1/configs/user_provider_keys — liga/desliga BYOK (admin).</summary>
    private static async Task<IResult> UpdateUserProviderKeysToggleAsync(
        FeatureToggle request, HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        await config.SetAsync("features.userProviderKeys", request.Enabled, ct);
        return Results.Ok(new FeatureToggle(request.Enabled));
    }

    /// <summary>
    /// GET /api/v1/configs/user/provider-keys — provedores com chave do
    /// usuário (RF-005). Expõe só <c>hasKey</c> + flag global — nunca valores.
    /// </summary>
    private static async Task<IResult> GetUserProviderKeysAsync(
        HttpContext http, AppDbContext db, ConfigService config, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var enabled = await config.GetAsync("features.userProviderKeys", false, ct);
        var keys = await config.GetAsync<Dictionary<string, string>?>(
            UserConfigOverlayService.ProviderKeysKey(user.Id), null, ct) ?? [];
        return Results.Ok(new UserProviderKeysResponse(
            enabled,
            keys.Where(kv => !string.IsNullOrEmpty(kv.Value))
                .Select(kv => kv.Key).OrderBy(p => p).ToList()));
    }

    /// <summary>PUT /api/v1/configs/user/provider-keys/{provider} — grava a chave BYOK.</summary>
    private static async Task<IResult> SetUserProviderKeyAsync(
        string provider, UserProviderKeyRequest request,
        HttpContext http, AppDbContext db, ConfigService config, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (!await config.GetAsync("features.userProviderKeys", false, ct))
        {
            return Results.BadRequest(new
            {
                detail = "Chaves de provider por usuário estão desabilitadas pelo admin.",
            });
        }
        if (!UserConfigOverlayService.IsByokProvider(provider))
        {
            return Results.BadRequest(new { detail = $"Provider '{provider}' não aceita chave de usuário." });
        }
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            return Results.BadRequest(new { detail = "key é obrigatória." });
        }

        var keys = await config.GetAsync<Dictionary<string, string>?>(
            UserConfigOverlayService.ProviderKeysKey(user.Id), null, ct) ?? [];
        keys[provider.ToLowerInvariant()] = request.Key.Trim();
        await config.SetAsync(
            UserConfigOverlayService.ProviderKeysKey(user.Id), keys, ct);
        return Results.Ok(new StatusResponse(true));
    }

    /// <summary>DELETE /api/v1/configs/user/provider-keys/{provider} — remove a chave BYOK.</summary>
    private static async Task<IResult> ClearUserProviderKeyAsync(
        string provider, HttpContext http, AppDbContext db,
        ConfigService config, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var keys = await config.GetAsync<Dictionary<string, string>?>(
            UserConfigOverlayService.ProviderKeysKey(user.Id), null, ct);
        if (keys is not null && keys.Remove(provider.ToLowerInvariant()))
        {
            await config.SetAsync(
                UserConfigOverlayService.ProviderKeysKey(user.Id), keys, ct);
        }
        return Results.Ok(new StatusResponse(true));
    }

    // ---------------- Auto-sync de memória (SPEC-20261014-memory-pipeline) ----------------

    /// <summary>GET /api/v1/configs/user/memory-sync — efetivo + camada global.</summary>
    private static async Task<IResult> GetUserMemorySyncAsync(
        HttpContext http, AppDbContext db, UserConfigOverlayService overlay,
        ConfigService config, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        return Results.Ok(await BuildMemorySyncResponseAsync(user.Id, overlay, config, ct));
    }

    /// <summary>PUT /api/v1/configs/user/memory-sync — override do usuário (null = herda).</summary>
    private static async Task<IResult> UpdateUserMemorySyncAsync(
        MemorySyncPrefs request, HttpContext http, AppDbContext db,
        UserConfigOverlayService overlay, ConfigService config, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (request.IntervalHours is < 1 or > 168
            || request.MaxAgeDays is < 1 or > 365)
        {
            return Results.BadRequest(new
            {
                detail = "intervalHours ∈ [1,168] e maxAgeDays ∈ [1,365].",
            });
        }

        await overlay.SetUserAsync(user.Id, "memsync.enabled", request.Enabled, ct);
        await overlay.SetUserAsync(
            user.Id, "memsync.intervalHours", request.IntervalHours, ct);
        await overlay.SetUserAsync(
            user.Id, "memsync.maxAgeDays", request.MaxAgeDays, ct);
        return Results.Ok(await BuildMemorySyncResponseAsync(user.Id, overlay, config, ct));
    }

    /// <summary>PUT /api/v1/configs/memory-sync — camada global do admin.</summary>
    private static async Task<IResult> UpdateGlobalMemorySyncAsync(
        MemorySyncPrefs request, HttpContext http, ConfigService config,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        if (request.IntervalHours is < 1 or > 168
            || request.MaxAgeDays is < 1 or > 365)
        {
            return Results.BadRequest(new
            {
                detail = "intervalHours ∈ [1,168] e maxAgeDays ∈ [1,365].",
            });
        }
        await config.SetAsync("memsync.enabled", request.Enabled, ct);
        await config.SetAsync("memsync.intervalHours", request.IntervalHours, ct);
        await config.SetAsync("memsync.maxAgeDays", request.MaxAgeDays, ct);
        return Results.Ok(await ReadGlobalMemorySyncAsync(config, ct));
    }

    /// <summary>Monta o efetivo do usuário dobrando a negativa global do admin.</summary>
    private static async Task<MemorySyncResponse> BuildMemorySyncResponseAsync(
        string userId, UserConfigOverlayService overlay,
        ConfigService config, CancellationToken ct)
    {
        var global = await ReadGlobalMemorySyncAsync(config, ct);
        var adminDisabled = global.Enabled == false;
        // enabled efetivo: admin-deny é absoluto — quando desligado, false.
        var effEnabled = !adminDisabled
            && (await overlay.GetAsync(userId, "memsync.enabled", true, ct)).Value;
        var interval = Math.Clamp(
            (await overlay.GetAsync(
                userId, "memsync.intervalHours",
                MemorySyncService.DefaultIntervalHours, ct)).Value, 1, 168);
        var maxAge = Math.Clamp(
            (await overlay.GetAsync(
                userId, "memsync.maxAgeDays",
                MemorySyncService.DefaultMaxAgeDays, ct)).Value, 1, 365);
        var userLayer = new MemorySyncPrefs(
            await config.GetAsync<bool?>($"u:{userId}:memsync.enabled", null, ct),
            await config.GetAsync<int?>($"u:{userId}:memsync.intervalHours", null, ct),
            await config.GetAsync<int?>($"u:{userId}:memsync.maxAgeDays", null, ct));
        return new MemorySyncResponse(
            effEnabled, interval, maxAge, adminDisabled, global, userLayer);
    }

    /// <summary>Lê os valores crus da camada global (null = sem override).</summary>
    private static async Task<MemorySyncPrefs> ReadGlobalMemorySyncAsync(
        ConfigService config, CancellationToken ct) =>
        new(
            await config.GetAsync<bool?>("memsync.enabled", null, ct),
            await config.GetAsync<int?>("memsync.intervalHours", null, ct),
            await config.GetAsync<int?>("memsync.maxAgeDays", null, ct));

    // ---------------- Agent loop (SPEC-20261019-run-autonomy-continue RF-004) ----------------

    /// <summary>GET /api/v1/configs/user/agent-loop — efetivo + camadas cruas.</summary>
    private static async Task<IResult> GetUserAgentLoopAsync(
        HttpContext http, AppDbContext db, ConfigService config, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        return Results.Ok(await BuildAgentLoopResponseAsync(user.Id, config, ct));
    }

    /// <summary>PUT /api/v1/configs/user/agent-loop — override do usuário (null = herda o global).</summary>
    private static async Task<IResult> UpdateUserAgentLoopAsync(
        AgentLoopPrefs request, HttpContext http, AppDbContext db,
        UserConfigOverlayService overlay, ConfigService config, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (request.MaxRounds is < 1 or > 99999
            || request.MaxAutoChain is < 0 or > 10)
        {
            return Results.BadRequest(new
            {
                detail = "maxRounds ∈ [1,99999] e maxAutoChain ∈ [0,10].",
            });
        }
        await overlay.SetUserAsync(user.Id, "agentLoop.maxRounds", request.MaxRounds, ct);
        await overlay.SetUserAsync(user.Id, "agentLoop.autoChain", request.AutoChain, ct);
        await overlay.SetUserAsync(user.Id, "agentLoop.maxAutoChain", request.MaxAutoChain, ct);
        return Results.Ok(await BuildAgentLoopResponseAsync(user.Id, config, ct));
    }

    /// <summary>Monta o efetivo dobrando override do usuário sobre a camada global.</summary>
    private static async Task<AgentLoopResponse> BuildAgentLoopResponseAsync(
        string userId, ConfigService config, CancellationToken ct)
    {
        var global = new AgentLoopPrefs(
            await config.GetAsync<int?>("agentLoop.maxRounds", null, ct),
            await config.GetAsync<bool?>("agentLoop.autoChain", null, ct),
            await config.GetAsync<int?>("agentLoop.maxAutoChain", null, ct));
        var userLayer = new AgentLoopPrefs(
            await config.GetAsync<int?>($"u:{userId}:agentLoop.maxRounds", null, ct),
            await config.GetAsync<bool?>($"u:{userId}:agentLoop.autoChain", null, ct),
            await config.GetAsync<int?>($"u:{userId}:agentLoop.maxAutoChain", null, ct));
        return new AgentLoopResponse(
            MaxRounds: Math.Clamp(userLayer.MaxRounds ?? global.MaxRounds ?? 99999, 1, 99999),
            AutoChain: userLayer.AutoChain ?? global.AutoChain ?? true,
            MaxAutoChain: Math.Clamp(userLayer.MaxAutoChain ?? global.MaxAutoChain ?? 3, 0, 10),
            global, userLayer);
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
