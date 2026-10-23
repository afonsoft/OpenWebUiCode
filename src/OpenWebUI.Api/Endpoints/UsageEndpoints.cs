using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Superfície de uso e políticas de budget
/// (SPEC-20261023-run-cost-budgets RF-005): <c>/api/v1/usage/me</c> para o
/// próprio usuário, <c>/api/v1/admin/usage</c> agregado por usuário/período
/// e CRUD de <c>/api/v1/admin/budget-policies</c> — somente admin.
/// </summary>
public static class UsageEndpoints
{
    /// <summary>Mapeia os endpoints de uso/budget.</summary>
    public static IEndpointRouteBuilder MapUsageEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/usage").RequireAuthorization();
        me.MapGet("/me", MyUsageAsync);

        var admin = app.MapGroup("/api/v1/admin").RequireAuthorization();
        admin.MapGet("/usage", AdminUsageAsync);
        admin.MapGet("/budget-policies", ListPoliciesAsync);
        admin.MapPost("/budget-policies", CreatePolicyAsync);
        admin.MapPut("/budget-policies/{id}", UpdatePolicyAsync);
        admin.MapDelete("/budget-policies/{id}", DeletePolicyAsync);

        return app;
    }

    /// <summary>Uso do próprio usuário no mês UTC corrente + cap efetivo.</summary>
    private static async Task<IResult> MyUsageAsync(
        HttpContext http, AppDbContext db, BudgetPolicyService budgets,
        RunUsageLedgerService ledger, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow;
        var totals = await ledger.MonthTotalsAsync(user.Id, now, ct);
        var limits = await budgets.EffectiveAsync(user.Id, ct);
        object? cap = limits.UsdPerMonth is { } usd
            ? new
            {
                usdPerMonth = usd,
                usedPct = usd > 0
                    ? Math.Round(totals.CostUsd / usd * 100, 1) : 0d,
            }
            : null;
        return Results.Ok(new
        {
            month = $"{now.Year:D4}-{now.Month:D2}",
            promptTokens = totals.PromptTokens,
            completionTokens = totals.CompletionTokens,
            costUsd = Math.Round(totals.CostUsd, 6),
            runs = totals.Runs,
            cap,
        });
    }

    /// <summary>Agregado admin por usuário e top chats por custo na janela.</summary>
    private static async Task<IResult> AdminUsageAsync(
        HttpContext http, AppDbContext db, RunUsageLedgerService ledger,
        string? userId, string? from, string? to, int? top, CancellationToken ct)
    {
        if (await Admin(http, db, ct) is not { })
        {
            return Forbid();
        }
        var (fromEpoch, toEpoch) = ParseWindow(from, to);
        var users = await ledger.ByUserAsync(fromEpoch, toEpoch, ct);
        var emails = await db.Users.AsNoTracking()
            .Where(u => users.Select(x => x.UserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, ct);
        var topChats = await ledger.TopChatsAsync(
            fromEpoch, toEpoch, Math.Clamp(top ?? 10, 1, 100), ct);
        return Results.Ok(new
        {
            from = DateTimeOffset.FromUnixTimeSeconds(fromEpoch)
                .ToString("yyyy-MM-dd"),
            to = DateTimeOffset.FromUnixTimeSeconds(toEpoch)
                .ToString("yyyy-MM-dd"),
            users = users.Select(u => new
            {
                u.UserId,
                email = emails.GetValueOrDefault(u.UserId),
                costUsd = Math.Round(u.CostUsd, 6),
                tokens = u.Tokens,
                u.Runs,
            }).ToList(),
            topChats = topChats.Select(c => new
            {
                c.ChatId, c.UserId, costUsd = Math.Round(c.CostUsd, 6),
            }).ToList(),
        });
    }

    private static async Task<IResult> ListPoliciesAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await Admin(http, db, ct) is not { })
        {
            return Forbid();
        }
        var policies = await db.BudgetPolicies.AsNoTracking()
            .OrderBy(p => p.Subject).ToListAsync(ct);
        return Results.Ok(policies.Select(ToResponse).ToList());
    }

    private static async Task<IResult> CreatePolicyAsync(
        BudgetPolicyRequest request, HttpContext http, AppDbContext db,
        CancellationToken ct)
    {
        if (await Admin(http, db, ct) is not { })
        {
            return Forbid();
        }
        if (Validate(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }
        var subject = request.Subject.Trim();
        if (await db.BudgetPolicies.AnyAsync(p => p.Subject == subject, ct))
        {
            return Results.BadRequest(
                new { detail = $"Já existe policy para '{subject}'." });
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var policy = new BudgetPolicy
        {
            Subject = subject,
            UsdPerRun = NonNegative(request.UsdPerRun),
            UsdPerMonth = NonNegative(request.UsdPerMonth),
            TokensPerMonth = request.TokensPerMonth is { } t && t >= 0 ? t : null,
            MaxModelTier = request.MaxModelTier is { } m
                ? Math.Clamp(m, 0, 3) : null,
            Note = string.IsNullOrWhiteSpace(request.Note)
                ? null : request.Note.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.BudgetPolicies.Add(policy);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(policy));
    }

    private static async Task<IResult> UpdatePolicyAsync(
        string id, BudgetPolicyRequest request, HttpContext http,
        AppDbContext db, CancellationToken ct)
    {
        if (await Admin(http, db, ct) is not { })
        {
            return Forbid();
        }
        var policy = await db.BudgetPolicies.FindAsync([id], ct);
        if (policy is null)
        {
            return Results.NotFound(new { detail = "Policy não encontrada." });
        }
        if (Validate(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }
        var subject = request.Subject.Trim();
        if (!subject.Equals(policy.Subject, StringComparison.Ordinal)
            && await db.BudgetPolicies.AnyAsync(p => p.Subject == subject, ct))
        {
            return Results.BadRequest(
                new { detail = $"Já existe policy para '{subject}'." });
        }
        policy.Subject = subject;
        policy.UsdPerRun = NonNegative(request.UsdPerRun);
        policy.UsdPerMonth = NonNegative(request.UsdPerMonth);
        policy.TokensPerMonth = request.TokensPerMonth is { } t && t >= 0
            ? t : null;
        policy.MaxModelTier = request.MaxModelTier is { } m
            ? Math.Clamp(m, 0, 3) : null;
        policy.Note = string.IsNullOrWhiteSpace(request.Note)
            ? null : request.Note.Trim();
        policy.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(policy));
    }

    private static async Task<IResult> DeletePolicyAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await Admin(http, db, ct) is not { })
        {
            return Forbid();
        }
        var policy = await db.BudgetPolicies.FindAsync([id], ct);
        if (policy is null)
        {
            return Results.NotFound(new { detail = "Policy não encontrada." });
        }
        db.BudgetPolicies.Remove(policy);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { deleted = true });
    }

    private static async Task<User?> Admin(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        return user is { Role: UserRoles.Admin } ? user : null;
    }

    private static IResult Forbid() =>
        Results.Json(new { detail = "Apenas admin." }, statusCode: 403);

    private static double? NonNegative(double? v) =>
        v is null or < 0 ? null : v;

    private static (long From, long To) ParseWindow(string? from, string? to)
    {
        var now = DateTimeOffset.UtcNow;
        var fromDt = DateTimeOffset.TryParse(from, out var f)
            ? f : new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var toDt = DateTimeOffset.TryParse(to, out var t)
            ? t : fromDt.AddMonths(1);
        return (fromDt.ToUnixTimeSeconds(), toDt.ToUnixTimeSeconds());
    }

    private static string? Validate(BudgetPolicyRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Subject))
        {
            return "subject é obrigatório (user:{id}, group:{id} ou global).";
        }
        var s = r.Subject.Trim();
        if (s != "global" && !s.StartsWith("user:", StringComparison.Ordinal)
            && !s.StartsWith("group:", StringComparison.Ordinal))
        {
            return "subject deve ser user:{id}, group:{id} ou global.";
        }
        if (r.UsdPerRun is null && r.UsdPerMonth is null
            && r.TokensPerMonth is null && r.MaxModelTier is null)
        {
            return "ao menos um limite é obrigatório.";
        }
        return null;
    }

    private static object ToResponse(BudgetPolicy p) => new
    {
        p.Id, p.Subject, p.UsdPerRun, p.UsdPerMonth,
        p.TokensPerMonth, p.MaxModelTier, p.Note, p.CreatedAt, p.UpdatedAt,
    };
}

/// <summary>Payload do CRUD de budget policy.</summary>
public sealed record BudgetPolicyRequest(
    string Subject,
    double? UsdPerRun = null,
    double? UsdPerMonth = null,
    long? TokensPerMonth = null,
    int? MaxModelTier = null,
    string? Note = null);
