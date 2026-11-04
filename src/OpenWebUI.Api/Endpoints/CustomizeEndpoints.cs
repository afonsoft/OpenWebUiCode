using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Hub Customize (SPEC-20261104-customize-hub): notas de conhecimento
/// org/pessoal (<c>/api/v1/customize/knowledge</c>), catálogo unificado de
/// skills (<c>/api/v1/customize/skills</c>) e operações admin
/// (<c>/api/v1/admin/skills/disabled|import</c>). Escopo:
/// <c>UserId == null</c> = global da organização; usuário comum só toca o
/// próprio escopo e desliga globais para si via tombstones.
/// </summary>
public static class CustomizeEndpoints
{
    private const int MaxNoteContent = 8000;
    private const string NoteNotFound = "Nota de conhecimento não encontrada.";
    private const string SkillNotFound = "Skill não encontrada.";

    /// <summary>Mapeia o grupo /api/v1/customize (autenticado) + admin skills.</summary>
    public static void MapCustomizeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/customize").RequireAuthorization();
        group.MapGet("/knowledge", ListKnowledgeAsync);
        group.MapPost("/knowledge", CreateKnowledgeAsync);
        group.MapPut("/knowledge/{id}", UpdateKnowledgeAsync);
        group.MapDelete("/knowledge/{id}", DeleteKnowledgeAsync);
        group.MapPut("/knowledge/{id}/enabled", SetKnowledgeEnabledAsync);
        group.MapGet("/skills", ListSkillsAsync);
        group.MapPost("/skills", CreateSkillAsync);
        group.MapPut("/skills/{id}", UpdateSkillAsync);
        group.MapDelete("/skills/{id}", DeleteSkillAsync);
        group.MapPut("/skills/{id}/enabled", SetSkillEnabledAsync);

