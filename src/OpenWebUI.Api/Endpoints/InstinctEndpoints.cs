using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Superfície de revisão dos instincts
/// (SPEC-20261023-instincts-learning RF-004/RF-005): listagem por
/// status/escopo, approve/reject/delete e evolve → rascunho de SKILL.md
/// (retornado no response — nunca escrito em disco).
/// </summary>
public static class InstinctEndpoints
{
    /// <summary>Mapeia os endpoints de instincts.</summary>
    public static IEndpointRouteBuilder MapInstinctEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/instincts").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapPatch("/{id}", PatchAsync);
        group.MapPost("/evolve", EvolveAsync);
        return app;
    }

    /// <summary>GET /api/v1/instincts?status=&amp;scope= — lista do
    /// usuário, pending primeiro.</summary>
    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, InstinctService instincts,
        string? status, string? scope, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (status is not null && status is not ("pending" or "active"
            or "rejected" or "expired"))
        {
            return Results.BadRequest(new { detail = "status inválido." });
        }
        if (scope is not null && scope is not ("user" or "workspace"))
        {
            return Results.BadRequest(new { detail = "scope inválido." });
        }

        var items = await instincts.ListAsync(user.Id, status, scope, ct);
        var pending = await instincts.PendingCountAsync(user.Id, ct);
        return Results.Ok(new
        {
            pendingCount = pending,
            items = items.Select(ToResponse).ToList(),
        });
    }

    /// <summary>PATCH /api/v1/instincts/{id} — approve/reject/delete.</summary>
    private static async Task<IResult> PatchAsync(
        string id, InstinctPatchRequest request,
        HttpContext http, AppDbContext db, InstinctService instincts,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (request.Action is not ("approve" or "reject" or "delete"))
        {
            return Results.BadRequest(
                new { detail = "action deve ser approve|reject|delete." });
        }

        var instinct = await instincts.ApplyActionAsync(
            user.Id, id, request.Action, ct);
        return instinct is null
            ? Results.NotFound(new { detail = "Instinct não encontrado." })
            : Results.Ok(new { deleted = request.Action == "delete", item = ToResponse(instinct) });
    }

    /// <summary>POST /api/v1/instincts/evolve — rascunho de SKILL.md do
    /// domínio (≥3 active); 409 sem massa mínima.</summary>
    private static async Task<IResult> EvolveAsync(
        InstinctEvolveRequest request,
        HttpContext http, AppDbContext db, InstinctService instincts,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request.Domain))
        {
            return Results.BadRequest(new { detail = "domain é obrigatório." });
        }
        if (request.Scope is not null && request.Scope is not ("user" or "workspace"))
        {
            return Results.BadRequest(new { detail = "scope inválido." });
        }

        var draft = await instincts.EvolveAsync(
            user.Id, request.Domain, request.Scope, ct);
        return draft is null
            ? Results.Json(
                new { detail = "Massa mínima insuficiente (≥3 instincts active no domínio)." },
                statusCode: 409)
            : Results.Ok(new
            {
                draftSkillMd = draft.Value.Draft,
                sources = draft.Value.Sources,
            });
    }

    private static object ToResponse(Instinct i) => new
    {
        i.Id,
        i.Scope,
        i.WorkspaceId,
        i.Domain,
        i.Trigger,
        i.Body,
        confidence = Math.Round(i.Confidence, 3),
        i.Status,
        i.HitCount,
        i.LastSeenAt,
        i.CreatedAt,
        i.UpdatedAt,
    };
}

/// <summary>Payload do PATCH de instinct.</summary>
public sealed record InstinctPatchRequest(string Action);

/// <summary>Payload do evolve de instincts.</summary>
public sealed record InstinctEvolveRequest(string Domain, string? Scope = null);
