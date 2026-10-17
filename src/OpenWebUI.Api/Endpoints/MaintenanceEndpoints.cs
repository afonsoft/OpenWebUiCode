using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Manutenção de sessões (SPEC-20261016-session-maintenance RF-004/RF-005):
/// status do último ciclo e disparo sob demanda (admin, 409 se em andamento).
/// </summary>
public static class MaintenanceEndpoints
{
    /// <summary>Mapeia as rotas de manutenção.</summary>
    public static RouteGroupBuilder MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/maintenance").RequireAuthorization();

        group.MapGet("/status", StatusAsync);
        group.MapPost("/run", RunAsync);
        group.MapPut("/config", SetConfigAsync);

        return group;
    }

    private static MaintenanceCycleResponse ToResponse(
        Infrastructure.Services.MaintenanceCycleResult r, bool dryRun = false) => new(
        r.StartedAt, r.FinishedAt, r.ChatsPurged, r.MemoriesSynthesized,
        r.OpsPurged, r.Vacuumed, r.ChatDays, dryRun);

    private static async Task<IResult> StatusAsync(
        HttpContext http, AppDbContext db, RetentionService retention, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var status = await retention.GetStatusAsync(ct);
        return Results.Ok(new MaintenanceStatusResponse(
            status.LastRun is null ? null : ToResponse(status.LastRun),
            status.ChatDays, status.NextRunAt, status.DbSizeBytes));
    }

    private static async Task<IResult> RunAsync(
        MaintenanceRunRequest request,
        HttpContext http, AppDbContext db, RetentionService retention, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (user.Role != "admin")
        {
            return Results.Json(new { detail = "Ciclo de manutenção exige admin." },
                statusCode: StatusCodes.Status403Forbidden);
        }
        if (!RetentionService.TryEnterCycle())
        {
            return Results.Conflict(new { detail = "Ciclo de manutenção já em andamento." });
        }

        try
        {
            var result = await retention.RunCycleAsync(request.DryRun, ct);
            return Results.Ok(ToResponse(result, request.DryRun));
        }
        finally
        {
            RetentionService.ExitCycle();
        }
    }

    private static async Task<IResult> SetConfigAsync(
        MaintenanceConfigRequest request,
        HttpContext http, AppDbContext db, ConfigService config, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (user.Role != "admin")
        {
            return Results.Json(new { detail = "Config de manutenção exige admin." },
                statusCode: StatusCodes.Status403Forbidden);
        }
        if (request.ChatDays is < 0 or > 3650)
        {
            return Results.BadRequest(new { detail = "ChatDays deve ser 0-3650." });
        }

        await config.SetAsync(RetentionService.ChatDaysKey, request.ChatDays ?? 0, ct);
        return Results.Ok(true);
    }
}

