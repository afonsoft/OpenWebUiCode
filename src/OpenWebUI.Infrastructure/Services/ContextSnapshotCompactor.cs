using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Compactação de itens de snapshot sob budget de tokens
/// (SPEC-20261020-context-snapshots RF-005/RF-006): opera sobre uma CÓPIA
/// — o snapshot persistido nunca é alterado. Pipeline: elision via
/// <see cref="ContextPruner"/> (itens projetados como mensagens, sem
/// reimplementar regras) → sumarização via <see cref="IContextSummarizer"/>
/// → corte ordenado (passage de menor score, depois tool_output mais
/// antigo). <c>decision</c>/<c>note</c>/<c>summary</c> nunca saem; se só
/// restarem protegidos acima do budget, <c>WithinBudget=false</c>.
/// </summary>
internal static class ContextSnapshotCompactor
{
    /// <summary>Cap do transcript enviado ao sumarizador (protege a janela).</summary>
    private const int SummaryTranscriptChars = 40_000;

    /// <summary>Estratégias válidas (RF-005).</summary>
    internal static readonly HashSet<string> Strategies = new(StringComparer.Ordinal)
    {
        "none", "elision", "elision+summary",
    };

    /// <summary>
    /// Estatísticas da compactação (RF-005): contagens de itens elididos/
    /// cortados, flags de sumarização e se o budget final foi atendido.
    /// </summary>
    internal sealed record CompactionStats(
        int TokensBefore, int TokensAfter,
        int DroppedCount, int ElidedCount,
        bool SummaryUsed, bool SummaryFallback, bool WithinBudget);

    /// <summary>Aplica a estratégia + budget sobre uma cópia dos itens.</summary>
    /// <param name="items">Itens de origem (não são modificados).</param>
    /// <param name="strategy">none | elision | elision+summary.</param>
    /// <param name="tokenBudget">Budget já clampado (RF-006).</param>
    /// <param name="summarizer">Seam de sumarização; pode ser null.</param>
    /// <param name="model">Modelo da run (ctx.Model → fallback "memory").</param>
    /// <param name="userId">Dono do chat (BYOK do provider).</param>
    /// <param name="chatId">Chat dono — compõe o pointer history:// do stub.</param>
    /// <param name="summaryTimeout">Timeout da sumarização (KV clampada).</param>
    public sealed record BudgetRequest(
        IReadOnlyList<ContextSnapshotService.SnapshotItem> Items,
        string Strategy, int TokenBudget,
        IContextSummarizer? Summarizer, string? Model, string? UserId,
        string ChatId, TimeSpan SummaryTimeout);

