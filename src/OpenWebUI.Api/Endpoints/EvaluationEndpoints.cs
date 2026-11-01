using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de avaliações (feedback de mensagens), espelhando <c>/api/v1/evaluations</c>.</summary>
public static class EvaluationEndpoints
{
    /// <summary>Mapeia as rotas de avaliações.</summary>
    public static RouteGroupBuilder MapEvaluationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/evaluations").RequireAuthorization();

        group.MapPost("/feedback", SaveFeedbackAsync);
        group.MapGet("/feedback/{id}", GetFeedbackAsync);
        group.MapPost("/feedback/{id}", SaveFeedbackByIdAsync);
        group.MapDelete("/feedback/{id}", DeleteFeedbackAsync);
        group.MapGet("/feedbacks/user", ListUserFeedbacksAsync);
        group.MapGet("/feedbacks/list", ListAllFeedbacksAsync);
        group.MapGet("/feedbacks/all/export", ExportAllFeedbacksAsync);
        group.MapDelete("/feedbacks/all", DeleteAllFeedbacksAsync);
        group.MapPost("/arena/feedback", ArenaFeedbackAsync);
        group.MapGet("/leaderboard", LeaderboardAsync);

        return group;
    }

    private static async Task<IResult> ArenaFeedbackAsync(
        ArenaFeedbackRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (request.Winner is not ("a" or "b" or "tie" or "both_bad"))
        {
            return Results.BadRequest(
                new { detail = "Vencedor deve ser a, b, tie ou both_bad." });
        }

        var battle = await db.ArenaBattles
            .FirstOrDefaultAsync(b => b.Id == request.BattleId && b.UserId == user.Id, ct);
        if (battle is null)
        {
            return Results.NotFound(new { detail = "Batalha não encontrada." });
        }
        if (battle.Winner is not null)
        {
            return Results.BadRequest(new { detail = "Batalha já avaliada." });
        }

        battle.Winner = request.Winner;
        battle.VotedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);

        var (ratingA, ratingB) = await RatingsForAsync(
            battle.ModelA, battle.ModelB, db, ct);
        return Results.Ok(new ArenaFeedbackResponse(
            battle.ModelA, battle.ModelB, ratingA, ratingB));
    }

    private static async Task<IResult> LeaderboardAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var battles = await db.ArenaBattles.AsNoTracking()
            .Where(b => b.Winner != null)
            .OrderBy(b => b.VotedAt)
            .ToListAsync(ct);

        var ratings = new Dictionary<string, double>();
        var played = new Dictionary<string, int>();
        var wins = new Dictionary<string, double>();
        foreach (var battle in battles)
        {
            var score = battle.Winner switch
            {
                "a" => 1.0,
                "b" => 0.0,
                "tie" => 0.5,
                _ => -1.0, // both_bad: ambos perdem
            };
            ApplyElo(ratings, battle.ModelA, battle.ModelB, score);
            played[battle.ModelA] = played.GetValueOrDefault(battle.ModelA) + 1;
            played[battle.ModelB] = played.GetValueOrDefault(battle.ModelB) + 1;
            wins[battle.ModelA] = wins.GetValueOrDefault(battle.ModelA)
                + Math.Max(0.0, score);
            wins[battle.ModelB] = wins.GetValueOrDefault(battle.ModelB)
                + (score < 0 ? 0 : 1 - score);
        }

        return Results.Ok(ratings
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new LeaderboardEntryResponse(
                kv.Key, Math.Round(kv.Value, 1),
                played.GetValueOrDefault(kv.Key), wins.GetValueOrDefault(kv.Key)))
            .ToList());
    }

    private static async Task<(double, double)> RatingsForAsync(
        string modelA, string modelB, AppDbContext db, CancellationToken ct)
    {
        var battles = await db.ArenaBattles.AsNoTracking()
            .Where(b => b.Winner != null)
            .OrderBy(b => b.VotedAt)
            .ToListAsync(ct);
        var ratings = new Dictionary<string, double>();
        foreach (var battle in battles)
        {
            var score = battle.Winner switch
            {
                "a" => 1.0,
                "b" => 0.0,
                "tie" => 0.5,
                _ => -1.0,
            };
            ApplyElo(ratings, battle.ModelA, battle.ModelB, score);
        }
        return (ratings.GetValueOrDefault(modelA, 1000.0),
            ratings.GetValueOrDefault(modelB, 1000.0));
    }

    /// <summary>ELO K=32 partindo de 1000; score -1 (both_bad) penaliza os dois.</summary>
    internal static void ApplyElo(
        Dictionary<string, double> ratings, string modelA, string modelB, double score)
    {
        const double k = 32.0;
        var ra = ratings.GetValueOrDefault(modelA, 1000.0);
        var rb = ratings.GetValueOrDefault(modelB, 1000.0);
        var ea = 1.0 / (1.0 + Math.Pow(10, (rb - ra) / 400.0));
        var eb = 1.0 - ea;

        if (score >= 0)
        {
            ratings[modelA] = ra + k * (score - ea);
            ratings[modelB] = rb + k * ((1.0 - score) - eb);
        }
        else
        {
            ratings[modelA] = ra - k * ea;
            ratings[modelB] = rb - k * eb;
        }
    }

    private static async Task<IResult> SaveFeedbackAsync(
        FeedbackUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (request.Rating is not (1 or -1))
        {
            return Results.BadRequest(new { detail = "Avaliação deve ser 1 ou -1." });
        }

        var existing = await db.Feedbacks
            .FirstOrDefaultAsync(f => f.UserId == user.Id && f.MessageId == request.MessageId, ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (existing is null)
        {
            var feedback = new Feedback
            {
                UserId = user.Id,
                ChatId = request.ChatId,
                MessageId = request.MessageId,
                ModelId = request.ModelId,
                Rating = request.Rating,
                Reason = request.Reason,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Feedbacks.Add(feedback);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(feedback));
        }

        existing.Rating = request.Rating;
        existing.Reason = request.Reason;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(existing));
    }

    private static async Task<IResult> GetFeedbackAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var feedback = await db.Feedbacks.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.UserId == user!.Id, ct);
        return feedback is null ? Results.NotFound() : Results.Ok(ToResponse(feedback));
    }

    private static async Task<IResult> SaveFeedbackByIdAsync(
        FeedbackUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        return await SaveFeedbackAsync(request with { }, http, db, ct);
    }

    private static async Task<IResult> DeleteFeedbackAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var feedback = await db.Feedbacks
            .FirstOrDefaultAsync(f => f.Id == id && f.UserId == user!.Id, ct);
        if (feedback is null)
        {
            return Results.NotFound();
        }

        db.Feedbacks.Remove(feedback);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> ListUserFeedbacksAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var feedbacks = await db.Feedbacks.AsNoTracking()
            .Where(f => f.UserId == user.Id)
            .OrderByDescending(f => f.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(feedbacks.Select(ToResponse).ToList());
    }

    private static async Task<IResult> ListAllFeedbacksAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var feedbacks = await db.Feedbacks.AsNoTracking()
            .OrderByDescending(f => f.UpdatedAt)
            .ToListAsync(ct);
        var userNames = await db.Users.AsNoTracking()
            .Where(u => feedbacks.Select(f => f.UserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        return Results.Ok(feedbacks.Select(f => new AdminFeedbackResponse(
            f.Id, f.UserId, userNames.GetValueOrDefault(f.UserId, f.UserId),
            f.ChatId, f.MessageId, f.ModelId, f.Rating, f.Reason, f.CreatedAt)).ToList());
    }

    private static async Task<IResult> ExportAllFeedbacksAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var feedbacks = await db.Feedbacks.AsNoTracking().ToListAsync(ct);
        return Results.Ok(feedbacks.Select(ToResponse).ToList());
    }

    private static async Task<IResult> DeleteAllFeedbacksAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        await db.Feedbacks.Where(f => f.UserId == user.Id).ExecuteDeleteAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static FeedbackResponse ToResponse(Feedback f) =>
        new(f.Id, f.ChatId, f.MessageId, f.ModelId, f.Rating, f.Reason, f.CreatedAt);
}
