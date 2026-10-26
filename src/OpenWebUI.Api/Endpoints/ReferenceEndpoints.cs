using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// CRUD de referências nomeadas (SPEC-20261018-references): diretórios
/// locais ou repositórios git cacheados, resolvidos via
/// <c>ref://alias/path</c> nas tools de arquivo quando anexadas com
/// <c>@alias</c> no composer. Somente-leitura para as tools; aliases são
/// únicos por usuário e seguem <c>[a-z0-9-]+</c>.
/// </summary>
public static class ReferenceEndpoints
{
    /// <summary>Mapeia os endpoints de referências.</summary>
    public static IEndpointRouteBuilder MapReferenceEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/references").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapPatch("/{id}", PatchAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapGet("/{alias}/tree", TreeAsync);
        group.MapPost("/{id}/refresh", RefreshAsync);
        return app;
    }

    /// <summary>DTO de entrada (create/patch).</summary>
    public sealed record ReferenceBody(
        string? Alias, string? Source, string? Branch,
        string? Description, bool? Hidden);

    /// <summary>View serializada de uma <see cref="ReferenceEntry"/>.</summary>
    private static object ToView(ReferenceEntry r) => new
    {
        id = r.Id,
        alias = r.Alias,
        source = r.Source,
        branch = r.Branch,
        description = r.Description,
        hidden = r.Hidden,
        lastFetchedAt = r.LastFetchedAt,
        updatedAt = r.UpdatedAt,
    };

    /// <summary>GET /api/v1/references?visible=true — lista do usuário
    /// (visíveis = menu @; ocultas ainda resolvem direto).</summary>
    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, ReferenceService refs,
        bool? visible, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var entries = await refs.ListAsync(user.Id, ct);
        if (visible == true)
        {
            entries = entries.Where(r => !r.Hidden).ToList();
        }
        return Results.Ok(entries.Select(ToView));
    }

    /// <summary>POST /api/v1/references — cria referência (alias único).</summary>
    private static async Task<IResult> CreateAsync(
        HttpContext http, AppDbContext db, ReferenceService refs,
        ReferenceBody body, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var alias = body.Alias?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(alias)
            || !ReferenceService.IsValidAlias(alias))
        {
            return Results.BadRequest(new
            {
                detail = "alias inválido — use [a-z0-9-]+.",
            });
        }
        if (string.IsNullOrWhiteSpace(body.Source))
        {
            return Results.BadRequest(new
            {
                detail = "source obrigatória — path local ou URL git.",
            });
        }

        var exists = await db.ReferenceEntries
            .AnyAsync(r => r.UserId == user.Id && r.Alias == alias, ct);
        if (exists)
        {
            return Results.Conflict(new
            {
                detail = $"alias '{alias}' já existe.",
            });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entry = new ReferenceEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = user.Id,
            Alias = alias,
            Source = body.Source.Trim(),
            Branch = string.IsNullOrWhiteSpace(body.Branch) ? null : body.Branch.Trim(),
            Description = body.Description?.Trim(),
            Hidden = body.Hidden == true,
            LastFetchedAt = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ReferenceEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        // Pré-resolve: git clona na hora (valida URL/branch); path local
        // só confirma acesso — falha não impede o create (SPEC: cache pode
        // ser preenchido depois).
        var resolved = await refs.ResolveAsync(user.Id, alias, ct);
        return Results.Created($"/api/v1/references/{entry.Id}", new
        {
            view = ToView(entry),
            ready = resolved is not null,
            warning = resolved?.Warning,
        });
    }

    /// <summary>PATCH /api/v1/references/{id} — atualiza campos mutáveis.</summary>
    private static async Task<IResult> PatchAsync(
        HttpContext http, AppDbContext db, string id,
        ReferenceBody body, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var entry = await db.ReferenceEntries
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == user.Id, ct);
        if (entry is null)
        {
            return Results.NotFound(new { detail = "referência não encontrada." });
        }

        if (body.Alias is not null
            && await TryApplyAliasAsync(db, user, entry, body.Alias, ct)
                is { } aliasError)
        {
            return aliasError;
        }
        if (body.Source is not null)
        {
            if (string.IsNullOrWhiteSpace(body.Source))
            {
                return Results.BadRequest(new { detail = "source não pode ser vazia." });
            }
            entry.Source = body.Source.Trim();
            entry.LastFetchedAt = 0; // origem nova → próximo resolve re-clona
        }
        if (body.Branch is not null)
        {
            entry.Branch = string.IsNullOrWhiteSpace(body.Branch) ? null : body.Branch.Trim();
            entry.LastFetchedAt = 0;
        }
        if (body.Description is not null)
        {
            entry.Description = string.IsNullOrWhiteSpace(body.Description)
                ? null : body.Description.Trim();
        }
        if (body.Hidden is not null)
        {
            entry.Hidden = body.Hidden.Value;
        }

        entry.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToView(entry));
    }

    /// <summary>Valida/aplica o novo alias; null quando aplicado (ou igual).</summary>
    private static async Task<IResult?> TryApplyAliasAsync(
        AppDbContext db, User user, ReferenceEntry entry,
        string raw, CancellationToken ct)
    {
        var alias = raw.Trim().ToLowerInvariant();
        if (!ReferenceService.IsValidAlias(alias))
        {
            return Results.BadRequest(new
            {
                detail = "alias inválido — use [a-z0-9-]+.",
            });
        }
        if (alias != entry.Alias && await db.ReferenceEntries.AnyAsync(
                r => r.UserId == user.Id && r.Alias == alias, ct))
        {
            return Results.Conflict(new { detail = $"alias '{alias}' já existe." });
        }
        entry.Alias = alias;
        return null;
    }

    /// <summary>DELETE /api/v1/references/{id} — remove a referência
    /// (cache git é limpo best-effort).</summary>
    private static async Task<IResult> DeleteAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var entry = await db.ReferenceEntries
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == user.Id, ct);
        if (entry is null)
        {
            return Results.NotFound(new { detail = "referência não encontrada." });
        }

        db.ReferenceEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { deleted = true });
    }

    /// <summary>GET /api/v1/references/{alias}/tree — resolve a referência
    /// e devolve o listing (cap 2000) como o agente veria.</summary>
    private static async Task<IResult> TreeAsync(
        HttpContext http, AppDbContext db, ReferenceService refs,
        string alias, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var resolved = await refs.ResolveAsync(user.Id, alias, ct);
        if (resolved is null)
        {
            return Results.NotFound(new
            {
                detail = $"referência '{alias}' indisponível.",
            });
        }

        var tree = await ReferenceService.ListTreeAsync(resolved, ct);
        return Results.Ok(new
        {
            alias = resolved.Entry.Alias,
            warning = resolved.Warning,
            paths = tree,
            truncated = tree.Count >= ReferenceService.MaxListingPaths,
        });
    }

    /// <summary>POST /api/v1/references/{id}/refresh — força re-clone/fetch
    /// na próxima resolução e já resolve (devolve warning se stale).</summary>
    private static async Task<IResult> RefreshAsync(
        HttpContext http, AppDbContext db, ReferenceService refs,
        string id, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var entry = await db.ReferenceEntries
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == user.Id, ct);
        if (entry is null)
        {
            return Results.NotFound(new { detail = "referência não encontrada." });
        }

        await refs.MarkStaleAsync(entry, ct);
        var resolved = await refs.ResolveAsync(user.Id, entry.Alias, ct);
        return Results.Ok(new
        {
            ready = resolved is not null,
            warning = resolved?.Warning,
            lastFetchedAt = resolved?.Entry.LastFetchedAt,
        });
    }
}
