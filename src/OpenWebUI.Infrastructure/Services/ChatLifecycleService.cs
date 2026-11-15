using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Ciclo de vida do chat para liberação de espaço
/// (SPEC-20261115-chat-lifecycle-meta): gera o resumo do histórico (LLM
/// via <see cref="IContextSummarizer"/> do compaction, com fallback
/// determinístico), persiste o <see cref="ChatArchiveMeta"/> que
/// sobrevive à exclusão do chat para o analytics, e purga o histórico
/// pesado (mensagens + runs/events/steers) mantendo os
/// <see cref="RunUsageRecord"/>s (métricas de relatório).
/// </summary>
public sealed class ChatLifecycleService(
    AppDbContext db,
    IContextSummarizer summarizer,
    ModelRoleResolver roles,
    ILogger<ChatLifecycleService> logger)
{
    /// <summary>Cap de caracteres do transcript enviado ao summarizer.</summary>
    internal const int TranscriptMaxChars = 32_000;

    /// <summary>Cap por mensagem no fallback determinístico.</summary>
    internal const int FallbackMessageChars = 500;

    /// <summary>Status de meta de chat arquivado.</summary>
    public const string StatusArchived = "archived";

    /// <summary>Status de meta de chat deletado.</summary>
    public const string StatusDeleted = "deleted";

    /// <summary>
    /// Resume o histórico do chat. Tenta o summarizer LLM (modelo do
    /// papel <c>tasks</c> com fallback ao primeiro modelo do chat); em
    /// qualquer falha devolve o resumo determinístico — nunca lança.
    /// Null quando o chat não tem mensagens.
    /// </summary>
    public async Task<string?> SummarizeAsync(Chat chat, CancellationToken ct)
    {
        var messages = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == chat.Id && m.Role != "compaction")
            .OrderBy(m => m.Position)
            .Select(m => new MessageLine(m.Role, m.Content))
            .ToListAsync(ct);
        if (messages.Count == 0)
        {
            return null;
        }

        var transcript = string.Join("\n",
            messages.Select(m => $"{m.Role}: {m.Content}"));
        if (transcript.Length > TranscriptMaxChars)
        {
            // Cauda recente tem mais valor de retomada que o head antigo.
            transcript = "…\n" + transcript[^TranscriptMaxChars..];
        }

        var model = FirstModel(chat);
        try
        {
            var resolved = await roles.ResolveAsync(
                "tasks", chat.UserId, model, ct);
            var digest = await summarizer.SummarizeAsync(
                resolved, transcript, chat.UserId, ct);
            if (digest is not null && !string.IsNullOrWhiteSpace(digest.Text))
            {
                return Render(digest);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Resumo LLM falhou para o chat {ChatId} — fallback determinístico.",
                chat.Id);
        }
        return DeterministicSummary(messages);
    }

    /// <summary>
    /// Upsert do meta do chat com título/status/resumo. PreserveSummary
    /// (delete sobre meta archived existente) mantém o resumo e o
    /// <c>ArchivedAt</c> originais; só atualiza status/<c>DeletedAt</c>
    /// e o título vigente.
    /// </summary>
    public async Task<ChatArchiveMeta> RecordMetaAsync(
        Chat chat, string status, string? summary, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var meta = await db.ChatArchiveMetas
            .FirstOrDefaultAsync(m => m.ChatId == chat.Id, ct);
        if (meta is null)
        {
            meta = new ChatArchiveMeta
            {
                ChatId = chat.Id,
                UserId = chat.UserId,
                CreatedAt = now,
                ArchivedAt = now,
            };
            db.ChatArchiveMetas.Add(meta);
        }
        meta.Title = chat.Title;
        meta.Status = status;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            meta.Summary = summary;
        }
        if (status == StatusDeleted)
        {
            meta.DeletedAt = now;
        }
        else
        {
            meta.ArchivedAt = now;
            meta.DeletedAt = null;
        }
        await db.SaveChangesAsync(ct);
        return meta;
    }

    /// <summary>
    /// Purga o histórico pesado do chat (RF-002): run events, steers,
    /// runs e mensagens. <see cref="RunUsageRecord"/>s ficam — são as
    /// métricas dos relatórios.
    /// </summary>
    public async Task PurgeHistoryAsync(string chatId, CancellationToken ct)
    {
        await db.ChatRunEvents
            .Where(e => db.ChatRuns
                .Where(r => r.ChatId == chatId)
                .Select(r => r.Id)
                .Contains(e.RunId))
            .ExecuteDeleteAsync(ct);
        await db.ChatRunSteers.Where(s => s.ChatId == chatId)
            .ExecuteDeleteAsync(ct);
        await db.ChatRuns.Where(r => r.ChatId == chatId)
            .ExecuteDeleteAsync(ct);
        await db.ChatMessages.Where(m => m.ChatId == chatId)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>Renderiza o digest estruturado em texto plano.</summary>
    internal static string Render(ContextSummary d)
    {
        var sb = new System.Text.StringBuilder(d.Text.Trim());
        if (!string.IsNullOrWhiteSpace(d.State))
        {
            sb.Append("\n\nEstado atual: ").Append(d.State.Trim());
        }
        AppendSection(sb, "Arquivos", d.Files);
        AppendSection(sb, "Decisões", d.Decisions);
        AppendSection(sb, "Pendências", d.Pending);
        AppendSection(sb, "Errors and fixes", d.Errors ?? []);
        if (d.NextStep is { } ns && !string.IsNullOrWhiteSpace(ns.Text))
        {
            sb.Append("\n\nPróximo passo: ").Append(ns.Text.Trim());
            if (!string.IsNullOrWhiteSpace(ns.Quote))
            {
                sb.Append("\n> \"").Append(ns.Quote.Trim()).Append('"');
            }
        }
        return sb.ToString();
    }

    private static void AppendSection(
        System.Text.StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }
        sb.Append("\n\n").Append(title).Append(':');
        foreach (var item in items)
        {
            sb.Append("\n- ").Append(item);
        }
    }

    /// <summary>
    /// Resumo determinístico (fallback sem LLM): primeiro pedido do
    /// usuário + últimas 2 mensagens de conteúdo, truncadas.
    /// </summary>
    private static string DeterministicSummary(
        List<MessageLine> messages)
    {
        var sb = new System.Text.StringBuilder("Resumo (automático):");
        var first = messages.FirstOrDefault(m => m.Role == "user");
        if (first is not null)
        {
            sb.Append("\n- Pedido inicial: ").Append(Truncate(first.Content));
        }
        foreach (var m in messages.TakeLast(2))
        {
            sb.Append("\n- ").Append(m.Role).Append(": ")
                .Append(Truncate(m.Content));
        }
        return sb.ToString();
    }

    /// <summary>Linha mínima do transcript para resumo.</summary>
    internal sealed record MessageLine(string Role, string Content);

    private static string Truncate(string s) =>
        s.Length <= FallbackMessageChars ? s : s[..FallbackMessageChars] + "…";

    private static string FirstModel(Chat chat)
    {
        try
        {
            var models = JsonSerializer.Deserialize<List<string>>(chat.ModelsJson);
            return models is { Count: > 0 } ? models[0] : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
