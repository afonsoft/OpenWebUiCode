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
        me.MapGet("/chats/{chatId}", ChatUsageAsync);

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
        object? cap = null;
        if (limits.UsdPerMonth is { } usd)
        {
            var usedPct = usd > 0
                ? Math.Round(totals.CostUsd / usd * 100, 1)
                : 0d;
            cap = new { usdPerMonth = usd, usedPct };
        }
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

    /// <summary>Uso de um chat do próprio usuário: totais, por modelo e série por run.</summary>
    private static async Task<IResult> ChatUsageAsync(
        string chatId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (!await db.Chats.AsNoTracking().AnyAsync(
            c => c.Id == chatId && c.UserId == user.Id, ct))
        {
            return Results.NotFound(new { detail = "Chat não encontrado." });
        }

        var records = await db.RunUsageRecords.AsNoTracking()
            .Where(r => r.ChatId == chatId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        var models = records
            .GroupBy(r => r.ModelId ?? "unknown")
            .Select(g => new ChatUsageModelRow(
                g.Key, g.Count(),
                g.Sum(r => r.PromptTokens), g.Sum(r => r.CompletionTokens),
                g.Sum(r => r.ReasoningTokens),
                g.Sum(r => r.PromptTokens + r.CompletionTokens + r.ReasoningTokens),
                Math.Round(g.Sum(r => r.CostUsd ?? 0), 6)))
            .OrderByDescending(m => m.Tokens)
            .ToList();
        return Results.Ok(new ChatUsageResponse(
            chatId,
            new ChatUsageTotals(
                records.Count,
                records.Sum(r => r.PromptTokens),
                records.Sum(r => r.CompletionTokens),
                records.Sum(r => r.ReasoningTokens),
                Math.Round(records.Sum(r => r.CostUsd ?? 0), 6)),
            models,
            records.Select(r => new ChatUsageRunRow(
                r.RunId, r.ModelId, r.ParentRunId, r.Steps, r.PromptTokens,
                r.CompletionTokens, r.ReasoningTokens, r.CostUsd,
                r.CreatedAt)).ToList()));
    }

    /// <summary>
    /// Dashboard de uso admin (SPEC-20261102): totais, série diária de
    /// tokens, agregado por modelo, ranking por usuário com cap efetivo
    /// e q.Top chats por custo. <c>?q.Days=</c> (default 30, 1..365) é
    /// atalho de janela — <c>from</c>/<c>to</c> ISO têm precedência;
    /// <c>?q.UserId=</c> filtra todas as seções.
    /// </summary>
    private static async Task<IResult> AdminUsageAsync(
        HttpContext http, [AsParameters] AdminUsageDeps deps,
        [AsParameters] AdminUsageQuery q, CancellationToken ct)
    {
        var db = deps.Db;
        var ledger = deps.Ledger;
        var budgets = deps.Budgets;
        if (await Admin(http, db, ct) is not { })
        {
            return Forbid();
        }
        if (q.Days is < 1 or > 365)
        {
            return Results.BadRequest(
                new { detail = "days deve estar entre 1 e 365." });
        }
        var (fromEpoch, toEpoch) = ParseWindow(q.From, q.To, q.Days ?? 30);
        if (toEpoch <= fromEpoch)
        {
            return Results.BadRequest(new { detail = "Janela inválida." });
        }

        var uid = string.IsNullOrWhiteSpace(q.UserId) ? null : q.UserId.Trim();
        var window = db.RunUsageRecords.AsNoTracking()
            .Where(r => r.CreatedAt >= fromEpoch && r.CreatedAt < toEpoch
                && (uid == null || r.UserId == uid));
        var totals = await ledger.TotalsAsync(window, ct);
        var activeUsers = await window.Select(r => r.UserId)
            .Distinct().CountAsync(ct);
        var series = await ledger.ByDayAsync(fromEpoch, toEpoch, uid, ct);
        var models = await ledger.ByModelAsync(
            fromEpoch, toEpoch, Math.Clamp(q.Top ?? 20, 1, 100), uid, ct);
        var users = await ledger.ByUserAsync(fromEpoch, toEpoch, uid, ct);
        var topChats = await ledger.TopChatsAsync(
            fromEpoch, toEpoch, Math.Clamp(q.Top ?? 10, 1, 100), uid, ct);

        var emails = await db.Users.AsNoTracking()
            .Where(u => users.Select(x => x.UserId).Contains(u.Id)
                || topChats.Select(c => c.UserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, ct);
        var titles = await db.Chats.AsNoTracking()
            .Where(c => topChats.Select(x => x.ChatId).Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => (string?)c.Title, ct);

        var rows = new List<AdminUsageUser>(users.Count);
        foreach (var u in users)
        {
            var limits = await budgets.EffectiveAsync(u.UserId, ct);
            rows.Add(new AdminUsageUser(
                u.UserId, emails.GetValueOrDefault(u.UserId), u.Runs,
                u.PromptTokens, u.CompletionTokens, u.ReasoningTokens,
                u.Tokens, Math.Round(u.CostUsd, 6),
                limits.TokensPerMonth, limits.UsdPerMonth,
                UsedPct(u.Tokens, u.CostUsd, limits)));
        }

        return Results.Ok(new AdminUsageResponse(
            DateTimeOffset.FromUnixTimeSeconds(fromEpoch)
                .ToString("yyyy-MM-dd"),
            DateTimeOffset.FromUnixTimeSeconds(toEpoch)
                .ToString("yyyy-MM-dd"),
            new AdminUsageTotals(
                totals.PromptTokens, totals.CompletionTokens,
                totals.ReasoningTokens, Math.Round(totals.CostUsd, 6),
                totals.Runs, activeUsers),
            series.Select(d => new AdminUsageDay(
                d.Day, d.PromptTokens, d.CompletionTokens,
                d.ReasoningTokens, Math.Round(d.CostUsd, 6))).ToList(),
            models.Select(m => new AdminUsageModel(
                m.ModelId, m.Runs, m.PromptTokens, m.CompletionTokens,
                m.ReasoningTokens, m.Tokens,
                Math.Round(m.CostUsd, 6))).ToList(),
            rows,
            topChats.Select(c => new AdminUsageTopChat(
                c.ChatId, titles.GetValueOrDefault(c.ChatId), c.UserId,
                emails.GetValueOrDefault(c.UserId), c.Tokens,
                Math.Round(c.CostUsd, 6))).ToList()));
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

    /// <summary>
    /// Percentual de cap usado (SPEC-20261102 RF-003): o maior entre
    /// tokens/capTokens e cost/capUsd, 1 casa decimal; null sem cap
    /// mensal (cap=0 é ignorado — sem teto prático naquela dimensão).
    /// </summary>
    private static double? UsedPct(
        long tokens, double costUsd, BudgetLimits limits)
    {
        double? pct = null;
        if (limits.TokensPerMonth is > 0)
        {
            pct = tokens / (double)limits.TokensPerMonth.Value * 100;
        }
        if (limits.UsdPerMonth is > 0)
        {
            var usd = costUsd / limits.UsdPerMonth.Value * 100;
            pct = pct is null ? usd : Math.Max(pct.Value, usd);
        }
        return pct is null ? null : Math.Round(pct.Value, 1);
    }

    /// <summary>
    /// Janela [from, to) em epoch seconds: <c>from</c>/<c>to</c> ISO têm
    /// precedência; sem eles, usa <paramref name="days"/> dias UTC
    /// terminando no fim de hoje (defaults: mês corrente, como antes).
    /// </summary>
    private static (long From, long To) ParseWindow(
        string? from, string? to, int days)
    {
        var now = DateTimeOffset.UtcNow;
        var hasFrom = DateTimeOffset.TryParse(from,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var fromDt);
        var hasTo = DateTimeOffset.TryParse(to,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var toDt);
        if (!hasFrom && !hasTo)
        {
            var end = new DateTimeOffset(
                now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero)
                .AddDays(1);
            return (end.AddDays(-days).ToUnixTimeSeconds(),
                end.ToUnixTimeSeconds());
        }
        var fromE = (hasFrom ? fromDt : new DateTimeOffset(
            now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero))
            .ToUnixTimeSeconds();
        var toE = (hasTo ? toDt : DateTimeOffset
            .FromUnixTimeSeconds(fromE).AddMonths(1)).ToUnixTimeSeconds();
        return (fromE, toE);
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

/// <summary>Bundle DI do uso admin (S107).</summary>
internal sealed record AdminUsageDeps(
    AppDbContext Db, RunUsageLedgerService Ledger,
    BudgetPolicyService Budgets);

/// <summary>Query params da janela/filtro do uso admin (S107).</summary>
internal sealed record AdminUsageQuery(
    string? UserId, string? From, string? To, int? Days, int? Top);
