using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Inbox persistido de atenção do usuário (SPEC-20261022-ade-surfaces RF-C1).
/// Complementa as aprovações em memória de <c>ChatRunApprovals</c>: itens
/// <c>run:{id}</c>/<c>term:{id}</c> sobrevivem a restart do servidor e saem
/// quando vistos (<see cref="MarkSeenAsync"/>) ou quando o chat de origem é
/// deletado. Rearmar a mesma origem faz upsert — a chave é única por usuário.
/// </summary>
public static class AttentionService
{
    /// <summary>Arma (ou rearma) um item de atenção para o usuário.</summary>
    public static async Task<UserAttention> RaiseAsync(
        AppDbContext db,
        string userId,
        string key,
        string kind,
        string title,
        string? chatId,
        CancellationToken ct)
    {
        var item = await db.UserAttentions
            .FirstOrDefaultAsync(a => a.UserId == userId && a.Key == key, ct);
        if (item is null)
        {
            item = new UserAttention
            {
                UserId = userId,
                Key = key,
                Kind = kind,
                Title = title,
                ChatId = chatId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            db.UserAttentions.Add(item);
        }
        else
        {
            item.Kind = kind;
            item.Title = title;
            item.ChatId = chatId;
            item.CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        await db.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>Lista os itens do inbox do usuário, mais recentes primeiro.</summary>
    public static Task<List<UserAttention>> ListAsync(
        AppDbContext db, string userId, CancellationToken ct) =>
        db.UserAttentions.AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Quantidade de itens persistidos (para o badge).</summary>
    public static Task<int> CountAsync(
        AppDbContext db, string userId, CancellationToken ct) =>
        db.UserAttentions.CountAsync(a => a.UserId == userId, ct);

    /// <summary>
    /// Marca como visto: remove o item. Retorna false quando a chave não
    /// pertence ao usuário (ou já foi vista) — o endpoint devolve 404.
    /// </summary>
    public static async Task<bool> MarkSeenAsync(
        AppDbContext db, string userId, string key, CancellationToken ct)
    {
        var removed = await db.UserAttentions
            .Where(a => a.UserId == userId && a.Key == key)
            .ExecuteDeleteAsync(ct);
        return removed > 0;
    }

    /// <summary>
    /// Marca como vistos todos os itens do usuário ligados a um chat —
    /// chamado quando o dono abre a conversa, para o badge não contar
    /// pendências já lidas.
    /// </summary>
    public static Task MarkChatSeenAsync(
        AppDbContext db, string userId, string chatId, CancellationToken ct) =>
        db.UserAttentions
            .Where(a => a.UserId == userId && a.ChatId == chatId)
            .ExecuteDeleteAsync(ct);

    /// <summary>Limpa os itens ligados a um chat (delete de conversa).</summary>
    public static Task RemoveForChatAsync(
        AppDbContext db, string chatId, CancellationToken ct) =>
        db.UserAttentions
            .Where(a => a.ChatId == chatId)
            .ExecuteDeleteAsync(ct);
}
