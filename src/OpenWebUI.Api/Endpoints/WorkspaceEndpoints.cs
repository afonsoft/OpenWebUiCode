using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de prompts, pastas, memórias e notas do workspace.</summary>
public static class WorkspaceEndpoints
{
    private const string ByIdRoute = "/{id}";
    /// <summary>Mapeia as rotas de workspace.</summary>
    public static void MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var prompts = app.MapGroup("/api/v1/prompts").RequireAuthorization()
            .RequirePermission(PermissionService.WorkspacePrompts);
        prompts.MapGet("/", ListPromptsAsync);
        prompts.MapGet("/list", ListPromptsAsync);
        prompts.MapPost("/create", CreatePromptAsync);
        prompts.MapGet("/command/{command}", GetPromptByCommandAsync);
        prompts.MapGet("/id/{id}", GetPromptAsync);
        prompts.MapPost("/id/{id}/update", UpdatePromptAsync);
        prompts.MapDelete("/id/{id}/delete", DeletePromptAsync);

        var folders = app.MapGroup("/api/v1/folders").RequireAuthorization();
        folders.MapGet("/", ListFoldersAsync);
        folders.MapPost("/", CreateFolderAsync);
        folders.MapGet(ByIdRoute, GetFolderAsync);
        folders.MapPost("/{id}/update", UpdateFolderAsync);
        folders.MapDelete(ByIdRoute, DeleteFolderAsync);

        var memories = app.MapGroup("/api/v1/memories").RequireAuthorization();
        memories.MapGet("/", ListMemoriesAsync);
        memories.MapPost("/add", AddMemoryAsync);
        memories.MapPost("/{id}/update", UpdateMemoryAsync);
        memories.MapDelete(ByIdRoute, DeleteMemoryAsync);
        memories.MapDelete("/delete/user", DeleteAllMemoriesAsync);

