using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Seam de sumarização para compactação de contexto (SPEC-20261010-context-compaction):
/// recebe o transcript do head e devolve um resumo em texto. A implementação real
/// chama o provider; testes injetam um fake.
/// </summary>
public interface IContextSummarizer
{
    /// <summary>Resume o trecho de conversa (formatado "role: content") num texto único.</summary>
    Task<string?> SummarizeAsync(string model, string transcript, CancellationToken ct);
}

/// <summary>Sumarizador real: completion única não-streaming no modelo da própria run.</summary>
public sealed class LlmContextSummarizer(ProviderService providers) : IContextSummarizer
{
    private const string Prompt =
        "Resuma esta conversa de forma compacta e fiel, preservando: decisões tomadas, " +
        "caminhos de arquivos citados, comandos executados e resultados, erros e correções, " +
        "tarefas pendentes e o objetivo atual. Responda só com o resumo, sem preâmbulo.\n\n";

    /// <inheritdoc />
    public async Task<string?> SummarizeAsync(string model, string transcript, CancellationToken ct)
    {
        var request = new ChatCompletionRequest(model,
            [new ChatCompletionMessage("user", Prompt + transcript)], Stream: false);
        var result = await providers.CompleteAsync(request, ct);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }
}

/// <summary>
/// Compactação de contexto para runs longas (SPEC-20261010-context-compaction):
/// quando o histórico passa do limiar, o head é resumido via LLM e
/// <c>{ cutoff, summary }</c> fica no kv <c>chat:{id}:compaction</c>. Runs
/// seguintes enviam <c>[resumo] + tail</c> em vez do histórico inteiro — as
/// mensagens originais permanecem no DB e a UI continua exibindo tudo; só a
/// janela enviada ao modelo é compactada. o histórico
/// são ordenados por Position (o cutoff é contagem de mensagens desde o início).
/// Best-effort: falha no summarizer nunca bloqueia a run.
/// </summary>
public sealed class ContextCompactionService(
    AppDbContext db,
    ConfigService config,
    IContextSummarizer summarizer,
    ModelRoleResolver roles,
    ILogger<ContextCompactionService> logger)
{
    /// <summary>Prefixo da mensagem sintética que injeta o resumo no histórico.</summary>
    public const string SummaryPrefix = "[Contexto compactado de mensagens anteriores]\n";

    private const int DefaultThresholdChars = 60_000;
    private const int DefaultTailMessages = 30;
    private const int DefaultTailChars = 25_000;

    private sealed record CompactionState(int Cutoff, string Summary);
    internal sealed record Thresholds(int TotalChars, int TailMessages, int TailChars);

    private static string KvKey(string chatId) => $"chat:{chatId}:compaction";

    /// <summary>
    /// Aplica a compactação persistida: com cutoff válido devolve
    /// <c>[ctx-summary] + mensagens após o cutoff</c>; senão devolve o histórico
    /// intacto.
    /// </summary>
    public async Task<List<ChatCompletionMessage>> ApplyAsync(
        string chatId, IReadOnlyList<ChatCompletionMessage> history, CancellationToken ct)
    {
        var state = await config.GetAsync<CompactionState?>(KvKey(chatId), null, ct);
        if (state is null || state.Cutoff <= 0 || state.Cutoff >= history.Count
            || string.IsNullOrWhiteSpace(state.Summary))
        {
            return [.. history];
        }
        var tail = history.Skip(state.Cutoff).ToList();
        tail.Insert(0, new ChatCompletionMessage("user", SummaryPrefix + state.Summary));
        return tail;
    }

    /// <summary>
    /// Compacta o histórico se ultrapassar o limiar: resume o trecho ainda não
    /// compactado (incluindo o resumo anterior como contexto), persiste kv e
    /// anexa um marcador <c>Role="compaction"</c> no chat. Retorna true quando
    /// compactou. Nunca lança — falha vira log e a run segue sem compactar.
    /// </summary>
    public async Task<bool> MaybeCompactAsync(
        string chatId, IReadOnlyList<ChatCompletionMessage> history,
        string model, CancellationToken ct, string? userId = null)
    {
        try
        {
            var t = await ThresholdsAsync(ct);
            var total = history.Sum(m => (m.Content?.Length ?? 0) + (m.ToolCallsJson?.Length ?? 0));
            if (total <= t.TotalChars)
            {
                return false;
            }

            var headEnd = SplitIndex(history, t);
            if (headEnd <= 0)
            {
                return false;
            }

            var prev = await config.GetAsync<CompactionState?>(KvKey(chatId), null, ct);
            var newHeadStart = prev is { Cutoff: > 0 } && prev.Cutoff < headEnd
                ? prev.Cutoff : 0;
            var transcript = (prev is not null && newHeadStart > 0
                    ? $"Resumo anterior já compactado: {prev.Summary}\n\n" : string.Empty)
                + string.Join("\n\n", history.Skip(newHeadStart).Take(headEnd - newHeadStart)
                    .Select(m => $"{m.Role}: {m.Content}"));

            // Papel "memory" (SPEC-20261013-model-roles): resumo roda no
            // modelo do papel quando configurado; senão no modelo da run.
            var summaryModel = userId is null
                ? model
                : await roles.ResolveAsync("memory", userId, model, ct);
            var summary = await summarizer.SummarizeAsync(summaryModel, transcript, ct);
            if (summary is null)
            {
                return false;
            }

            await config.SetAsync(KvKey(chatId), new CompactionState(headEnd, summary), ct);

            var tracked = await db.Chats.Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == chatId, ct);
            if (tracked is not null)
            {
                var pos = tracked.Messages.Count == 0 ? 0 : tracked.Messages.Max(m => m.Position) + 1;
                tracked.Messages.Add(new ChatMessage
                {
                    ChatId = tracked.Id, Role = "compaction", Content = summary,
                    Position = pos, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
                tracked.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await db.SaveChangesAsync(ct);
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Compactação de contexto falhou para chat {ChatId}.", chatId);
            return false;
        }
    }

    /// <summary>
    /// Índice de corte (mensagens mantidas no tail): recua do fim até cobrir
    /// <see cref="Thresholds.TailMessages"/> mensagens ou <see cref="Thresholds.TailChars"/>
    /// chars — e avança enquanto o primeiro item do tail for <c>tool</c>, para não
    /// separar respostas tool do assistant(tool_calls) que as gerou.
    /// </summary>
    internal static int SplitIndex(IReadOnlyList<ChatCompletionMessage> history, Thresholds t)
    {
        var tailCount = 0;
        var tailChars = 0;
        var idx = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            tailCount++;
            tailChars += (m.Content?.Length ?? 0) + (m.ToolCallsJson?.Length ?? 0);
            idx = i;
            if (tailCount >= t.TailMessages || tailChars >= t.TailChars)
            {
                break;
            }
        }
        while (idx < history.Count && history[idx].Role == "tool")
        {
            idx++;
        }
        return idx;
    }

    private async Task<Thresholds> ThresholdsAsync(CancellationToken ct)
    {
        var total = await config.GetAsync("compaction:threshold", DefaultThresholdChars, ct);
        var tailMsgs = await config.GetAsync("compaction:tail_messages", DefaultTailMessages, ct);
        var tailChars = await config.GetAsync("compaction:tail_chars", DefaultTailChars, ct);
        return new Thresholds(total, tailMsgs, tailChars);
    }
}
