using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de modelos personalizados, espelhando <c>/api/v1/models</c> do Open WebUI.</summary>
public static class ModelEndpoints
{
    /// <summary>Mapeia as rotas de modelos do workspace.</summary>
    public static RouteGroupBuilder MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/models")
            .RequireAuthorization()
            .AddEndpointFilter(ApiEndpoints.InvalidateModelListCacheAsync);

        // Leitura fica aberta (chat precisa listar modelos para todos os usuários);
        // workspace.models gateia só a gestão no workspace.
        group.MapGet("/", ListModelsAsync);
        group.MapGet("/list", ListModelsAsync);
        group.MapGet("/all", ListCatalogAsync);
        group.MapPost("/create", CreateModelAsync)
            .RequirePermission(PermissionService.WorkspaceModels);
        group.MapGet("/model", GetModelByQueryAsync);
        group.MapPost("/model/update", UpdateModelAsync)
            .RequirePermission(PermissionService.WorkspaceModels);
        group.MapPost("/model/delete", DeleteModelAsync)
            .RequirePermission(PermissionService.WorkspaceModels);
        group.MapPost("/model/toggle", ToggleModelAsync)
            .RequirePermission(PermissionService.WorkspaceModels);
        group.MapGet("/model/access", GetAccessAsync);
        group.MapPost("/model/access/update", UpdateAccessAsync)
            .RequirePermission(PermissionService.WorkspaceModels);
        group.MapGet("/export", ExportModelsAsync);
        group.MapPost("/import", ImportModelsAsync)
            .RequirePermission(PermissionService.WorkspaceModels);

