using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Planos persistidos da workspace (SPEC-20261031-plan-mode-v2 RF-005):
/// lista os <c>.md</c> do diretório <c>plans/</c> do repo vinculado —
/// escritos por <c>builtin_write_plan</c>.
/// </summary>
public static class PlanEndpoints
{
    /// <summary>Mapeia as rotas de planos dentro do grupo workspace/repo.</summary>
    public static void MapPlanEndpoints(this IEndpointRouteBuilder app)
    {
        var repo = app.MapGroup("/api/v1/workspace/repo").RequireAuthorization();
        repo.MapGet("/plans", ListAsync);
    }

    /// <summary>
    /// <c>GET /api/v1/workspace/repo/plans</c> — lista
    /// <c>{name, path, title, modifiedAt}</c> dos planos do repo vinculado
    /// (mais recente primeiro).
    /// </summary>
    private static async Task<IResult> ListAsync(
        string? chatId, HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, PlanFileService plans,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (chatId is not null
            && !await db.Chats.AsNoTracking()
                .AnyAsync(c => c.Id == chatId && c.UserId == user.Id, ct))
        {
            return Results.NotFound(new { detail = "Chat não encontrado." });
        }
        var binding = await repos.ResolveBindingAsync(user.Id, chatId, ct);
        if (binding.Binding is null)
        {
            return Results.NotFound(
                new { detail = "Nenhum repositório vinculado.", bound = false });
        }
        var workdir = await repos.ResolveWorkdirAsync(user.Id, chatId, ct);
        var items = plans.List(workdir)
            .Select(e => new PlanItemResponse(
                e.Name, e.Path, e.Title, e.ModifiedAt))
            .ToList();
        return Results.Ok(items);
    }

    /// <summary>Item da listagem de planos.</summary>
    public sealed record PlanItemResponse(
        string Name, string Path, string? Title, DateTime ModifiedAt);
}
