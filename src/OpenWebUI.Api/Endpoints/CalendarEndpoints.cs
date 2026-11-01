using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de calendários e eventos (/api/v1/calendars) com access grants.</summary>
public static class CalendarEndpoints
{
    private const string CalendarNotFoundMessage = "Calendário não encontrado.";
    /// <summary>Mapeia o grupo /api/v1/calendars.</summary>
    public static void MapCalendarEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/calendars").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapPost("/{id}/access/update", UpdateAccessAsync);
        group.MapGet("/events", ListEventsAsync);
        group.MapGet("/events/search", SearchEventsAsync);
        group.MapPost("/events", CreateEventAsync);
        group.MapPut("/events/{eventId}", UpdateEventAsync);
        group.MapDelete("/events/{eventId}", DeleteEventAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, AccessControlService access, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var all = await db.Calendars.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var groups = await access.GetGroupIdsAsync(user.Id, ct);
        var items = access
            .FilterReadable(user, all, groups, c => c.UserId, c => c.AccessGrantsJson)
            .Select(ToResponse)
            .ToList();
        return Results.Ok(items);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateCalendarRequest request,
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

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var calendar = new Calendar
        {
            UserId = user.Id,
            Name = request.Name.Trim(),
            Color = request.Color?.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Calendars.Add(calendar);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(calendar));
    }

    private static async Task<IResult> GetAsync(
        string id, HttpContext http, AppDbContext db,
        AccessControlService access, CancellationToken ct)
    {
        var (user, calendar) = await LoadAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (calendar is null
            || await access.LevelAsync(user, calendar.UserId, calendar.AccessGrantsJson, ct)
                is AccessControlService.None)
        {
            return Results.NotFound(new { detail = CalendarNotFoundMessage });
        }

        return Results.Ok(ToResponse(calendar));
    }

    private static async Task<IResult> UpdateAsync(
        string id, [FromBody] UpdateCalendarRequest request,
        HttpContext http, AppDbContext db,
        AccessControlService access, CancellationToken ct)
    {
        var (user, calendar) = await LoadAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (calendar is null
            || await access.LevelAsync(user, calendar.UserId, calendar.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound(new { detail = CalendarNotFoundMessage });
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            calendar.Name = request.Name.Trim();
        }
        if (request.Color is not null)
        {
            calendar.Color = request.Color.Trim();
        }
        calendar.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(calendar));
    }

