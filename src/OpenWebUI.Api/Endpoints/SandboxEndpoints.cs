using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Sandbox;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Capabilities do sandbox (SPEC-20261031-sandboxed-exec RF-006):
/// <c>GET /api/v1/system/sandbox</c> reporta
/// <c>{ bwrap, seccomp, proxy, mode }</c> — tipo "devin doctor". Admin.
/// </summary>
public static class SandboxEndpoints
{
    /// <summary>Mapeia as rotas do grupo <c>/api/v1/system</c>.</summary>
    public static void MapSandboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/system").RequireAuthorization();
        group.MapGet("/sandbox", GetAsync);
    }

    private static async Task<IResult> GetAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }
        var caps = SandboxedProcessRunner.Capabilities;
        return Results.Ok(new
        {
            caps.Bwrap,
            caps.Seccomp,
            caps.Proxy,
            caps.Mode,
        });
    }
}
