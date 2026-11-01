using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// CRUD das regras de permissão ordenadas do usuário + lista/revogação das
/// aprovações duráveis (SPEC-20261018-permissions-v2 RF-001/RF-002 e a UI
/// Settings → Permissions). Escopo: sempre o usuário autenticado.
/// </summary>
public static class PermissionEndpoints
{
    /// <summary>Mapeia <c>/api/v1/permissions</c>.</summary>
    public static RouteGroupBuilder MapPermissionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/permissions").RequireAuthorization();

        group.MapGet("/rules", ListRulesAsync);
        group.MapPost("/rules", CreateRuleAsync);
        group.MapPut("/rules/{id}", UpdateRuleAsync);
        group.MapDelete("/rules/{id}", DeleteRuleAsync);
        group.MapGet("/approvals", ListApprovalsAsync);
        group.MapDelete("/approvals/{id}", DeleteApprovalAsync);

        return group;
    }

    private static async Task<IResult> ListRulesAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await AuthEndpoints.FindUserAsync(http, db, ct) is not { } user)
        {
            return Results.Unauthorized();
        }
        var rules = await db.UserPermissionRules.AsNoTracking()
            .Where(r => r.UserId == user.Id)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);
        return Results.Ok(rules.Select(ToRuleResponse).ToList());
    }

    private static async Task<IResult> CreateRuleAsync(
        PermissionRuleRequest request, HttpContext http, AppDbContext db,
        CancellationToken ct)
    {
        if (await AuthEndpoints.FindUserAsync(http, db, ct) is not { } user)
        {
            return Results.Unauthorized();
        }
        if (ValidateRule(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }

        var maxPos = await db.UserPermissionRules
            .Where(r => r.UserId == user.Id)
            .MaxAsync(r => (int?)r.Position, ct) ?? 0;
        var rule = new UserPermissionRule
        {
            UserId = user.Id,
            Action = request.Action.Trim().ToLowerInvariant(),
            Resource = request.Resource.Trim(),
            Effect = request.Effect.Trim().ToLowerInvariant(),
            Position = request.Position ?? maxPos + 1,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.UserPermissionRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToRuleResponse(rule));
    }

    private static async Task<IResult> UpdateRuleAsync(
        string id, PermissionRuleRequest request, HttpContext http,
        AppDbContext db, CancellationToken ct)
    {
        if (await AuthEndpoints.FindUserAsync(http, db, ct) is not { } user)
        {
            return Results.Unauthorized();
        }
        if (ValidateRule(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }

        var rule = await db.UserPermissionRules
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == user.Id, ct);
        if (rule is null)
        {
            return Results.NotFound(new { detail = "Regra não encontrada." });
        }
        rule.Action = request.Action.Trim().ToLowerInvariant();
        rule.Resource = request.Resource.Trim();
        rule.Effect = request.Effect.Trim().ToLowerInvariant();
        rule.Position = request.Position ?? rule.Position;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToRuleResponse(rule));
    }

    private static async Task<IResult> DeleteRuleAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await AuthEndpoints.FindUserAsync(http, db, ct) is not { } user)
        {
            return Results.Unauthorized();
        }
        var rule = await db.UserPermissionRules
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == user.Id, ct);
        if (rule is null)
        {
            return Results.NotFound(new { detail = "Regra não encontrada." });
        }
        db.UserPermissionRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListApprovalsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await AuthEndpoints.FindUserAsync(http, db, ct) is not { } user)
        {
            return Results.Unauthorized();
        }
        var saved = await db.SavedApprovals.AsNoTracking()
            .Where(s => s.UserId == user.Id)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(saved.Select(s => new
        {
            s.Id, s.Workdir, s.Tool, s.ArgPattern, s.Decision, s.CreatedAt,
        }).ToList());
    }

    private static async Task<IResult> DeleteApprovalAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await AuthEndpoints.FindUserAsync(http, db, ct) is not { } user)
        {
            return Results.Unauthorized();
        }
        var saved = await db.SavedApprovals
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == user.Id, ct);
        if (saved is null)
        {
            return Results.NotFound(new { detail = "Aprovação não encontrada." });
        }
        db.SavedApprovals.Remove(saved);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static string? ValidateRule(PermissionRuleRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Action))
        {
            return "action é obrigatório.";
        }
        var action = r.Action.Trim();
        var baseAction = action.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase)
            ? "mcp"
            : action;
        if (!PermissionRuleEvaluator.ValidActions.Contains(baseAction))
        {
            return $"action deve ser {string.Join(", ", PermissionRuleEvaluator.ValidActions.OrderBy(a => a))} (mcp:<server> aceito).";
        }
        if (string.IsNullOrWhiteSpace(r.Resource))
        {
            return "resource (glob) é obrigatório.";
        }
        return !PermissionRuleEvaluator.ValidEffects.Contains(r.Effect)
            ? "effect deve ser allow, ask ou deny."
            : null;
    }

    private static object ToRuleResponse(UserPermissionRule r) => new
    {
        r.Id, r.Action, r.Resource, r.Effect, r.Position, r.CreatedAt,
    };

    /// <summary>Payload de criação/edição de regra.</summary>
    public sealed record PermissionRuleRequest(
        string Action, string Resource, string Effect, int? Position);
}
