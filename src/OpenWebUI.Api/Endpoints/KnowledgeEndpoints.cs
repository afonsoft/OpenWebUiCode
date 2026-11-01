using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de coleções Knowledge (/api/v1/knowledge) para RAG.</summary>
public static class KnowledgeEndpoints
{
    private const string CollectionNotFoundMessage = "Coleção não encontrada.";
    /// <summary>Mapeia o grupo /api/v1/knowledge.</summary>
    public static void MapKnowledgeEndpoints(this WebApplication app)
    {
        var knowledge = app.MapGroup("/api/v1/knowledge").RequireAuthorization()
            .RequirePermission(PermissionService.WorkspaceKnowledge);
        knowledge.MapGet("/", ListAsync);
        knowledge.MapPost("/", CreateAsync);
        knowledge.MapGet("/{id}", GetAsync);
        knowledge.MapPut("/{id}", UpdateAsync);
        knowledge.MapDelete("/{id}", DeleteAsync);
        knowledge.MapPost("/{id}/files", AddFileAsync);
        knowledge.MapPost("/{id}/file/add", AddFileAsync);
        knowledge.MapPost("/{id}/reindex", ReindexAsync);
        knowledge.MapPost("/batch/delete", BatchDeleteAsync);
        knowledge.MapDelete("/{id}/files/{fileId}", RemoveFileAsync);
        knowledge.MapPost("/{id}/access/update", UpdateAccessAsync);
        knowledge.MapGet("/{id}/access", GetAccessAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, AccessControlService access, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var all = await db.KnowledgeCollections.AsNoTracking()
            .OrderBy(k => k.Name)
            .ToListAsync(ct);
        var fileCounts = await db.KnowledgeFiles.AsNoTracking()
            .GroupBy(f => f.CollectionId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var groups = await access.GetGroupIdsAsync(user.Id, ct);
        var collections = access
            .FilterReadable(user, all, groups, k => k.UserId, k => k.AccessGrantsJson)
            .Select(k => new KnowledgeResponse(
                k.Id, k.Name, k.Description, fileCounts.GetValueOrDefault(k.Id), k.CreatedAt))
            .ToList();
        return Results.Ok(collections);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateKnowledgeRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { detail = "Nome é obrigatório." });
        }

        var name = request.Name.Trim();
        var exists = await db.KnowledgeCollections
            .AnyAsync(k => k.UserId == user.Id && k.Name == name, ct);
        if (exists)
        {
            return Results.Conflict(new { detail = "Já existe uma coleção com esse nome." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var collection = new KnowledgeCollection
        {
            UserId = user.Id,
            Name = name,
            Description = request.Description?.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.KnowledgeCollections.Add(collection);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new KnowledgeResponse(
            collection.Id, collection.Name, collection.Description, 0, collection.CreatedAt));
    }

    private static async Task<IResult> GetAsync(
        string id, HttpContext http, AppDbContext db,
        AccessControlService access, CancellationToken ct)
    {
        var (user, collection) = await LoadAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (collection is null
            || await access.LevelAsync(user, collection.UserId, collection.AccessGrantsJson, ct)
                is AccessControlService.None)
        {
            return Results.NotFound(new { detail = CollectionNotFoundMessage });
        }

        var files = await db.KnowledgeFiles.AsNoTracking()
            .Where(f => f.CollectionId == id)
            .Join(db.Files, k => k.FileId, f => f.Id,
                (k, f) => new KnowledgeFileResponse(k.Id, f.Id, f.Filename, k.AddedAt))
            .ToListAsync(ct);

        return Results.Ok(new KnowledgeDetailResponse(
            collection.Id, collection.Name, collection.Description,
            collection.CreatedAt, files));
    }

    private static async Task<IResult> UpdateAsync(
        string id, [FromBody] UpdateKnowledgeRequest request,
        HttpContext http, AppDbContext db,
        AccessControlService access, CancellationToken ct)
    {
        var (user, collection) = await LoadAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (collection is null
            || await access.LevelAsync(user, collection.UserId, collection.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound(new { detail = CollectionNotFoundMessage });
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            var name = request.Name.Trim();
            var exists = await db.KnowledgeCollections.AnyAsync(
                k => k.UserId == user.Id && k.Name == name && k.Id != id, ct);
            if (exists)
            {
                return Results.Conflict(new { detail = "Já existe uma coleção com esse nome." });
            }
            collection.Name = name;
        }
        if (request.Description is not null)
        {
            collection.Description = request.Description.Trim();
        }
        collection.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<IResult> DeleteAsync(
        string id, HttpContext http, AppDbContext db,
        AccessControlService access, CancellationToken ct)
    {
        var (user, collection) = await LoadAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (collection is null
            || await access.LevelAsync(user, collection.UserId, collection.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound(new { detail = CollectionNotFoundMessage });
        }

        db.KnowledgeCollections.Remove(collection);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<IResult> ReindexAsync(
        string id, HttpContext http, AppDbContext db, RagService rag,
        AccessControlService access, CancellationToken ct)
    {
        var (user, collection) = await LoadAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (collection is null
            || await access.LevelAsync(user, collection.UserId, collection.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound(new { detail = CollectionNotFoundMessage });
        }

        var fileIds = await db.KnowledgeFiles
            .Where(f => f.CollectionId == id)
            .Select(f => f.FileId)
            .ToListAsync(ct);
        var files = await db.Files.Where(f => fileIds.Contains(f.Id)).ToListAsync(ct);

        var indexed = 0;
        var failed = 0;
        foreach (var file in files)
        {
            if (await rag.IndexFileAsync(file, ct))
            {
                indexed++;
            }
            else
            {
                failed++;
            }
        }

        // Falha parcial/total do provider de embedding: 502 com contagem,
        // espelhando os demais endpoints dependentes de provider externo.
        if (failed > 0)
        {
            return Results.Json(
                new { status = false, indexed, failed },
                statusCode: StatusCodes.Status502BadGateway);
        }
        return Results.Ok(new { status = true, indexed, failed });
    }

    private static async Task<IResult> BatchDeleteAsync(
        [FromBody] BatchKnowledgeRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var isAdmin = http.User.IsInRole(UserRoles.Admin);
        var ids = request.Ids;
        var owned = await db.KnowledgeCollections
            .Where(k => ids.Contains(k.Id) && (isAdmin || k.UserId == user.Id))
            .ToListAsync(ct);
        if (owned.Count == 0)
        {
            return Results.Ok(new { deleted = 0 });
        }

        db.KnowledgeCollections.RemoveRange(owned);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { deleted = owned.Count });
    }

    private static async Task<IResult> AddFileAsync(
        string id, [FromBody] AddKnowledgeFileRequest request,
        HttpContext http, AppDbContext db, RagService rag,
        AccessControlService access, CancellationToken ct)
    {
        var (user, collection) = await LoadAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (collection is null
            || await access.LevelAsync(user, collection.UserId, collection.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound(new { detail = CollectionNotFoundMessage });
        }

        var file = await db.Files.FirstOrDefaultAsync(
            f => f.Id == request.FileId && f.UserId == user.Id, ct);
        if (file is null)
        {
            return Results.NotFound(new { detail = "Arquivo não encontrado." });
        }

        var exists = await db.KnowledgeFiles.AnyAsync(
            f => f.CollectionId == id && f.FileId == file.Id, ct);
        if (!exists)
        {
            db.KnowledgeFiles.Add(new KnowledgeFile
            {
                CollectionId = id,
                FileId = file.Id,
                AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            await db.SaveChangesAsync(ct);
        }

        // Indexação é best-effort: sem provider de embedding o arquivo entra
        // mesmo assim e o fallback de texto integral continua valendo.
        var indexed = await rag.IndexFileAsync(file, ct);
        return Results.Ok(new { status = true, indexed });
    }

    private static async Task<IResult> RemoveFileAsync(
        string id, string fileId,
        HttpContext http, AppDbContext db,
        AccessControlService access, CancellationToken ct)
    {
        var (user, collection) = await LoadAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (collection is null
            || await access.LevelAsync(user, collection.UserId, collection.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound(new { detail = CollectionNotFoundMessage });
        }

        var link = await db.KnowledgeFiles.FirstOrDefaultAsync(
            f => f.CollectionId == id && f.FileId == fileId, ct);
        if (link is null)
        {
            return Results.NotFound(new { detail = "Arquivo não está na coleção." });
        }

        db.KnowledgeFiles.Remove(link);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<(User? user, KnowledgeCollection? collection)> LoadAsync(
        HttpContext http, string id, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null);
        }

        var collection = await db.KnowledgeCollections.FirstOrDefaultAsync(
            k => k.Id == id, ct);
        return (user, collection);
    }

    private static async Task<IResult> UpdateAccessAsync(
        string id, [FromBody] AccessUpdateRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, collection) = await LoadAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (collection is null
            || (collection.UserId != user.Id && user.Role != UserRoles.Admin))
        {
            return Results.NotFound(new { detail = CollectionNotFoundMessage });
        }
        if (request.AccessGrants.Any(g =>
                g.PrincipalType is not ("user" or "group")
                || string.IsNullOrWhiteSpace(g.PrincipalId)
                || g.Permission is not ("read" or "write")))
        {
            return Results.BadRequest(new { detail = "Grant inválido." });
        }

        collection.AccessGrantsJson = AccessControlService.Serialize(request.AccessGrants);
        collection.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true, access_grants = request.AccessGrants });
    }

    private static async Task<IResult> GetAccessAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, collection) = await LoadAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (collection is null
            || (collection.UserId != user.Id && user.Role != UserRoles.Admin))
        {
            return Results.NotFound(new { detail = CollectionNotFoundMessage });
        }

        return Results.Ok(new
        {
            access_grants = AccessControlService.Parse(collection.AccessGrantsJson),
        });
    }
}
