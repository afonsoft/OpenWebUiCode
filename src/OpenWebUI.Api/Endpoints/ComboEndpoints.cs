using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>CRUD de combos de modelos com failover (SPEC-20261016-model-combos RF-005).</summary>
public static class ComboEndpoints
{
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

        var combos = await db.ModelCombos.AsNoTracking()
            .Include(c => c.Items)
            .Where(c => c.UserId == user.Id || c.UserId == null)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
        return Results.Ok(combos.Select(ToResponse));
    }

    private static async Task<IResult> CreateAsync(
        ComboUpsertRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var error = Validate(request);
        if (error is not null)
        {
            return Results.BadRequest(new { detail = error });
        }

        var ownerId = request.Global == true ? null : user.Id;
        if (ownerId is null && user.Role != "admin")
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
        HttpContext http, AppDbContext db, CancellationToken ct)
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
        var error = Validate(request);
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

        var canRead = combo.UserId is null || combo.UserId == user.Id;
        var canWrite = combo.UserId == user.Id
            || (combo.UserId is null && user.Role == "admin");
        var allowed = write ? canWrite : canRead;
        return (user, allowed ? combo : null);
    }
}
