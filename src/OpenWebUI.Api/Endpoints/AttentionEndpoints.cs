using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Inbox de atenção unificado (SPEC-20261022-ade-surfaces RF-C3):
/// <c>GET /api/v1/attention</c> funde os itens persistidos
/// (<c>run:{id}</c> levantados pelo <c>AttentionObserver</c>,
/// <c>term:{id}</c> de sessões de terminal encerradas) com as aprovações
/// pendentes em memória (<c>chat:{id}</c> — mesma fonte do
/// <c>/chats/attention/count</c>). <c>POST /{key}/seen</c> dispensa um
/// item persistido; chaves de aprovação retornam 404 — elas saem quando
/// o usuário decide a run, não por dismiss.
/// </summary>
public static class AttentionEndpoints
{
    /// <summary>Mapeia o grupo <c>/api/v1/attention</c>.</summary>
    public static RouteGroupBuilder MapAttentionEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/attention").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPost("/{key}/seen", MarkSeenAsync);
        return group;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, ChatRunApprovals approvals,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var persisted = await AttentionService.ListAsync(db, user.Id, ct);

        // Aprovações pendentes viram itens sintéticos no topo — são as mais
        // urgentes (run parada esperando o dono).
        var awaiting = approvals.PendingChatIds();
        var approvalTitles = awaiting.Count == 0
            ? new Dictionary<string, string>()
            : await db.Chats.AsNoTracking()
                .Where(c => c.UserId == user.Id && awaiting.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Title, ct);

        var items = approvalTitles
            .Select(kv => new AttentionItemResponse(
                $"chat:{kv.Key}", "approval", kv.Value, kv.Key, 0))
            .Concat(persisted.Select(a => new AttentionItemResponse(
                a.Key, a.Kind, a.Title, a.ChatId, a.CreatedAt)))
            .ToList();

        return Results.Ok(items);
    }

    private static async Task<IResult> MarkSeenAsync(
        string key, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var removed = await AttentionService.MarkSeenAsync(
            db, user.Id, key, ct);
        return removed
            ? Results.Ok(new { status = "seen", key })
            : Results.NotFound(new { detail = "item de atenção não encontrado." });
    }
}