    private static async Task<IResult> DeleteAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, calendar) = await LoadAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (calendar is null
            || (calendar.UserId != user.Id && user.Role != UserRoles.Admin))
        {
            return Results.NotFound(new { detail = CalendarNotFoundMessage });
        }

        var events = db.CalendarEvents.Where(e => e.CalendarId == calendar.Id);
        db.CalendarEvents.RemoveRange(events);
        db.Calendars.Remove(calendar);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<IResult> UpdateAccessAsync(
        string id, [FromBody] AccessUpdateRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, calendar) = await LoadAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (calendar is null
            || (calendar.UserId != user.Id && user.Role != UserRoles.Admin))
        {
            return Results.NotFound(new { detail = CalendarNotFoundMessage });
        }
        if (!ValidGrants(request.AccessGrants))
        {
            return Results.BadRequest(new { detail = "Grant inválido." });
        }

        calendar.AccessGrantsJson = AccessControlService.Serialize(request.AccessGrants);
        calendar.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true, access_grants = request.AccessGrants });
    }

    private static async Task<IResult> ListEventsAsync(
        HttpContext http, AppDbContext db, AccessControlService access,
        [FromQuery] long? from = null, [FromQuery] long? to = null,
        CancellationToken ct = default)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var events = await ReadableEventsAsync(user, db, access, ct);
        var filtered = events
            .Where(e => (from is null || e.EndTs >= from) && (to is null || e.StartTs <= to))
            .OrderBy(e => e.StartTs)
            .Select(ToResponse)
            .ToList();
        return Results.Ok(filtered);
    }

    private static async Task<IResult> SearchEventsAsync(
        HttpContext http, AppDbContext db, AccessControlService access,
        [FromQuery] string? query = null, CancellationToken ct = default)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var events = await ReadableEventsAsync(user, db, access, ct);
        var q = (query ?? string.Empty).Trim();
        var filtered = events
            .Where(e => q.Length == 0
                || e.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (e.Notes?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(e => e.StartTs)
            .Select(ToResponse)
            .ToList();
        return Results.Ok(filtered);
    }

    private static async Task<IResult> CreateEventAsync(
        [FromBody] CreateEventRequest request,
        HttpContext http, AppDbContext db,
        AccessControlService access, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var calendar = await db.Calendars.FirstOrDefaultAsync(c => c.Id == request.CalendarId, ct);
        if (calendar is null
            || await access.LevelAsync(user, calendar.UserId, calendar.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound(new { detail = CalendarNotFoundMessage });
        }
        if (string.IsNullOrWhiteSpace(request.Title) || request.EndTs <= request.StartTs)
        {
            return Results.BadRequest(new { detail = "Título e intervalo válidos são obrigatórios." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ev = new CalendarEvent
        {
            CalendarId = calendar.Id,
            Title = request.Title.Trim(),
            StartTs = request.StartTs,
            EndTs = request.EndTs,
            Color = request.Color?.Trim(),
            Notes = request.Notes?.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.CalendarEvents.Add(ev);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(ev));
    }

    private static async Task<IResult> UpdateEventAsync(
        string eventId, [FromBody] UpdateEventRequest request,
        HttpContext http, AppDbContext db,
        AccessControlService access, CancellationToken ct)
    {
        var (user, ev) = await LoadEventAsync(http, eventId, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (ev is null
            || await access.LevelAsync(user, ev.Calendar!.UserId, ev.Calendar.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound(new { detail = "Evento não encontrado." });
        }

        var start = request.StartTs ?? ev.StartTs;
        var end = request.EndTs ?? ev.EndTs;
        if (end <= start)
        {
            return Results.BadRequest(new { detail = "Intervalo inválido." });
        }

        if (request.Title is not null && !string.IsNullOrWhiteSpace(request.Title))
        {
            ev.Title = request.Title.Trim();
        }
        ev.StartTs = start;
        ev.EndTs = end;
        if (request.Color is not null)
        {
            ev.Color = request.Color.Trim();
        }
        if (request.Notes is not null)
        {
            ev.Notes = request.Notes.Trim();
        }
        ev.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(ev));
    }

    private static async Task<IResult> DeleteEventAsync(
        string eventId, HttpContext http, AppDbContext db,
        AccessControlService access, CancellationToken ct)
    {
        var (user, ev) = await LoadEventAsync(http, eventId, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (ev is null
            || await access.LevelAsync(user, ev.Calendar!.UserId, ev.Calendar.AccessGrantsJson, ct)
                is not AccessControlService.Write)
        {
            return Results.NotFound(new { detail = "Evento não encontrado." });
        }

        db.CalendarEvents.Remove(ev);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<List<CalendarEvent>> ReadableEventsAsync(
        User user, AppDbContext db, AccessControlService access, CancellationToken ct)
    {
        var calendars = await db.Calendars.AsNoTracking().ToListAsync(ct);
        var groups = await access.GetGroupIdsAsync(user.Id, ct);
        var readableIds = access
            .FilterReadable(user, calendars, groups, c => c.UserId, c => c.AccessGrantsJson)
            .Select(c => c.Id)
            .ToHashSet();
        return await db.CalendarEvents.AsNoTracking()
            .Where(e => readableIds.Contains(e.CalendarId))
            .ToListAsync(ct);
    }

    private static async Task<(User? user, Calendar? calendar)> LoadAsync(
        HttpContext http, string id, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null);
        }

        var calendar = await db.Calendars.FirstOrDefaultAsync(c => c.Id == id, ct);
        return (user, calendar);
    }

    private static async Task<(User? user, CalendarEvent? ev)> LoadEventAsync(
        HttpContext http, string eventId, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null);
        }

        var ev = await db.CalendarEvents
            .Include(e => e.Calendar)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);
        return (user, ev);
    }

    private static bool ValidGrants(List<AccessGrant> grants) =>
        grants.All(g =>
            g.PrincipalType is "user" or "group"
            && !string.IsNullOrWhiteSpace(g.PrincipalId)
            && g.Permission is "read" or "write");

    private static CalendarResponse ToResponse(Calendar c) =>
        new(c.Id, c.Name, c.Color, AccessControlService.Parse(c.AccessGrantsJson), c.CreatedAt);

    private static CalendarEventResponse ToResponse(CalendarEvent e) =>
        new(e.Id, e.CalendarId, e.Title, e.StartTs, e.EndTs, e.Color, e.Notes, e.CreatedAt);
}
