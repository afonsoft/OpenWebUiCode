using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Registry de agentes externos (SPEC-20261016-external-agent-registry):
/// detecção via PATH e toggle de seleção (instala/remove a skill ponte).
/// </summary>
public static class ExternalAgentEndpoints
{
    /// <summary>Mapeia as rotas de agentes externos.</summary>
    public static RouteGroupBuilder MapExternalAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/external-agents").RequireAuthorization();

        group.MapGet("/detect", DetectAsync);
        group.MapPut("/{id}/selected", SetSelectedAsync);

        return group;
    }

    private static async Task<IResult> DetectAsync(
        HttpContext http, AppDbContext db,
        ExternalAgentService external, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(await external.DetectAsync(user.Id, ct));
    }

    private static async Task<IResult> SetSelectedAsync(
        string id, ExternalAgentSelectRequest request,
        HttpContext http, AppDbContext db,
        ExternalAgentService external, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var (ok, error) = await external.SetSelectedAsync(
            user.Id, id, request.Enabled, ct);
        if (ok)
        {
            return Results.Ok(true);
        }
        return error!.Contains("desconhecido", StringComparison.Ordinal)
            ? Results.NotFound(new { detail = error })
            : Results.Conflict(new { detail = error });
    }
}

