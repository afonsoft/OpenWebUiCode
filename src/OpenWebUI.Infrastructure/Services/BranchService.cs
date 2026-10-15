using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Árvore de conversa (SPEC-20261013-chat-branching): cada chat é uma árvore
/// de <see cref="ChatMessage"/> ligada por <see cref="ChatMessage.ParentId"/>.
/// O caminho ativo é o walk <see cref="Chat.LeafMessageId"/>→raiz; nós com
/// <c>ParentId == null</c> (raiz ou dado legado) herdam a aresta implícita
/// "maior <see cref="ChatMessage.Position"/> anterior sem pai explícito",
/// o que mantém chats lineares antigos funcionando sem backfill.
/// </summary>
public sealed class BranchService(
    AppDbContext db,
    ConfigService config,
    IContextSummarizer summarizer,
    ModelRoleResolver roles,
    ILogger<BranchService> logger)
{
    /// <summary>Mínimo de mensagens no ramo abandonado para gerar resumo (RF-005).</summary>
    public const int BranchSummaryMinMessages = 4;

    /// <summary>Limite de caracteres do transcript enviado ao summarizer.</summary>
    private const int SummaryTranscriptMaxChars = 12_000;

    /// <summary>Chave kv do resumo de um leaf: <c>chat:{id}:branch:{leafId}</c>.</summary>
    public static string SummaryKey(string chatId, string leafId) =>
        $"chat:{chatId}:branch:{leafId}";

    /// <summary>
    /// Resolve a mensagem-leaf efetiva do chat: <see cref="Chat.LeafMessageId"/>
    /// quando aponta para mensagem existente; senão a maior Position (legado).
    /// </summary>
    public static ChatMessage? ResolveLeaf(Chat chat)
    {
        if (chat.Messages.Count == 0)
        {
            return null;
        }
        if (chat.LeafMessageId is not null)
        {
            var leaf = chat.Messages.FirstOrDefault(m => m.Id == chat.LeafMessageId);
            if (leaf is not null)
            {
                return leaf;
            }
        }
        return chat.Messages.MaxBy(m => m.Position);
    }

    /// <summary>
    /// Caminho ativo raiz→leaf: segue <c>ParentId</c> enquanto houver aresta
    /// explícita; nó sem pai cai na aresta legada (maior Position anterior
    /// dentre os também sem pai). Ciclos/dangling caem na aresta legada —
    /// nunca lança, nunca repete nó.
    /// </summary>
    public static List<ChatMessage> WalkPath(Chat chat)
    {
        var leaf = ResolveLeaf(chat);
        if (leaf is null)
        {
            return [];
        }

        var byId = chat.Messages
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var legacyByPosition = chat.Messages
            .Where(m => m.ParentId is null)
            .OrderBy(m => m.Position)
            .ToList();

        var path = new List<ChatMessage>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        ChatMessage? cursor = leaf;
        while (cursor is not null && visited.Add(cursor.Id))
        {
            path.Add(cursor);
            cursor = NextParent(cursor, byId, legacyByPosition);
        }
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Próximo nó subindo (cursor→pai): aresta explícita quando resolve dentro
    /// do chat, senão a legada (maior Position anterior sem pai explícito).
    /// </summary>
    private static ChatMessage? NextParent(
        ChatMessage cursor,
        IReadOnlyDictionary<string, ChatMessage> byId,
        IReadOnlyList<ChatMessage> legacyByPosition)
    {
        if (cursor.ParentId is not null
            && byId.TryGetValue(cursor.ParentId, out var parent))
        {
            return parent;
        }
        // Aresta legada: cursor sem pai (ou pai danificado) desce para o maior
        // Position anterior dentre os nós também sem pai — replica a cadeia
        // linear pré-branching sem exigir backfill.
        ChatMessage? candidate = null;
        foreach (var m in legacyByPosition)
        {
            if (m.Position >= cursor.Position)
            {
                break;
            }
            candidate = m;
        }
        return candidate;
    }

    /// <summary>
    /// Filhos de um nó na árvore: explícitos (<c>ParentId == id</c>) mais o
    /// filho legado implícito — o próximo nó sem pai por Position, quando o
    /// próprio nó é sem-pai.
    /// </summary>
    private static bool HasChildren(
        ChatMessage node,
        ISet<string> explicitParents,
        IReadOnlyList<ChatMessage> legacyByPosition)
    {
        if (explicitParents.Contains(node.Id))
        {
            return true;
        }
        if (node.ParentId is not null)
        {
            return false;
        }
        return legacyByPosition.Any(m => m.Position > node.Position);
    }

    /// <summary>
    /// Leafs divergentes do chat (nós sem filhos, explícitos ou implícitos).
    /// Chat linear/legado tem exatamente um leaf (maior Position).
    /// </summary>
    public static List<ChatMessage> ListLeaves(Chat chat)
    {
        if (chat.Messages.Count == 0)
        {
            return [];
        }
        var legacyByPosition = chat.Messages
            .Where(m => m.ParentId is null)
            .OrderBy(m => m.Position)
            .ToList();
        var explicitParents = chat.Messages
            .Where(m => m.ParentId is not null)
            .Select(m => m.ParentId!)
            .ToHashSet(StringComparer.Ordinal);
        return chat.Messages
            .Where(m => !HasChildren(m, explicitParents, legacyByPosition))
            .ToList();
    }

    /// <summary>
    /// Leafs divergentes do chat (nós sem filhos, explícitos ou implícitos),
    /// com profundidade, contagem do caminho, preview e flag de ativo.
    /// </summary>
    public async Task<List<BranchLeafInfo>> ListBranchesAsync(Chat chat, CancellationToken ct)
    {
        if (chat.Messages.Count == 0)
        {
            return [];
        }

        var byId = chat.Messages
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var legacyByPosition = chat.Messages
            .Where(m => m.ParentId is null)
            .OrderBy(m => m.Position)
            .ToList();

        var activeLeaf = ResolveLeaf(chat);
        var leaves = ListLeaves(chat);

        var result = new List<BranchLeafInfo>(leaves.Count);
        foreach (var leaf in leaves.OrderByDescending(m => m.Timestamp))
        {
            var path = WalkPathFrom(leaf, byId, legacyByPosition);
            var summary = await GetBranchSummaryAsync(chat.Id, leaf.Id, ct);
            result.Add(new BranchLeafInfo(
                leaf.Id,
                Math.Max(0, path.Count - 1),
                path.Count,
                Preview(leaf.Content),
                summary,
                leaf.Timestamp,
                activeLeaf?.Id == leaf.Id));
        }
        return result;
    }

    /// <summary>Walk a partir de um leaf arbitrário (mesmo algoritmo do caminho ativo).</summary>
    private static List<ChatMessage> WalkPathFrom(
        ChatMessage leaf,
        IReadOnlyDictionary<string, ChatMessage> byId,
        IReadOnlyList<ChatMessage> legacyByPosition)
    {
        var path = new List<ChatMessage>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        ChatMessage? cursor = leaf;
        while (cursor is not null && visited.Add(cursor.Id))
        {
            path.Add(cursor);
            cursor = NextParent(cursor, byId, legacyByPosition);
        }
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Ativa <paramref name="leafId"/> como nova ponta do chat. Ao sair de um
    /// ramo com ≥ <see cref="BranchSummaryMinMessages"/> mensagens, gera o
    /// resumo (RF-005) no modelo do papel <c>memory</c> (fallback: modelo do
    /// chat) e grava no kv — falha de resumo nunca bloqueia a troca.
    /// </summary>
    /// <returns><c>false</c> quando o leaf não pertence ao chat.</returns>
    public async Task<bool> ActivateLeafAsync(
        Chat chat, string leafId, string? chatModel, CancellationToken ct)
    {
        var target = chat.Messages
            .FirstOrDefault(m => m.Id == leafId && m.ChatId == chat.Id);
        if (target is null)
        {
            return false;
        }

        var abandoned = ResolveLeaf(chat);
        if (abandoned is not null && abandoned.Id != target.Id)
        {
            await SummarizeAbandonedAsync(chat, abandoned, chatModel, ct);
        }

        chat.LeafMessageId = target.Id;
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Resumo persistido do leaf (RF-005) — <c>null</c> quando não há.
    /// </summary>
    public async Task<string?> GetBranchSummaryAsync(
        string chatId, string leafId, CancellationToken ct) =>
        await config.GetAsync<string?>(SummaryKey(chatId, leafId), null, ct);

    /// <summary>
    /// Gera e persiste o resumo do ramo abandonado; sempre sobrescreve —
    /// se o ramo ganhou mensagens desde a última saída, o resumo é novo.
    /// </summary>
    private async Task SummarizeAbandonedAsync(
        Chat chat, ChatMessage leaf, string? chatModel, CancellationToken ct)
    {
        var byId = chat.Messages
            .GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var legacyByPosition = chat.Messages
            .Where(m => m.ParentId is null)
            .OrderBy(m => m.Position)
            .ToList();
        var path = WalkPathFrom(leaf, byId, legacyByPosition);
        if (path.Count < BranchSummaryMinMessages)
        {
            return;
        }

        try
        {
            var transcript = string.Join('\n', path
                .Select(m => $"{m.Role}: {m.Content}"));
            if (transcript.Length > SummaryTranscriptMaxChars)
            {
                transcript = transcript[^SummaryTranscriptMaxChars..];
            }

            var model = await roles.ResolveAsync(
                "memory", chat.UserId,
                chatModel ?? path.LastOrDefault(m => m.Model is not null)?.Model ?? string.Empty,
                ct);
            var summary = await summarizer.SummarizeAsync(model, transcript, chat.UserId, ct);
            var text = summary?.Text?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                await config.SetAsync(SummaryKey(chat.Id, leaf.Id), text, ct);
            }
        }
        catch (Exception ex)
        {
            // RF-005: falha de resumo nunca falha a troca de ramo.
            logger.LogDebug(ex, "Falha ao resumir ramo {Leaf} do chat {Chat}.", leaf.Id, chat.Id);
        }
    }

    /// <summary>Preview compacto do leaf para o popover (primeiras palavras).</summary>
    private static string Preview(string content)
    {
        var clean = content.Trim();
        return clean.Length <= 96 ? clean : string.Concat(clean.AsSpan(0, 96), "…");
    }
}

/// <summary>Leaf divergente de um chat para <c>GET /branches</c> (RF-003).</summary>
public sealed record BranchLeafInfo(
    string LeafId,
    int Depth,
    int MessageCount,
    string Preview,
    string? Summary,
    long UpdatedAt,
    bool IsActive);
