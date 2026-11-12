using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>CRUD de combos de modelos com failover (SPEC-20261016-model-combos RF-005).</summary>
public static class ComboEndpoints
{
    private const string AdminRole = "admin";

    /// <summary>Deps dos endpoints de escrita — catálogo efetivo + DB.</summary>
    internal sealed record ComboDeps(
        AppDbContext Db, ProviderService Providers,
        PipelineClientService Pipelines, HybridCache Cache,
        ModelCapabilityResolver Capabilities);

    /// <summary>Mapeia as rotas de combos.</summary>
    public static RouteGroupBuilder MapComboEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/combos").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapGet("/{id}/status", StatusAsync);

        return group;
    }

    private static ComboResponse ToResponse(ModelCombo c) => new(
        c.Id, c.Name, c.UserId, c.Strategy, c.RequestsPerMinute,
        c.TokensPerMinute, c.Enabled,
        [.. c.Items.OrderBy(i => i.Position).Select(i => i.ModelId)],
        c.CreatedAt, c.UpdatedAt);

    private static string? Validate(ComboUpsertRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name))
        {
            return "Nome obrigatório.";
        }
        if (r.Strategy is not null && !ComboService.Strategies.Contains(r.Strategy))
        {
            return $"Strategy deve ser uma de: {string.Join(", ", ComboService.Strategies)}.";
        }
        if (r.Items.Count == 0 || r.Items.Any(i => string.IsNullOrWhiteSpace(i.ModelId)))
        {
            return "Items exige ao menos um modelo.";
        }
        if (r.RequestsPerMinute is < 1 || r.TokensPerMinute is < 1)
        {
            return "Limites por minuto devem ser >= 1.";
        }
        return null;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var isAdmin = user.Role == AdminRole;
        var combos = await db.ModelCombos.AsNoTracking()
            .Include(c => c.Items)
            // SPEC-20261104-model-visibility-gate RF-004: não-admin vê só os
            // próprios combos + globais habilitados; admin vê todos os globais.
            .Where(c => c.UserId == user.Id
                || (c.UserId == null && (isAdmin || c.Enabled)))
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
        return Results.Ok(combos.Select(ToResponse));
    }

    /// <summary>Ids do catálogo efetivo do usuário — a validação de itens do
    /// combo usa a mesma fonte do picker da UI (RF-004).</summary>
    private static async Task<HashSet<string>> EffectiveModelIdsAsync(
        User user, ComboDeps deps, CancellationToken ct) =>
        (await ApiEndpoints.GetCachedModelListAsync(
            user, deps.Providers, deps.Db, deps.Pipelines, deps.Cache,
            deps.Capabilities, ct))
            .Data.Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Itens fora do catálogo efetivo → detalhe do erro (null = ok).</summary>
    private static string? ValidateItemsCatalog(
        ComboUpsertRequest request, HashSet<string> catalogIds)
    {
        var invalid = request.Items
            .Select(i => i.ModelId.Trim())
            .FirstOrDefault(id => !catalogIds.Contains(id));
        return invalid is null
            ? null
            : $"Modelo '{invalid}' não está no catálogo disponível.";
    }

    private static async Task<IResult> CreateAsync(
        ComboUpsertRequest request,
        HttpContext http, [AsParameters] ComboDeps deps, CancellationToken ct)
    {
        var db = deps.Db;
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var error = Validate(request)
            ?? ValidateItemsCatalog(request, await EffectiveModelIdsAsync(
                user, deps, ct));
        if (error is not null)
        {
            return Results.BadRequest(new { detail = error });
        }

        var ownerId = request.Global == true ? null : user.Id;
        if (ownerId is null && user.Role != AdminRole)
        {
            return Results.Json(new { detail = "Combos globais exigem admin." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var name = request.Name.Trim();
        var dup = await db.ModelCombos.AnyAsync(
            c => c.Name == name && c.UserId == ownerId, ct);
        if (dup)
        {
            return Results.Conflict(new { detail = "Já existe um combo com esse nome." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var combo = new ModelCombo
        {
            UserId = ownerId,
            Name = name,
            Strategy = request.Strategy ?? "priority",
            RequestsPerMinute = request.RequestsPerMinute,
            TokensPerMinute = request.TokensPerMinute,
            Enabled = request.Enabled ?? true,
            CreatedAt = now,
            UpdatedAt = now,
            Items = [.. request.Items.Select((item, index) => new ModelComboItem
            {
                ModelId = item.ModelId.Trim(),
                Position = item.Position ?? index,
            })],
        };
        db.ModelCombos.Add(combo);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/combos/{combo.Id}", ToResponse(combo));
    }

    private static async Task<IResult> GetAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, combo) = await FindAsync(id, http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        return combo is null ? Results.NotFound() : Results.Ok(ToResponse(combo));
    }

    private static async Task<IResult> UpdateAsync(
        string id, ComboUpsertRequest request,
        HttpContext http, [AsParameters] ComboDeps deps, CancellationToken ct)
    {
        var db = deps.Db;
        var (user, combo) = await FindAsync(id, http, db, ct, write: true);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (combo is null)
        {
            return Results.NotFound();
        }
        var error = Validate(request)
            ?? ValidateItemsCatalog(request, await EffectiveModelIdsAsync(
                user, deps, ct));
        if (error is not null)
        {
            return Results.BadRequest(new { detail = error });
        }

        var name = request.Name.Trim();
        if (await db.ModelCombos.AnyAsync(
                c => c.Name == name && c.UserId == combo.UserId && c.Id != combo.Id, ct))
        {
            return Results.Conflict(new { detail = "Já existe um combo com esse nome." });
        }

        combo.Name = name;
        combo.Strategy = request.Strategy ?? combo.Strategy;
        combo.RequestsPerMinute = request.RequestsPerMinute;
        combo.TokensPerMinute = request.TokensPerMinute;
        combo.Enabled = request.Enabled ?? combo.Enabled;
        combo.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        combo.Items = [.. request.Items.Select((item, index) => new ModelComboItem
        {
            ComboId = combo.Id,
            ModelId = item.ModelId.Trim(),
            Position = item.Position ?? index,
        })];
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(combo));
    }

    private static async Task<IResult> DeleteAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, combo) = await FindAsync(id, http, db, ct, write: true);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (combo is null)
        {
            return Results.NotFound();
        }

        db.ModelCombos.Remove(combo);
        await db.SaveChangesAsync(ct);
        return Results.Ok(true);
    }

    private static async Task<IResult> StatusAsync(
        string id, HttpContext http, AppDbContext db,
        ComboService combos, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var status = await combos.GetStatusAsync(id, user.Id, ct);
        return status is null
            ? Results.NotFound()
            : Results.Ok(new ComboStatusResponse(
                status.ComboId, status.EffectiveModel,
                [.. status.Cooldowns.Select(c => new ComboCooldownResponse(c.ModelId, c.Until))],
                status.WindowRequests, status.WindowTokens));
    }

    /// <summary>
    /// Dono do combo ou admin (leitura aceita combos globais <c>UserId==null</c>;
    /// escrita exige dono ou admin).
    /// </summary>
    private static async Task<(User?, ModelCombo?)> FindAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct,
        bool write = false)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null);
        }

        var query = db.ModelCombos.Include(c => c.Items).AsQueryable();
        var combo = await query.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (combo is null)
        {
            return (user, null);
        }

        // Global desabilitado não é legível por não-admin (mesmo escopo da lista).
        var canRead = combo.UserId == user.Id
            || (combo.UserId is null && (combo.Enabled || user.Role == AdminRole));
        var canWrite = combo.UserId == user.Id
            || (combo.UserId is null && user.Role == AdminRole);
        var allowed = write ? canWrite : canRead;
        return (user, allowed ? combo : null);
    }
}
