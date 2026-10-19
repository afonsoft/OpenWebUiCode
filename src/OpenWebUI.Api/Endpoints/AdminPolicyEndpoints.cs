using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// CRUD de policies administrativas + simulação + auditoria de bloqueios
/// (SPEC-20261018-policies-admin RF-005). Somente admin.
/// </summary>
public static class AdminPolicyEndpoints
{
    /// <summary>Mapeia <c>/api/v1/admin/policies</c>.</summary>
    public static RouteGroupBuilder MapAdminPolicyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/policies").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapPost("/simulate", SimulateAsync);
        group.MapGet("/denials", ListDenialsAsync);

        return group;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await Admin(http, db, ct) is not { }) return Forbid();
        var statements = await db.AdminPolicyStatements.AsNoTracking()
            .OrderBy(s => s.Order).ToListAsync(ct);
        return Results.Ok(statements.Select(ToResponse).ToList());
    }

    private static async Task<IResult> CreateAsync(
        PolicyStatementRequest request, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await Admin(http, db, ct) is not { }) return Forbid();
        if (Validate(request) is { } error) return Results.BadRequest(new { detail = error });

        var maxOrder = await db.AdminPolicyStatements.MaxAsync(s => (int?)s.Order, ct) ?? 0;
        var s = new AdminPolicyStatement
        {
            Order = request.Order ?? maxOrder + 1,
            Action = request.Action.Trim(),
            Resource = request.Resource.Trim(),
            Effect = "deny",
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.AdminPolicyStatements.Add(s);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(s));
    }

    private static async Task<IResult> UpdateAsync(
        string id, PolicyStatementRequest request, HttpContext http,
        AppDbContext db, CancellationToken ct)
    {
        if (await Admin(http, db, ct) is not { }) return Forbid();
        if (Validate(request) is { } error) return Results.BadRequest(new { detail = error });

        var s = await db.AdminPolicyStatements.FindAsync([id], ct);
        if (s is null) return Results.NotFound(new { detail = "Policy não encontrada." });

        s.Order = request.Order ?? s.Order;
        s.Action = request.Action.Trim();
        s.Resource = request.Resource.Trim();
        s.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(s));
    }

    private static async Task<IResult> DeleteAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await Admin(http, db, ct) is not { }) return Forbid();
        var s = await db.AdminPolicyStatements.FindAsync([id], ct);
        if (s is null) return Results.NotFound(new { detail = "Policy não encontrada." });
        db.AdminPolicyStatements.Remove(s);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SimulateAsync(
        PolicySimulateRequest request, HttpContext http, AppDbContext db,
        PolicyService policies, CancellationToken ct)
    {
        if (await Admin(http, db, ct) is not { }) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Action)
            || !PolicyService.ValidActions.Contains(request.Action))
        {
            return Results.BadRequest(new { detail = "action inválida." });
        }

        var statements = await policies.LoadAsync(request.Action, ct);
        var matched = PolicyService.MatchIn(statements, new[] { request.Resource ?? "" });
        return Results.Ok(new
        {
            denied = matched is not null,
            matched = matched is null ? null : ToResponse(matched),
        });
    }

    private static async Task<IResult> ListDenialsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await Admin(http, db, ct) is not { }) return Forbid();
        var denials = await db.PolicyDenials.AsNoTracking()
            .OrderByDescending(d => d.CreatedAt).Take(100).ToListAsync(ct);
        return Results.Ok(denials);
    }

    private static string? Validate(PolicyStatementRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Action) || !PolicyService.ValidActions.Contains(r.Action))
        {
            return "action deve ser provider.use, model.use, tool.use ou integration.use.";
        }
        return string.IsNullOrWhiteSpace(r.Resource)
            ? "resource (glob) é obrigatório."
            : null;
    }

    private static async Task<User?> Admin(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        return user is { Role: UserRoles.Admin } ? user : null;
    }

    private static IResult Forbid() =>
        Results.Json(new { detail = "Apenas admin." }, statusCode: 403);

    private static object ToResponse(AdminPolicyStatement s) => new
    {
        s.Id, s.Order, s.Action, s.Resource, s.Effect, s.Note, s.CreatedAt,
    };

    /// <summary>Payload de criação/edição de statement.</summary>
    public sealed record PolicyStatementRequest(
        string Action, string Resource, string? Note, int? Order);

    /// <summary>Payload de simulação (dry-run).</summary>
    public sealed record PolicySimulateRequest(string Action, string? Resource);
}
