using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Secrets do agente (SPEC-20261017-agent-secrets-env): o usuário gerencia
/// os próprios pares nome→valor em <c>/api/v1/secrets</c> e o admin os
/// defaults globais em <c>/api/v1/configs/secrets</c>. Listagens devolvem
/// só nome/origem/timestamps — valores são write-only.
/// </summary>
public static class SecretEndpoints
{
    /// <summary>Mapeia as rotas de secrets (user + admin global).</summary>
    public static void MapSecretEndpoints(this IEndpointRouteBuilder app)
    {
        var user = app.MapGroup("/api/v1/secrets").RequireAuthorization();
        user.MapGet("/", ListUserAsync);
        user.MapPut("/{name}", PutUserAsync);
        user.MapDelete("/{name}", DeleteUserAsync);

        var admin = app.MapGroup("/api/v1/configs/secrets").RequireAuthorization();
        admin.MapGet("/", ListGlobalAsync);
        admin.MapPut("/{name}", PutGlobalAsync);
        admin.MapDelete("/{name}", DeleteGlobalAsync);
    }

    // ---------------- nível usuário ----------------

    /// <summary>GET /api/v1/secrets — lista mesclada global∪user com origem (sem valores).</summary>
    private static async Task<IResult> ListUserAsync(
        HttpContext http, AppDbContext db, UserSecretsService secrets,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var list = await secrets.ListResolvedAsync(user.Id, ct);
        return Results.Ok(list.Select(e =>
            new SecretEntryResponse(e.Name, e.Source, e.CreatedAt, e.UpdatedAt)));
    }

    /// <summary>PUT /api/v1/secrets/{name} — upsert no nível do usuário (override de global).</summary>
    private static async Task<IResult> PutUserAsync(
        string name, SecretUpsertRequest request,
        HttpContext http, AppDbContext db, UserSecretsService secrets,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (request.Value is null)
        {
            return Results.BadRequest(new { detail = "value é obrigatório." });
        }
        var error = await secrets.SetUserAsync(user.Id, name, request.Value, ct);
        return error is null
            ? Results.Ok(new SecretUpsertResponse(name))
            : Results.BadRequest(new { detail = error });
    }

    /// <summary>DELETE /api/v1/secrets/{name} — remove o secret do usuário (global volta a valer).</summary>
    private static async Task<IResult> DeleteUserAsync(
        string name, HttpContext http, AppDbContext db,
        UserSecretsService secrets, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        await secrets.DeleteUserAsync(user.Id, name, ct);
        return Results.Ok(new StatusResponse(true));
    }

    // ---------------- nível global (admin) ----------------

    /// <summary>GET /api/v1/configs/secrets — lista global (admin, sem valores).</summary>
    private static async Task<IResult> ListGlobalAsync(
        HttpContext http, UserSecretsService secrets, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }
        var list = await secrets.ListGlobalAsync(ct);
        return Results.Ok(list.Select(e =>
            new SecretEntryResponse(e.Name, e.Source, e.CreatedAt, e.UpdatedAt)));
    }

    /// <summary>PUT /api/v1/configs/secrets/{name} — upsert global (admin).</summary>
    private static async Task<IResult> PutGlobalAsync(
        string name, SecretUpsertRequest request,
        HttpContext http, UserSecretsService secrets, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }
        if (request.Value is null)
        {
            return Results.BadRequest(new { detail = "value é obrigatório." });
        }
        var error = await secrets.SetGlobalAsync(name, request.Value, ct);
        return error is null
            ? Results.Ok(new SecretUpsertResponse(name))
            : Results.BadRequest(new { detail = error });
    }

    /// <summary>DELETE /api/v1/configs/secrets/{name} — remove o global (admin).</summary>
    private static async Task<IResult> DeleteGlobalAsync(
        string name, HttpContext http, UserSecretsService secrets,
        CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }
        await secrets.DeleteGlobalAsync(name, ct);
        return Results.Ok(new StatusResponse(true));
    }
}
