using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de automações (prompts agendados), espelhando o upstream.</summary>
public static class AutomationEndpoints
{
    private const string PromptKey = "prompt";
    private static readonly HashSet<string> Kinds = ["interval", "daily", "weekly", "once"];

    /// <summary>Mapeia as rotas de automações.</summary>
    public static RouteGroupBuilder MapAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/automations").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/runs", ListRunsInRangeAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapGet("/{id}/runs", ListRunsAsync);
        group.MapPost("/{id}/run-now", RunNowAsync);

        return group;
    }

    private static AutomationResponse ToResponse(Automation a) => new(
        a.Id, a.Name, a.Prompt, a.ModelId, a.ScheduleKind,
        a.IntervalMinutes, a.TimeOfDay, a.Weekday, a.Enabled,
        a.NextRunAt, a.CreatedAt, a.UpdatedAt,
        a.ActionKind, a.ActionJson, a.PermissionMode, a.ChatId);

    private static string? Validate(AutomationUpsertRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Nome obrigatório.";
        var kind = r.ActionKind ?? PromptKey;
        if (kind == PromptKey && string.IsNullOrWhiteSpace(r.Prompt))
            return "Prompt obrigatório.";
        if (kind is PromptKey or "skill" && string.IsNullOrWhiteSpace(r.ModelId))
            return "Modelo obrigatório.";
        var actionError = AutomationService.ValidateAction(kind, r.ActionJson);
        if (actionError is not null) return actionError;
        if (r.PermissionMode is not null &&
            r.PermissionMode is not "default" and not "auto" and not "bypass")
            return "PermissionMode deve ser default, auto ou bypass.";
        if (!Kinds.Contains(r.ScheduleKind)) return "ScheduleKind deve ser interval, daily, weekly ou once.";
        if (r.ScheduleKind == "interval" && (r.IntervalMinutes ?? 0) < 1)
            return "Intervalo mínimo é 1 minuto.";
        if (r.ScheduleKind == "once" &&
            r.RunAt is null or <= 0 && (r.InMinutes ?? 0) < 1)
            return "Once exige RunAt (epoch futuro) ou InMinutes >= 1.";
        if (r.ScheduleKind is "daily" or "weekly" &&
            !TimeOnly.TryParseExact(r.TimeOfDay, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            return "TimeOfDay deve estar no formato HH:mm.";
        if (r.ScheduleKind == "weekly" && r.Weekday is null or < 0 or > 6)
            return "Weekday deve ser 0-6.";
        return null;
    }

    private static void Apply(Automation a, AutomationUpsertRequest r)
    {
        a.Name = r.Name.Trim();
        a.Prompt = r.Prompt;
        a.ModelId = r.ModelId;
        a.ActionKind = r.ActionKind ?? PromptKey;
        a.ActionJson = r.ActionJson;
        a.PermissionMode = r.PermissionMode ?? "default";
        a.ChatId = r.ChatId;
        a.ScheduleKind = r.ScheduleKind;
        a.IntervalMinutes = Math.Max(1, r.IntervalMinutes ?? 60);
        a.TimeOfDay = r.TimeOfDay;
        a.Weekday = r.Weekday;
        a.Enabled = r.Enabled ?? a.Enabled;
        a.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (a.ScheduleKind == "once")
        {
            a.NextRunAt = r.RunAt is > 0
                ? r.RunAt
                : DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, r.InMinutes ?? 1)).ToUnixTimeSeconds();
        }
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null) return Results.Unauthorized();

        var items = await db.Automations.AsNoTracking()
            .Where(a => a.UserId == user.Id)
            .OrderByDescending(a => a.UpdatedAt)
            .ToListAsync(ct);
        return Results.Ok(items.Select(ToResponse));
    }

    private static async Task<IResult> GetAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, automation) = await FindOwnedAsync(id, http, db, ct);
        if (user is null) return Results.Unauthorized();
        if (automation is null) return Results.NotFound();
        return Results.Ok(ToResponse(automation));
    }

    private static async Task<IResult> CreateAsync(
        AutomationUpsertRequest request,
        HttpContext http, AppDbContext db,
        AutomationService service, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null) return Results.Unauthorized();

        var error = Validate(request);
        if (error is not null) return Results.BadRequest(new { detail = error });

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var automation = new Automation { UserId = user.Id, CreatedAt = now, Enabled = true };
        Apply(automation, request);
        AutomationService.Reschedule(automation);
        db.Automations.Add(automation);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(automation));
    }

    private static async Task<IResult> UpdateAsync(
        string id, AutomationUpsertRequest request,
        HttpContext http, AppDbContext db,
        AutomationService service, CancellationToken ct)
    {
        var (user, automation) = await FindOwnedAsync(id, http, db, ct);
        if (user is null) return Results.Unauthorized();
        if (automation is null) return Results.NotFound();

        var error = Validate(request);
        if (error is not null) return Results.BadRequest(new { detail = error });

        Apply(automation, request);
        AutomationService.Reschedule(automation);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(automation));
    }

    private static async Task<IResult> DeleteAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, automation) = await FindOwnedAsync(id, http, db, ct);
        if (user is null) return Results.Unauthorized();
        if (automation is null) return Results.NotFound();

        db.Automations.Remove(automation);
        await db.SaveChangesAsync(ct);
        return Results.Ok(true);
    }

    private static async Task<IResult> ListRunsAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, automation) = await FindOwnedAsync(id, http, db, ct);
        if (user is null) return Results.Unauthorized();
        if (automation is null) return Results.NotFound();

        var runs = await db.AutomationRuns.AsNoTracking()
            .Where(r => r.AutomationId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(200)
            .ToListAsync(ct);
        return Results.Ok(runs.Select(r => ToRunResponse(r, automation.Name)));
    }

    private static async Task<IResult> ListRunsInRangeAsync(
        long from, long to,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null) return Results.Unauthorized();

        var runs = await db.AutomationRuns.AsNoTracking()
            .Where(r => r.StartedAt >= from && r.StartedAt <= to &&
                        db.Automations.Any(a => a.Id == r.AutomationId && a.UserId == user.Id))
            .OrderBy(r => r.StartedAt)
            .ToListAsync(ct);
        var names = await db.Automations.AsNoTracking()
            .Where(a => a.UserId == user.Id)
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return Results.Ok(runs.Select(r =>
            ToRunResponse(r, names.GetValueOrDefault(r.AutomationId, ""))));
    }

    private static async Task<IResult> RunNowAsync(
        string id, HttpContext http, AppDbContext db,
        AutomationService service, CancellationToken ct)
    {
        var (user, automation) = await FindOwnedAsync(id, http, db, ct);
        if (user is null) return Results.Unauthorized();
        if (automation is null) return Results.NotFound();

        var run = await service.RunNowAsync(automation, ct);
        AutomationService.Reschedule(automation);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToRunResponse(run, automation.Name));
    }

    private static AutomationRunResponse ToRunResponse(AutomationRun r, string name) => new(
        r.Id, r.AutomationId, name, r.Status, r.Error, r.ChatId, r.StartedAt, r.FinishedAt,
        r.ActionKind, r.Output);

    private static async Task<(User?, Automation?)> FindOwnedAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null) return (null, null);
        var automation = await db.Automations
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == user.Id, ct);
        return (user, automation);
    }
}
