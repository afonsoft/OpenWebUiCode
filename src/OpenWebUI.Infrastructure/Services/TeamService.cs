using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Roster/status de agent teams (SPEC-20261021-harness-seams RF-E1/E2):
/// o team é identificado pelo chat raiz (<see cref="Chat.TeamId"/>) — sem
/// entidade Team separada. Resolver sobe <see cref="Chat.ParentChatId"/>
/// até a raiz para chats legados sem a coluna.
/// </summary>
public static class TeamService
{
    /// <summary>Proteção contra cadeia corrompida/loop.</summary>
    private const int MaxHops = 32;

    /// <summary>Membro do roster: chat + papel + run ativa, quando houver.</summary>
    public sealed record TeamMember(
        string ChatId, string Role, string Title, string? ActiveRunId);

    /// <summary>Snapshot do team para o endpoint e para <c>team_status</c>.</summary>
    public sealed record TeamStatus(
        string TeamId,
        IReadOnlyList<TeamMember> Members,
        IReadOnlyList<TeamTask> Tasks);

    /// <summary>
    /// Resolve o TeamId efetivo do chat: a coluna quando presente; senão
    /// sobe <see cref="Chat.ParentChatId"/> até a raiz (chats de team
    /// criados antes da coluna). Retorna null quando o chat (e a raiz)
    /// não pertencem a um team.
    /// </summary>
    public static async Task<string?> ResolveTeamIdAsync(
        AppDbContext db, string chatId, CancellationToken ct)
    {
        var chat = await db.Chats.AsNoTracking()
            .Where(c => c.Id == chatId)
            .Select(c => new { c.Id, c.ParentChatId, c.TeamId })
            .FirstOrDefaultAsync(ct);
        if (chat is null)
        {
            return null;
        }
        if (chat.TeamId is not null)
        {
            return chat.TeamId;
        }

        // Sem coluna: sobe até a raiz e usa o TeamId dela (raiz de team
        // aponta para si; raiz comum retorna o próprio id quando algum
        // descendente tiver TeamId — aqui ninguém tem, então a raiz só é
        // team se TeamId == Id).
        var current = chat;
        for (var hop = 0; hop < MaxHops && current.ParentChatId is not null; hop++)
        {
            var parent = await db.Chats.AsNoTracking()
                .Where(c => c.Id == current.ParentChatId)
                .Select(c => new { c.Id, c.ParentChatId, c.TeamId })
                .FirstOrDefaultAsync(ct);
            if (parent is null)
            {
                return null;
            }
            if (parent.TeamId is not null)
            {
                return parent.TeamId;
            }
            current = parent;
        }
        return current.TeamId == current.Id ? current.Id : null;
    }

    /// <summary>
    /// Garante o TeamId na raiz quando <c>delegate_task team:true</c> cria
    /// o primeiro membro: a raiz ganha <c>TeamId = próprio Id</c> +
    /// <c>leader</c>; o filho herda o id resolvido. Idempotente.
    /// </summary>
    /// <returns>TeamId efetivo (id da raiz do team).</returns>
    public static async Task<string> EnsureRootTeamAsync(
        AppDbContext db, Chat parentChat, CancellationToken ct)
    {
        var resolved = await ResolveTeamIdAsync(db, parentChat.Id, ct);
        var teamId = resolved ?? (await RootChatIdAsync(db, parentChat, ct));

        var root = await db.Chats.FirstOrDefaultAsync(c => c.Id == teamId, ct);
        if (root is not null && root.TeamId is null)
        {
            root.TeamId = root.Id;
            root.TeamRole ??= "leader";
            await db.SaveChangesAsync(ct);
        }
        return teamId;
    }

    /// <summary>Sobe a cadeia <see cref="Chat.ParentChatId"/> até a raiz.</summary>
    public static async Task<string> RootChatIdAsync(
        AppDbContext db, Chat chat, CancellationToken ct)
    {
        var current = chat;
        for (var hop = 0; hop < MaxHops && current.ParentChatId is not null; hop++)
        {
            var parent = await db.Chats.AsNoTracking()
                .Where(c => c.Id == current.ParentChatId)
                .Select(c => new Chat { Id = c.Id, ParentChatId = c.ParentChatId })
                .FirstOrDefaultAsync(ct);
            if (parent is null)
            {
                break;
            }
            current = parent;
        }
        return current.Id;
    }

    /// <summary>
    /// Monta o roster + tasks + runs vivas do team. O chat raiz aparece
    /// como leader mesmo sem coluna gravada (teams legados).
    /// </summary>
    public static async Task<TeamStatus?> BuildStatusAsync(
        AppDbContext db, string teamId, CancellationToken ct)
    {
        var members = await db.Chats.AsNoTracking()
            .Where(c => c.TeamId == teamId || c.Id == teamId)
            .Select(c => new { c.Id, c.Title, Role = c.TeamRole ?? "leader" })
            .ToListAsync(ct);
        if (members.Count == 0)
        {
            return null;
        }

        var chatIds = members.Select(m => m.Id).ToList();
        var activeRuns = await db.ChatRuns.AsNoTracking()
            .Where(r => chatIds.Contains(r.ChatId)
                && (r.Status == ChatRunStatus.Queued
                    || r.Status == ChatRunStatus.Running
                    || r.Status == ChatRunStatus.Paused))
            .Select(r => new { r.ChatId, r.Id })
            .ToListAsync(ct);
        var tasks = await db.TeamTasks.AsNoTracking()
            .Where(t => t.TeamId == teamId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);

        return new TeamStatus(
            teamId,
            members.Select(m => new TeamMember(
                m.Id, m.Role, m.Title,
                activeRuns.FirstOrDefault(r => r.ChatId == m.Id)?.Id))
                .ToList(),
            tasks);
    }
}
