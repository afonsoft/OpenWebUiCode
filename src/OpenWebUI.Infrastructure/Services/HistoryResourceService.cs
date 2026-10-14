using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Pseudo-recurso <c>history://</c> (SPEC-20261014-context-maintenance-v2
/// RF-003): expõe o histórico <b>persistido</b> do chat — incluindo
/// mensagens elididas/compactadas fora da janela — para leitura por tools
/// (<c>file_read</c>, <c>file_grep</c>). Somente leitura, escopo do dono
/// do chat; saída truncada a <see cref="MaxBytes"/>.
///
/// Gramática: <c>history://current|{chatId}[/tail:{n}|/msg:{id}|/call:{toolCallId}]</c>.
/// </summary>
public sealed class HistoryResourceService(AppDbContext db)
{
    /// <summary>Teto do transcript resolvido (32KB — RF-003).</summary>
    public const int MaxBytes = 32 * 1024;

    private const string Scheme = "history://";

    /// <summary>Se <paramref name="path"/> usa o esquema <c>history://</c>.</summary>
    public static bool IsHistoryUri(string? path) =>
        path?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Resolve o URI em transcript textual
    /// (<c>[{pos}] {role}: {content}</c> por linha). Erros voltam como
    /// string de detalhe — a tool nunca lança.
    /// </summary>
    /// <param name="uri">URI history:// completo.</param>
    /// <param name="userId">Dono da sessão (RBAC — só o dono lê).</param>
    /// <param name="currentChatId">Chat da run quando o URI usa <c>current</c>.</param>
    public async Task<(string? Text, string? Error)> ResolveAsync(
        string uri, string userId, string? currentChatId, CancellationToken ct)
    {
        var rest = uri[Scheme.Length..];
        var slash = rest.IndexOf('/');
        var chatPart = slash >= 0 ? rest[..slash] : rest;
        var selector = slash >= 0 ? rest[(slash + 1)..] : null;

        var chatId = string.Equals(chatPart, "current", StringComparison.OrdinalIgnoreCase)
            ? currentChatId
            : chatPart;
        if (string.IsNullOrWhiteSpace(chatId))
        {
            return (null, "history:// sem chat — a run atual não tem chat associado.");
        }

        var chat = await db.Chats.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null || chat.UserId != userId)
        {
            return (null, $"Chat '{chatId}' não encontrado.");
        }

        var query = db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == chatId)
            .OrderBy(m => m.Position);

        List<ChatMessage> messages;
        if (selector is null)
        {
            messages = await query.ToListAsync(ct);
        }
        else if (selector.StartsWith("tail:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(selector[5..], out var n) && n > 0)
        {
            var all = await query.Select(m => m.Id).ToListAsync(ct);
            var skip = Math.Max(0, all.Count - Math.Min(n, 500));
            messages = await query.Skip(skip).ToListAsync(ct);
        }
        else if (selector.StartsWith("msg:", StringComparison.OrdinalIgnoreCase))
        {
            var id = selector[4..];
            var msg = await query.FirstOrDefaultAsync(m => m.Id == id, ct);
            messages = msg is null ? [] : [msg];
            if (msg is null)
            {
                return (null, $"Mensagem '{id}' não existe no histórico de '{chatId}'.");
            }
        }
        else if (selector.StartsWith("call:", StringComparison.OrdinalIgnoreCase))
        {
            var callId = selector[5..];
            var msg = await query.FirstOrDefaultAsync(m => m.ToolCallId == callId, ct);
            messages = msg is null ? [] : [msg];
            if (msg is null)
            {
                return (null, $"Nenhum tool result com call id '{callId}' em '{chatId}'.");
            }
        }
        else
        {
            return (null,
                $"Seletor '{selector}' inválido — use tail:{{n}}, msg:{{id}} ou call:{{id}}.");
        }

        return (Format(messages), null);
    }

    /// <summary>
    /// Transcript textual <c>[{pos}] {role}: {content}</c> truncado a
    /// <see cref="MaxBytes"/> com marcador de continuação.
    /// </summary>
    internal static string Format(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        var emitted = 0;
        foreach (var m in messages)
        {
            var line = $"[{m.Position}] {m.Role}: {m.Content}\n";
            if (sb.Length + line.Length > MaxBytes)
            {
                break;
            }
            sb.Append(line);
            emitted++;
        }
        if (emitted < messages.Count)
        {
            sb.Append($"[truncado — restam {messages.Count - emitted} mensagens; refine com tail:{{n}}]");
        }
        return sb.ToString().TrimEnd();
    }
}