        var admin = app.MapGroup("/api/v1/admin/skills").RequireAuthorization();
        admin.MapGet("/disabled", GetAdminDisabledSkillsAsync);
        admin.MapPut("/disabled", SetAdminDisabledSkillsAsync);
        admin.MapPost("/import", ImportSkillsAsync);
        app.MapPost("/api/v1/admin/plugins/import", ImportPluginAsync)
            .RequireAuthorization();
    }

    // ---------------- Knowledge notes ----------------

    /// <summary>Lista notas globais (habilitadas) + próprias, com escopo e toggle.</summary>
    private static async Task<IResult> ListKnowledgeAsync(
        HttpContext http, AppDbContext db,
        UserConfigOverlayService overlay, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var notes = await db.KnowledgeNotes.AsNoTracking()
            .Where(n => n.UserId == null || n.UserId == user.Id)
            .OrderBy(n => n.Title)
            .ToListAsync(ct);
        var disabled = await overlay.GetKnowledgeDisabledAsync(user.Id, ct);
        var isAdmin = user.Role == UserRoles.Admin;
        return Results.Ok(new
        {
            data = notes.Select(n => ToNoteResponse(n, user.Id, isAdmin, disabled)),
        });
    }

    /// <summary>Cria nota — <c>global:true</c> exige admin; usuário sempre pessoal.</summary>
    private static async Task<IResult> CreateKnowledgeAsync(
        [FromBody] KnowledgeNoteRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (ValidateNote(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }
        var global = request.Global && user.Role == UserRoles.Admin;
        if (request.Global && user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }
        var title = request.Title.Trim();
        var dup = await db.KnowledgeNotes.AnyAsync(n =>
            n.UserId == (global ? null : user.Id) && n.Title == title, ct);
        if (dup)
        {
            return Results.Conflict(new { detail = "Já existe uma nota com este título." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var note = new KnowledgeNote
        {
            UserId = global ? null : user.Id,
            Title = title,
            Trigger = request.Trigger?.Trim(),
            Content = request.Content.Trim(),
            Pinned = request.Pinned,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.KnowledgeNotes.Add(note);
        await db.SaveChangesAsync(ct);
        return Results.Created(
            $"/api/v1/customize/knowledge/{note.Id}",
            ToNoteResponse(note, user.Id, global, []));
    }

    /// <summary>Edita nota — dono da pessoal ou admin da global (admin não edita a de outro).</summary>
    private static async Task<IResult> UpdateKnowledgeAsync(
        string id, [FromBody] KnowledgeNoteRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var note = await FindNoteAsync(db, id, user, ct);
        if (note is null)
        {
            return Results.NotFound(new { detail = NoteNotFound });
        }
        if (ValidateNote(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }
        var title = request.Title.Trim();
        var dup = await db.KnowledgeNotes.AnyAsync(n =>
            n.Id != id && n.UserId == note.UserId && n.Title == title, ct);
        if (dup)
        {
            return Results.Conflict(new { detail = "Já existe uma nota com este título." });
        }

        note.Title = title;
        note.Trigger = request.Trigger?.Trim();
        note.Content = request.Content.Trim();
        note.Pinned = request.Pinned;
        note.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToNoteResponse(
            note, user.Id, user.Role == UserRoles.Admin, []));
    }

    /// <summary>Apaga nota — mesmo escopo de edição do PUT.</summary>
    private static async Task<IResult> DeleteKnowledgeAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var note = await FindNoteAsync(db, id, user, ct);
        if (note is null)
        {
            return Results.NotFound(new { detail = NoteNotFound });
        }
        db.KnowledgeNotes.Remove(note);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    /// <summary>
    /// Toggle em nota global grava tombstone <c>u:{id}:knowledge-disabled</c>;
    /// em nota própria o toggle não se aplica (a nota é do usuário — delete
    /// se não quiser) → 400.
    /// </summary>
    private static async Task<IResult> SetKnowledgeEnabledAsync(
        string id, [FromBody] CustomizeEnabledRequest request,
        HttpContext http, AppDbContext db,
        UserConfigOverlayService overlay, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var note = await db.KnowledgeNotes.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null
            || (note.UserId is not null && note.UserId != user.Id))
        {
            return Results.NotFound(new { detail = NoteNotFound });
        }
        if (note.UserId is not null)
        {
            return Results.BadRequest(new
            {
                detail = "Notas pessoais não têm toggle — edite ou apague a nota.",
            });
        }
        await overlay.SetKnowledgeDisabledAsync(user.Id, id, !request.Enabled, ct);
        return Results.Ok(new { status = true });
    }

    // ---------------- Skills catalog ----------------

    /// <summary>
    /// Catálogo unificado de skills (RF-006): DB globais + DB próprias +
    /// repo/global-scope do <see cref="SkillDiscoveryService"/> (scan sem
    /// workdir — só o escopo <c>~/.agents</c> etc., RF-007 da config-layers).
    /// </summary>
    private static async Task<IResult> ListSkillsAsync(
        HttpContext http, AppDbContext db,
        UserConfigOverlayService overlay, SkillDiscoveryService discovery,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var adminOff = await overlay.AdminDisabledSkillNamesAsync(ct);
        var hidden = await overlay.HiddenSkillNamesAsync(user.Id, ct);
        var dbSkills = await db.Skills.AsNoTracking()
            .Where(s => s.UserId == null || s.UserId == user.Id)
            .ToListAsync(ct);
        var isAdmin = user.Role == UserRoles.Admin;
        var myNames = dbSkills.Where(s => s.UserId == user.Id)
            .Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<SkillCatalogItem>();
        foreach (var s in dbSkills)
        {
            var global = s.UserId is null;
            if (global && myNames.Contains(s.Name))
            {
                continue;   // sombreado por skill pessoal de mesmo nome
            }
            var denied = adminOff.Contains(s.Name);
            var enabled = s.IsActive && !denied;
            items.Add(new SkillCatalogItem(
                s.Id, s.Name, s.Description,
                global ? "global" : "personal",
                enabled,
                enabled && !hidden.Contains(s.Name),
                denied,
                Editable: !global || isAdmin,
                Content: s.Content,
                AllowedTools: s.AllowedTools));
        }
        foreach (var s in discovery.Scan(workdir: null).Skills)
        {
            var denied = adminOff.Contains(s.Name);
            items.Add(new SkillCatalogItem(
                null, s.Name, s.Description, "repo",
                Enabled: !denied,
                EnabledForMe: !hidden.Contains(s.Name),
                DisabledByAdmin: denied,
                Editable: false,
                Content: null,
                AllowedTools: s.AllowedTools is { Count: > 0 }
                    ? string.Join(',', s.AllowedTools) : null));
        }
        return Results.Ok(new { data = items.OrderBy(i => i.Name).ToList() });
    }

    /// <summary>Cria skill DB — <c>global:true</c> exige admin.</summary>
    private static async Task<IResult> CreateSkillAsync(
        [FromBody] SkillUpsertRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (ValidateSkill(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }
        if (request.Global && user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }
        var global = request.Global;
        var name = request.Name.Trim();
        var dup = await db.Skills.AnyAsync(s =>
            s.UserId == (global ? null : user.Id) && s.Name == name, ct);
        if (dup)
        {
            return Results.Conflict(new { detail = "Já existe uma skill com este nome." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var skill = new Skill
        {
            UserId = global ? null : user.Id,
            Name = name,
            Description = request.Description?.Trim(),
            Content = request.Content.Trim(),
            AllowedTools = NormalizeAllowedTools(request.AllowedTools),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Skills.Add(skill);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/customize/skills/{skill.Id}",
            new SkillCatalogItem(skill.Id, skill.Name, skill.Description,
                global ? "global" : "personal", true, true, false, true,
                skill.Content, skill.AllowedTools));
    }

    /// <summary>Edita skill DB — dono da pessoal ou admin da global.</summary>
    private static async Task<IResult> UpdateSkillAsync(
        string id, [FromBody] SkillUpsertRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var skill = await FindSkillAsync(db, id, user, ct);
        if (skill is null)
        {
            return Results.NotFound(new { detail = SkillNotFound });
        }
        if (ValidateSkill(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }
        var name = request.Name.Trim();
        var dup = await db.Skills.AnyAsync(s =>
            s.Id != id && s.UserId == skill.UserId && s.Name == name, ct);
        if (dup)
        {
            return Results.Conflict(new { detail = "Já existe uma skill com este nome." });
        }

        skill.Name = name;
        skill.Description = request.Description?.Trim();
        skill.Content = request.Content.Trim();
        skill.AllowedTools = NormalizeAllowedTools(request.AllowedTools);
        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    /// <summary>Apaga skill DB — mesmo escopo do PUT.</summary>
    private static async Task<IResult> DeleteSkillAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var skill = await FindSkillAsync(db, id, user, ct);
        if (skill is null)
        {
            return Results.NotFound(new { detail = SkillNotFound });
        }
        db.Skills.Remove(skill);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    /// <summary>
    /// Toggle: skill própria → <c>IsActive</c>; global → tombstone por nome
    /// (<c>u:{id}:skills-disabled</c>) — deny admin é absoluto.
    /// </summary>
    private static async Task<IResult> SetSkillEnabledAsync(
        string id, [FromBody] CustomizeEnabledRequest request,
        HttpContext http, AppDbContext db,
        UserConfigOverlayService overlay, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (skill is null
            || (skill.UserId is not null && skill.UserId != user.Id))
        {
            return Results.NotFound(new { detail = SkillNotFound });
        }

        if (skill.UserId is null)
        {
            var adminOff = await overlay.AdminDisabledSkillNamesAsync(ct);
            if (request.Enabled && adminOff.Contains(skill.Name))
            {
                return Results.BadRequest(new
                {
                    detail = "Skill desabilitada pelo administrador — o usuário não pode religar.",
                });
            }
            await overlay.SetSkillDisabledAsync(
                user.Id, skill.Name, disabled: !request.Enabled, ct);
            return Results.Ok(new { status = true });
        }

        skill.IsActive = request.Enabled;
        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    // ---------------- Admin: denylist global + import ----------------

    /// <summary>Lê a denylist global de skills (admin).</summary>
    private static async Task<IResult> GetAdminDisabledSkillsAsync(
        HttpContext http, AppDbContext db,
        UserConfigOverlayService overlay, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user?.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }
        var names = await overlay.AdminDisabledSkillNamesAsync(ct);
        return Results.Ok(new { names = names.Order().ToList() });
    }

    /// <summary>Sobrescreve a denylist global de skills (admin — RF-006).</summary>
    private static async Task<IResult> SetAdminDisabledSkillsAsync(
        [FromBody] SkillsDisabledRequest request,
        HttpContext http, AppDbContext db,
        UserConfigOverlayService overlay, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user?.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }
        await overlay.SetGlobalSkillsDisabledAsync(request.Names ?? [], ct);
        var names = await overlay.AdminDisabledSkillNamesAsync(ct);
        return Results.Ok(new { names = names.Order().ToList() });
    }

    /// <summary>Importa SKILL.md de repositório público como skills globais (admin — RF-008).</summary>
    private static async Task<IResult> ImportSkillsAsync(
        [FromBody] SkillImportRequest request,
        HttpContext http, AppDbContext db,
        SkillImportService importer, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user?.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }
        if (string.IsNullOrWhiteSpace(request.RepoUrl))
        {
            return Results.BadRequest(new { detail = "repoUrl é obrigatório." });
        }
        try
        {
            return Results.Ok(await importer.ImportAsync(
                request.RepoUrl, request.SubPath, request.Names, ct));
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.StartsWith("invalid:", StringComparison.Ordinal)
                ? Results.BadRequest(new { detail = ex.Message[8..].TrimStart() })
                : Results.Problem(
                    ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Importa plugin.json de repositório público — skills/commands/MCPs globais (admin).</summary>
    private static async Task<IResult> ImportPluginAsync(
        [FromBody] PluginImportRequest request,
        HttpContext http, AppDbContext db,
        PluginImportService importer, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user?.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }
        if (string.IsNullOrWhiteSpace(request.RepoUrl))
        {
            return Results.BadRequest(new { detail = "repoUrl é obrigatório." });
        }
        try
        {
            return Results.Ok(await importer.ImportAsync(request.RepoUrl, ct));
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message.StartsWith("invalid:", StringComparison.Ordinal)
                ? Results.BadRequest(new { detail = ex.Message[8..].TrimStart() })
                : Results.Problem(
                    ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    // ---------------- Helpers ----------------

    private static async Task<KnowledgeNote?> FindNoteAsync(
        AppDbContext db, string id, User user, CancellationToken ct)
    {
        var note = await db.KnowledgeNotes.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null)
        {
            return null;
        }
        var editable = note.UserId == user.Id
            || (note.UserId is null && user.Role == UserRoles.Admin);
        return editable ? note : null;
    }

    private static async Task<Skill?> FindSkillAsync(
        AppDbContext db, string id, User user, CancellationToken ct)
    {
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (skill is null)
        {
            return null;
        }
        var editable = skill.UserId == user.Id
            || (skill.UserId is null && user.Role == UserRoles.Admin);
        return editable ? skill : null;
    }

    private static KnowledgeNoteResponse ToNoteResponse(
        KnowledgeNote n, string userId, bool isAdmin, HashSet<string> disabled)
    {
        var global = n.UserId is null;
        return new KnowledgeNoteResponse(
            n.Id, n.Title, n.Trigger, n.Content, n.Pinned,
            global ? "org" : "personal",
            global ? !disabled.Contains(n.Id) : true,
            n.UserId == userId || (global && isAdmin),
            n.UpdatedAt);
    }

    private static string? ValidateNote(KnowledgeNoteRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Title))
        {
            return "Título é obrigatório.";
        }
        if (!r.Pinned && string.IsNullOrWhiteSpace(r.Trigger))
        {
            return "Trigger é obrigatório quando a nota não está pinada.";
        }
        if (string.IsNullOrWhiteSpace(r.Content))
        {
            return "Conteúdo é obrigatório.";
        }
        return r.Content.Length > MaxNoteContent
            ? $"Conteúdo excede {MaxNoteContent} caracteres."
            : null;
    }

    private static string? ValidateSkill(SkillUpsertRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name))
        {
            return "Nome é obrigatório.";
        }
        return string.IsNullOrWhiteSpace(r.Content)
            ? "Conteúdo é obrigatório."
            : null;
    }

    /// <summary>Normaliza allowed-tools: CSV trim/lower ou null quando vazio.</summary>
    private static string? NormalizeAllowedTools(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var tools = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).Distinct().ToList();
        var joined = string.Join(',', tools);
        return tools.Count == 0 ? null : joined.Length > 500 ? joined[..500] : joined;
    }
}