        return group;
    }

    /// <summary>Catálogo completo de modelos com a flag de visibilidade do
    /// usuário chamador — alimenta a aba Models das settings (qualquer role).</summary>
    private static async Task<IResult> ListCatalogAsync(
        HttpContext http, [AsParameters] ModelCatalogDeps deps, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, deps.Db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var response = await ApiEndpoints.GetCachedModelListAsync(
            user, deps.Providers, deps.Db, deps.Pipelines, deps.Cache,
            deps.Capabilities, ct);
        var disabled = ApiEndpoints.ParseDisabledModels(user.SettingsJson);
        var providerRules = await deps.Policies.LoadAsync(PolicyService.ActionProviderUse, ct);
        var modelRules = await deps.Policies.LoadAsync(PolicyService.ActionModelUse, ct);
        var data = response.Data
            .Where(m => PolicyService.MatchIn(providerRules,
                    new[] { m.Provider, $"{m.Provider}/{m.Id}" }) is null
                && PolicyService.MatchIn(modelRules,
                    new[] { m.Id, m.Name ?? "", $"{m.Provider}/{m.Id}" }) is null)
            .Select(m => new CatalogModel(
                m.Id, m.Name, m.Provider, m.OwnedBy,
                !disabled.Contains($"{m.Provider}:{m.Id}"), m.Capabilities)).ToList();
        return Results.Ok(new CatalogModelListResponse(data));
    }

    private static async Task<IResult> ListModelsAsync(
        HttpContext http, AppDbContext db, AccessControlService access,
        PolicyService policies, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var all = await db.ModelEntries.AsNoTracking()
            .OrderBy(m => m.Name)
            .ToListAsync(ct);
        var modelRules = await policies.LoadAsync(PolicyService.ActionModelUse, ct);
        var groups = await access.GetGroupIdsAsync(user.Id, ct);
        var models = all.Where(m =>
            (m.UserId == "public"
            || access.Level(user, m.UserId, m.AccessGrantsJson, groups) is not AccessControlService.None)
            && PolicyService.MatchIn(modelRules,
                new[] { m.Name, m.BaseModelId ?? "", m.Id }) is null);

        return Results.Ok(models.Select(ToResponse).ToList());
    }

    private static async Task<IResult> CreateModelAsync(
        ModelEntryUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var arena = ParseArenaMeta(request.MetaJson);
        if (string.IsNullOrWhiteSpace(request.Name)
            || (arena is null && string.IsNullOrWhiteSpace(request.BaseModelId)))
        {
            return Results.BadRequest(new { detail = "Nome e modelo base são obrigatórios." });
        }
        var filterError = ModelFilterService.Validate(request.MetaJson);
        if (filterError is not null)
        {
            return Results.BadRequest(new { detail = filterError });
        }
        if (arena is not null && arena.Value.ModelIds.Count < 2)
        {
            return Results.BadRequest(
                new { detail = "Modelo arena exige ao menos 2 modelos concorrentes." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entry = new ModelEntry
        {
            UserId = user.Id,
            Name = request.Name.Trim(),
            BaseModelId = string.IsNullOrWhiteSpace(request.BaseModelId)
                ? null : request.BaseModelId.Trim(),
            SystemPrompt = request.SystemPrompt,
            ParamsJson = request.ParamsJson,
            ProfileImageUrl = request.ProfileImageUrl,
            MetaJson = request.MetaJson,
            AccessGrantsJson = request.AccessGrantsJson,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ModelEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(entry));
    }

    private static async Task<IResult> GetModelByQueryAsync(
        HttpContext http, AppDbContext db, AccessControlService access,
        CancellationToken ct, string? id = null)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var model = await db.ModelEntries.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        var readable = model is not null
            && (model.UserId == "public"
                || await access.LevelAsync(user!, model.UserId, model.AccessGrantsJson, ct)
                    is not AccessControlService.None);
        return readable ? Results.Ok(ToResponse(model!)) : Results.NotFound();
    }

    private static async Task<IResult> UpdateModelAsync(
        ModelUpdateRequest request,
        HttpContext http,
        AppDbContext db,
        AccessControlService access,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var model = await db.ModelEntries
            .FirstOrDefaultAsync(m => m.Id == request.Id, ct);
        if (model is null
            || await access.LevelAsync(user!, model.UserId, model.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound();
        }

        var updateFilterError = ModelFilterService.Validate(request.MetaJson);
        if (updateFilterError is not null)
        {
            return Results.BadRequest(new { detail = updateFilterError });
        }

        model.Name = string.IsNullOrWhiteSpace(request.Name) ? model.Name : request.Name.Trim();
        model.BaseModelId = string.IsNullOrWhiteSpace(request.BaseModelId) ? model.BaseModelId : request.BaseModelId.Trim();
        model.SystemPrompt = request.SystemPrompt;
        model.ParamsJson = request.ParamsJson;
        model.ProfileImageUrl = request.ProfileImageUrl;
        model.MetaJson = request.MetaJson ?? model.MetaJson;
        model.AccessGrantsJson = request.AccessGrantsJson ?? model.AccessGrantsJson;
        model.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(model));
    }

    private static async Task<IResult> ToggleModelAsync(
        ToggleRequest request,
        HttpContext http,
        AppDbContext db,
        AccessControlService access,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var model = await db.ModelEntries
            .FirstOrDefaultAsync(m => m.Id == request.Id, ct);
        if (model is null
            || await access.LevelAsync(user!, model.UserId, model.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound();
        }

        model.IsActive = !model.IsActive;
        model.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(model));
    }

    private static async Task<IResult> DeleteModelAsync(
        ToggleRequest request,
        HttpContext http,
        AppDbContext db,
        AccessControlService access,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var model = await db.ModelEntries
            .FirstOrDefaultAsync(m => m.Id == request.Id, ct);
        if (model is null
            || await access.LevelAsync(user!, model.UserId, model.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound();
        }

        db.ModelEntries.Remove(model);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> ExportModelsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var models = await db.ModelEntries.AsNoTracking()
            .Where(m => m.UserId == user.Id)
            .ToListAsync(ct);

        return Results.Ok(models.Select(ToResponse).ToList());
    }

    private static async Task<IResult> ImportModelsAsync(
        List<ModelEntryUpsertRequest> request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        foreach (var item in request)
        {
            var importFilterError = ModelFilterService.Validate(item.MetaJson);
            if (importFilterError is not null)
            {
                return Results.BadRequest(
                    new { detail = $"Modelo '{item.Name}': {importFilterError}" });
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var item in request)
        {
            db.ModelEntries.Add(new ModelEntry
            {
                UserId = user.Id,
                Name = item.Name.Trim(),
                BaseModelId = string.IsNullOrWhiteSpace(item.BaseModelId)
                    ? null : item.BaseModelId.Trim(),
                SystemPrompt = item.SystemPrompt,
                ParamsJson = item.ParamsJson,
                ProfileImageUrl = item.ProfileImageUrl,
                MetaJson = item.MetaJson,
                AccessGrantsJson = item.AccessGrantsJson,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    internal static ModelEntryResponse ToResponse(ModelEntry m) => new(
        m.Id, m.Name, m.BaseModelId, m.SystemPrompt, m.ParamsJson, m.ProfileImageUrl,
        m.IsActive, m.CreatedAt, m.UpdatedAt, m.MetaJson, m.AccessGrantsJson);

    private static async Task<IResult> GetAccessAsync(
        string id,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var model = await db.ModelEntries.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is null || (model.UserId != user.Id && user.Role != UserRoles.Admin))
        {
            return Results.NotFound();
        }

        return Results.Ok(new { access_grants = AccessControlService.Parse(model.AccessGrantsJson) });
    }

    private static async Task<IResult> UpdateAccessAsync(
        string id,
        [FromBody] AccessUpdateRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var model = await db.ModelEntries.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is null || (model.UserId != user.Id && user.Role != UserRoles.Admin))
        {
            return Results.NotFound();
        }
        if (request.AccessGrants.Any(g =>
                g.PrincipalType is not ("user" or "group")
                || string.IsNullOrWhiteSpace(g.PrincipalId)
                || g.Permission is not ("read" or "write")))
        {
            return Results.BadRequest(new { detail = "Grant inválido." });
        }

        model.AccessGrantsJson = AccessControlService.Serialize(request.AccessGrants);
        model.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true, access_grants = request.AccessGrants });
    }

    /// <summary>Metadados de modelo arena extraídos do MetaJson.</summary>
    internal record struct ArenaMeta(bool Arena, List<string> ModelIds);

    /// <summary>Interpreta MetaJson como config arena; null quando não é arena ou inválido.</summary>
    internal static ArenaMeta? ParseArenaMeta(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson))
        {
            return null;
        }
        try
        {
            var meta = JsonSerializer.Deserialize<JsonElement>(metaJson);
            if (!meta.TryGetProperty("arena", out var flag) || flag.ValueKind != JsonValueKind.True)
            {
                return null;
            }
            var ids = new List<string>();
            if (meta.TryGetProperty("model_ids", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                ids = arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(idStr => !string.IsNullOrWhiteSpace(idStr))
                    .ToList();
            }
            return new ArenaMeta(true, ids);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Verifica o acesso do usuário a um modelo via AccessGrantsJson.
    /// Sem grants vale a visibilidade padrão (dono ou "public"); com grants é
    /// preciso casar principal user/group/* com permission read ou write.
    /// </summary>
    internal static async Task<bool> HasModelAccessAsync(
        User user, ModelEntry model, AppDbContext db, CancellationToken ct)
    {
        if (model.UserId == user.Id || user.Role == "admin")
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(model.AccessGrantsJson))
        {
            return true;
        }

        List<AccessGrantEntry> grants;
        try
        {
            grants = JsonSerializer.Deserialize<List<AccessGrantEntry>>(model.AccessGrantsJson) ?? [];
        }
        catch (JsonException)
        {
            return false;
        }
        if (grants.Count == 0)
        {
            return true;
        }

        var groupIds = (await db.GroupMembers.AsNoTracking()
            .Where(m => m.UserId == user.Id)
            .Select(m => m.GroupId)
            .ToListAsync(ct)).ToHashSet();

        return grants.Any(g =>
        {
            if (g.Permission is not ("read" or "write"))
            {
                return false;
            }
            return g.PrincipalType == "*"
                || (g.PrincipalType == "user" && g.PrincipalId == user.Id)
                || (g.PrincipalType == "group"
                    && g.PrincipalId is { } gid && groupIds.Contains(gid));
        });
    }

    internal sealed record AccessGrantEntry(
        [property: JsonPropertyName("principal_type")] string PrincipalType,
        [property: JsonPropertyName("principal_id")] string? PrincipalId,
        string Permission);

    /// <summary>Referência a um modelo por id (toggle/delete).</summary>
    public sealed record ToggleRequest(string Id);

    /// <summary>Atualização de modelo personalizado.</summary>
    public sealed record ModelUpdateRequest(
        string Id, string? Name, string? BaseModelId, string? SystemPrompt,
        string? ParamsJson, string? ProfileImageUrl,
        string? MetaJson = null, string? AccessGrantsJson = null);

    /// <summary>Bundle DI do catálogo de modelos — mantém a assinatura do
    /// endpoint dentro do limite de parâmetros.</summary>
    internal sealed record ModelCatalogDeps(
        ProviderService Providers,
        AppDbContext Db,
        PipelineClientService Pipelines,
        HybridCache Cache,
        ModelCapabilityResolver Capabilities,
        PolicyService Policies);
}