    public static async Task<(
        List<ContextSnapshotService.SnapshotItem> Items,
        CompactionStats Stats)> ApplyBudgetAsync(
        BudgetRequest b, CancellationToken ct)
    {
        var items = b.Items; var strategy = b.Strategy;
        var tokenBudget = b.TokenBudget; var summarizer = b.Summarizer;
        var model = b.Model; var userId = b.UserId; var chatId = b.ChatId;
        var summaryTimeout = b.SummaryTimeout;
        var work = items.ToList();
        var tokensBefore = ContextSnapshotService.EstimateTokens(work);
        var elided = 0;
        var dropped = 0;
        var summaryUsed = false;
        var summaryFallback = false;

        // 1) Elision (RF-005): projeta itens como mensagens e reusa as
        // regras do ContextPruner — só tool_output (role "tool") é tocado.
        if (strategy is "elision" or "elision+summary"
            && ContextSnapshotService.EstimateTokens(work) > tokenBudget)
        {
            var projected = work.Select(ToMessage).ToList();
            var pruned = ContextPruner.Prune(projected, chatId);
            for (var i = 0; i < work.Count; i++)
            {
                if (!string.Equals(pruned[i].Content, projected[i].Content, StringComparison.Ordinal))
                {
                    work[i] = work[i] with { Content = pruned[i].Content, Elided = true };
                    elided++;
                }
            }
        }

        // 2) Sumarização (RF-005/RF-002): falha do provider vira fallback,
        // nunca erro — mesmo comportamento da compactação automática.
        if (string.Equals(strategy, "elision+summary", StringComparison.Ordinal)
            && ContextSnapshotService.EstimateTokens(work) > tokenBudget
            && summarizer is not null && !string.IsNullOrWhiteSpace(model))
        {
            var (transcript, covered) = BuildTranscript(work);
            if (covered.Count > 0)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(summaryTimeout);
                    var s = await summarizer.SummarizeAsync(
                        model, transcript, userId, cts.Token);
                    if (s is null)
                    {
                        summaryFallback = true;
                    }
                    else
                    {
                        // Remove só os itens que entraram no transcript e
                        // insere o resumo na posição do primeiro deles.
                        var insertAt = work.IndexOf(covered[0]);
                        foreach (var c in covered)
                        {
                            work.Remove(c);
                        }
                        work.Insert(insertAt, new ContextSnapshotService.SnapshotItem(
                            "summary", "resumo", FormatSummary(s)));
                        summaryUsed = true;
                    }
                }
                catch (Exception)
                {
                    summaryFallback = true;
                }
            }
        }

        // 3) Corte ordenado (RF-006): passage de menor score primeiro,
        // depois tool_output mais antigo; protegidos nunca saem.
        while (ContextSnapshotService.EstimateTokens(work) > tokenBudget)
        {
            var idx = LowestScorePassageIndex(work);
            if (idx < 0)
            {
                idx = work.FindIndex(i => i.Kind == "tool_output");
            }
            if (idx < 0)
            {
                break; // só restam protegidos
            }
            work.RemoveAt(idx);
            dropped++;
        }

        var tokensAfter = ContextSnapshotService.EstimateTokens(work);
        return (work, new CompactionStats(
            tokensBefore, tokensAfter, dropped, elided,
            summaryUsed, summaryFallback, tokensAfter <= tokenBudget));
    }

    private static ChatCompletionMessage ToMessage(
        ContextSnapshotService.SnapshotItem item) =>
        new(item.Kind == "tool_output" ? "tool" : "user",
            item.Title is { Length: > 0 } t ? $"{t}\n{item.Content}" : item.Content);

    /// <summary>Monta o transcript dos itens elidíveis até o cap; devolve os cobertos.</summary>
    private static (string Transcript, List<ContextSnapshotService.SnapshotItem> Covered)
        BuildTranscript(List<ContextSnapshotService.SnapshotItem> work)
    {
        var sb = new System.Text.StringBuilder();
        var covered = new List<ContextSnapshotService.SnapshotItem>();
        foreach (var item in work)
        {
            if (ContextSnapshotService.ProtectedKinds.Contains(item.Kind))
            {
                continue;
            }
            var block = item.Title is { Length: > 0 } t
                ? $"## {item.Kind}: {t}\n{item.Content}\n\n"
                : $"## {item.Kind}\n{item.Content}\n\n";
            if (sb.Length + block.Length > SummaryTranscriptChars)
            {
                break; // itens além do cap ficam no resultado (corte decide)
            }
            sb.Append(block);
            covered.Add(item);
        }
        return (sb.ToString(), covered);
    }

    /// <summary>Resumo estruturado → conteúdo do item summary (campos já vêm do provider).</summary>
    private static string FormatSummary(ContextSummary s)
    {
        var sb = new System.Text.StringBuilder(s.Text);
        AppendList(sb, "arquivos", s.Files);
        AppendList(sb, "decisões", s.Decisions);
        AppendList(sb, "pendências", s.Pending);
        return sb.ToString().TrimEnd();

        static void AppendList(
            System.Text.StringBuilder sb, string header, IReadOnlyList<string> xs)
        {
            if (xs.Count == 0)
            {
                return;
            }
            sb.Append("\n\n").Append(header).Append(':');
            foreach (var x in xs)
            {
                sb.Append("\n- ").Append(x);
            }
        }
    }

    /// <summary>Índice do passage de menor score (null = menor); -1 se não houver.</summary>
    private static int LowestScorePassageIndex(List<ContextSnapshotService.SnapshotItem> work)
    {
        var idx = -1;
        var best = double.PositiveInfinity;
        for (var i = 0; i < work.Count; i++)
        {
            if (work[i].Kind != "passage")
            {
                continue;
            }
            var score = work[i].Score ?? double.NegativeInfinity;
            if (score < best)
            {
                best = score;
                idx = i;
            }
        }
        return idx;
    }
}