        var notes = app.MapGroup("/api/v1/notes").RequireAuthorization();
        notes.MapGet("/", ListNotesAsync);
        notes.MapPost("/create", CreateNoteAsync);
        notes.MapGet(ByIdRoute, GetNoteAsync);
        notes.MapPost("/{id}/update", UpdateNoteAsync);
        notes.MapDelete("/{id}/delete", DeleteNoteAsync);
        notes.MapPost("/{id}/access/update", UpdateNoteAccessAsync);
        notes.MapGet("/{id}/access", GetNoteAccessAsync);
    }

    // -------- Prompts --------

    private static async Task<IResult> ListPromptsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var prompts = await db.Prompts.AsNoTracking()
            .Where(p => p.UserId == user.Id)
            .OrderBy(p => p.Command)
            .ToListAsync(ct);

        return Results.Ok(prompts.Select(ToPromptResponse).ToList());
    }

    private static async Task<IResult> CreatePromptAsync(
        PromptUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var command = request.Command.TrimStart('/').Trim();
        if (command.Length == 0)
        {
            return Results.BadRequest(new { detail = "Comando é obrigatório." });
        }

        if (await db.Prompts.AnyAsync(p => p.UserId == user.Id && p.Command == command, ct))
        {
            return Results.BadRequest(new { detail = "Já existe um prompt com esse comando." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var prompt = new Prompt
        {
            UserId = user.Id,
            Command = command,
            Title = request.Title.Trim(),
            Content = request.Content,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Prompts.Add(prompt);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToPromptResponse(prompt));
    }

    private static async Task<IResult> GetPromptAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var prompt = await db.Prompts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == user!.Id, ct);
        return prompt is null ? Results.NotFound() : Results.Ok(ToPromptResponse(prompt));
    }

    private static async Task<IResult> GetPromptByCommandAsync(
        string command, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var prompt = await db.Prompts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Command == command && p.UserId == user!.Id, ct);
        return prompt is null ? Results.NotFound() : Results.Ok(ToPromptResponse(prompt));
    }

    private static async Task<IResult> UpdatePromptAsync(
        string id,
        PromptUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var prompt = await db.Prompts
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == user!.Id, ct);
        if (prompt is null)
        {
            return Results.NotFound();
        }

        prompt.Title = request.Title.Trim();
        prompt.Content = request.Content;
        prompt.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToPromptResponse(prompt));
    }

    private static async Task<IResult> DeletePromptAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var prompt = await db.Prompts
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == user!.Id, ct);
        if (prompt is null)
        {
            return Results.NotFound();
        }

        db.Prompts.Remove(prompt);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    // -------- Folders --------

    private static async Task<IResult> ListFoldersAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var folders = await db.Folders.AsNoTracking()
            .Where(f => f.UserId == user.Id)
            .OrderBy(f => f.Name)
            .ToListAsync(ct);

        var result = new List<object>();
        foreach (var folder in folders)
        {
            var items = await db.Chats.AsNoTracking()
                .Where(c => c.FolderId == folder.Id && !c.Archived)
                .OrderByDescending(c => c.UpdatedAt)
                .Select(c => new
                {
                    c.Id,
                    c.Title,
                    c.Pinned,
                    c.FolderId,
                    c.CreatedAt,
                    c.UpdatedAt,
                })
                .ToListAsync(ct);

            result.Add(new
            {
                folder.Id,
                folder.Name,
                folder.ParentId,
                folder.CreatedAt,
                folder.UpdatedAt,
                Items = new
                {
                    chats = items,
                },
            });
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateFolderAsync(
        FolderUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var folder = new Folder
        {
            UserId = user.Id,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Nova pasta" : request.Name.Trim(),
            ParentId = request.ParentId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new FolderResponse(folder.Id, folder.Name, folder.ParentId, folder.CreatedAt, folder.UpdatedAt));
    }

    private static async Task<IResult> GetFolderAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var folder = await db.Folders.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.UserId == user!.Id, ct);
        return folder is null
            ? Results.NotFound()
            : Results.Ok(new FolderResponse(folder.Id, folder.Name, folder.ParentId, folder.CreatedAt, folder.UpdatedAt));
    }

    private static async Task<IResult> UpdateFolderAsync(
        string id,
        FolderUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var folder = await db.Folders
            .FirstOrDefaultAsync(f => f.Id == id && f.UserId == user!.Id, ct);
        if (folder is null)
        {
            return Results.NotFound();
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            folder.Name = request.Name.Trim();
        }

        folder.ParentId = request.ParentId;
        folder.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        return Results.Ok(new FolderResponse(folder.Id, folder.Name, folder.ParentId, folder.CreatedAt, folder.UpdatedAt));
    }

    private static async Task<IResult> DeleteFolderAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var folder = await db.Folders
            .FirstOrDefaultAsync(f => f.Id == id && f.UserId == user!.Id, ct);
        if (folder is null)
        {
            return Results.NotFound();
        }

        // Remove os chats da pasta (sem excluí-los) e apaga a pasta.
        await db.Chats.Where(c => c.FolderId == id && c.UserId == user!.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FolderId, (string?)null), ct);

        db.Folders.Remove(folder);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    // -------- Memories --------

    private static async Task<IResult> ListMemoriesAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var memories = await db.Memories.AsNoTracking()
            .Where(m => m.UserId == user.Id)
            .OrderByDescending(m => m.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(memories.Select(m => new MemoryResponse(m.Id, m.Content, m.CreatedAt, m.UpdatedAt)).ToList());
    }

    private static async Task<IResult> AddMemoryAsync(
        MemoryUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return Results.BadRequest(new { detail = "Conteúdo é obrigatório." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var memory = new MemoryEntry
        {
            UserId = user.Id,
            Content = request.Content.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Memories.Add(memory);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new MemoryResponse(memory.Id, memory.Content, memory.CreatedAt, memory.UpdatedAt));
    }

    private static async Task<IResult> UpdateMemoryAsync(
        string id,
        MemoryUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var memory = await db.Memories
            .FirstOrDefaultAsync(m => m.Id == id && m.UserId == user!.Id, ct);
        if (memory is null)
        {
            return Results.NotFound();
        }

        memory.Content = request.Content.Trim();
        memory.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        return Results.Ok(new MemoryResponse(memory.Id, memory.Content, memory.CreatedAt, memory.UpdatedAt));
    }

    private static async Task<IResult> DeleteMemoryAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var memory = await db.Memories
            .FirstOrDefaultAsync(m => m.Id == id && m.UserId == user!.Id, ct);
        if (memory is null)
        {
            return Results.NotFound();
        }

        db.Memories.Remove(memory);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> DeleteAllMemoriesAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        await db.Memories.Where(m => m.UserId == user.Id).ExecuteDeleteAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    // -------- Notes --------

    private static async Task<IResult> ListNotesAsync(
        HttpContext http, AppDbContext db, AccessControlService access, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var all = await db.Notes.AsNoTracking()
            .OrderByDescending(n => n.UpdatedAt)
            .ToListAsync(ct);
        var groups = await access.GetGroupIdsAsync(user.Id, ct);
        var notes = access.FilterReadable(user, all, groups, n => n.UserId, n => n.AccessGrantsJson);
        return Results.Ok(notes.Select(n => new NoteResponse(n.Id, n.Title, n.Content, n.CreatedAt, n.UpdatedAt)).ToList());
    }

    private static async Task<IResult> CreateNoteAsync(
        NoteUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var note = new Note
        {
            UserId = user.Id,
            Title = string.IsNullOrWhiteSpace(request.Title) ? "Nova nota" : request.Title.Trim(),
            Content = request.Content,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new NoteResponse(note.Id, note.Title, note.Content, note.CreatedAt, note.UpdatedAt));
    }

    private static async Task<IResult> GetNoteAsync(
        string id, HttpContext http, AppDbContext db,
        AccessControlService access, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null
            || await access.LevelAsync(user!, note.UserId, note.AccessGrantsJson, ct)
                is AccessControlService.None)
        {
            return Results.NotFound();
        }
        return Results.Ok(new NoteResponse(note.Id, note.Title, note.Content, note.CreatedAt, note.UpdatedAt));
    }

    private static async Task<IResult> UpdateNoteAsync(
        string id,
        NoteUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        AccessControlService access,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null
            || await access.LevelAsync(user!, note.UserId, note.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound();
        }

        note.Title = string.IsNullOrWhiteSpace(request.Title) ? note.Title : request.Title.Trim();
        note.Content = request.Content;
        note.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        return Results.Ok(new NoteResponse(note.Id, note.Title, note.Content, note.CreatedAt, note.UpdatedAt));
    }

    private static async Task<IResult> DeleteNoteAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id && n.UserId == user!.Id, ct);
        if (note is null)
        {
            return Results.NotFound();
        }

        db.Notes.Remove(note);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> UpdateNoteAccessAsync(
        string id,
        [FromBody] AccessUpdateRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null || (note.UserId != user!.Id && user!.Role != UserRoles.Admin))
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

        note.AccessGrantsJson = AccessControlService.Serialize(request.AccessGrants);
        note.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true, access_grants = request.AccessGrants });
    }

    private static async Task<IResult> GetNoteAccessAsync(
        string id,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var note = await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null || (note.UserId != user!.Id && user!.Role != UserRoles.Admin))
        {
            return Results.NotFound();
        }

        return Results.Ok(new { access_grants = AccessControlService.Parse(note.AccessGrantsJson) });
    }

    private static PromptResponse ToPromptResponse(Prompt p) =>
        new(p.Id, p.Command, p.Title, p.Content, p.CreatedAt, p.UpdatedAt);
}
