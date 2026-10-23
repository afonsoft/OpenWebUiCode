using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Totais agregados de uso (SPEC-20261023-run-cost-budgets).
/// </summary>
/// <param name="PromptTokens">Prompt tokens somados.</param>
/// <param name="CompletionTokens">Completion tokens somados.</param>
/// <param name="ReasoningTokens">Reasoning tokens somados.</param>
/// <param name="CostUsd">Custo somado (só linhas com custo conhecido).</param>
/// <param name="Runs">Número de runs no recorte.</param>
public sealed record UsageTotals(
    long PromptTokens,
    long CompletionTokens,
    long ReasoningTokens,
    double CostUsd,
    int Runs);

/// <summary>
/// Ledger consultável de uso por run (SPEC-20261023-run-cost-budgets
/// RF-001): escrita idempotente por <c>RunId</c> no fim da run e queries
/// de agregação por usuário/mês e por cadeia de delegação
/// (<c>ParentRunId</c>). Invariante do projeto: tokens/custo só entram
/// quando o provider reporta — nunca estimados.
/// </summary>
public sealed class RunUsageLedgerService(
    AppDbContext db,
    ILogger<RunUsageLedgerService> logger)
{
    /// <summary>Profundidade máxima da cadeia de delegação (depth de delegate_task).</summary>
    private const int MaxChainDepth = 4;

    /// <summary>
    /// Grava o record da run; idempotente por <paramref name="runId"/> —
    /// re-tentativas de gravação (retomada, double-finish) não duplicam.
    /// </summary>
    public async Task RecordAsync(
        string runId, string chatId, string userId, string? parentRunId,
        string? modelId, int steps, long promptTokens, long completionTokens,
        long reasoningTokens, double? costUsd, CancellationToken ct)
    {
        try
        {
            if (await db.RunUsageRecords.AnyAsync(r => r.RunId == runId, ct))
            {
                return;
            }
            db.RunUsageRecords.Add(new RunUsageRecord
            {
                RunId = runId,
                ChatId = chatId,
                UserId = userId,
                ParentRunId = parentRunId,
                ModelId = modelId,
                Steps = steps,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                ReasoningTokens = reasoningTokens,
                CostUsd = costUsd,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ledger é observabilidade: falha de gravação não derruba a run
            // (SPEC §8 — falha na query de budget trata como "sem policy").
            logger.LogWarning(ex, "Falha ao gravar RunUsageRecord da run {RunId}.", runId);
        }
    }

    /// <summary>
    /// Totais do usuário no mês UTC corrente (RF-002): linhas com
    /// <c>CreatedAt</c> dentro do mês de <paramref name="now"/>.
    /// </summary>
    public async Task<UsageTotals> MonthTotalsAsync(
        string userId, DateTimeOffset now, CancellationToken ct)
    {
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeSeconds();
        var end = now.AddMonths(1);
        var next = new DateTimeOffset(end.Year, end.Month, 1, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeSeconds();
        return await TotalsAsync(db.RunUsageRecords.AsNoTracking()
            .Where(r => r.UserId == userId
                && r.CreatedAt >= start && r.CreatedAt < next), ct);
    }

    /// <summary>Totais de uma janela arbitrária (epoch seconds) — superfície admin.</summary>
    public async Task<UsageTotals> TotalsAsync(
        IQueryable<RunUsageRecord> source, CancellationToken ct)
    {
        var rows = await source
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Prompt = g.Sum(r => r.PromptTokens),
                Completion = g.Sum(r => r.CompletionTokens),
                Reasoning = g.Sum(r => r.ReasoningTokens),
                Cost = g.Sum(r => r.CostUsd ?? 0),
                Runs = g.Count(),
            })
            .FirstOrDefaultAsync(ct);
        return rows is null
            ? new UsageTotals(0, 0, 0, 0, 0)
            : new UsageTotals(rows.Prompt, rows.Completion,
                rows.Reasoning, rows.Cost, rows.Runs);
    }

    /// <summary>
    /// Custo somado da cadeia de delegação (RF-003): o record da raiz mais
    /// os descendentes por <c>ParentRunId</c>, com profundidade limitada a
    /// <see cref="MaxChainDepth"/> (delegate_task tem depth ≤ 3).
    /// </summary>
    public async Task<double> ChainCostUsdAsync(string rootRunId, CancellationToken ct)
    {
        var frontier = new List<string> { rootRunId };
        var total = 0d;
        for (var depth = 0; depth <= MaxChainDepth && frontier.Count > 0; depth++)
        {
            var rows = await db.RunUsageRecords.AsNoTracking()
                .Where(r => frontier.Contains(r.RunId)
                    || (r.ParentRunId != null && frontier.Contains(r.ParentRunId)))
                .Select(r => new { r.RunId, r.CostUsd })
                .ToListAsync(ct);
            total += rows.Where(r => frontier.Contains(r.RunId))
                .Sum(r => r.CostUsd ?? 0);
            // Filhos encontrados nesta iteração viram a próxima fronteira.
            frontier = rows.Select(r => r.RunId)
                .Where(id => !frontier.Contains(id))
                .ToList();
        }
        return total;
    }

    /// <summary>Registros na janela [from, to) epoch, opcionalmente filtrados por usuário.</summary>
    public Task<List<RunUsageRecord>> QueryAsync(
        string? userId, long from, long to, CancellationToken ct) =>
        db.RunUsageRecords.AsNoTracking()
            .Where(r => r.CreatedAt >= from && r.CreatedAt < to
                && (userId == null || r.UserId == userId))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Top chats por custo na janela (superfície admin, RF-005).</summary>
    public async Task<List<ChatCostRow>> TopChatsAsync(
        long from, long to, int take, CancellationToken ct)
    {
        var rows = await db.RunUsageRecords.AsNoTracking()
            .Where(r => r.CreatedAt >= from && r.CreatedAt < to)
            .GroupBy(r => new { r.ChatId, r.UserId })
            .Select(g => new
            {
                g.Key.ChatId, g.Key.UserId,
                Cost = g.Sum(r => r.CostUsd ?? 0),
            })
            .OrderByDescending(g => g.Cost)
            .Take(take)
            .ToListAsync(ct);
        return rows.Select(r => new ChatCostRow(
            r.ChatId, r.UserId, r.Cost)).ToList();
    }

    /// <summary>Agregado por usuário na janela (superfície admin, RF-005).</summary>
    public async Task<List<UserCostRow>> ByUserAsync(
        long from, long to, CancellationToken ct)
    {
        var rows = await db.RunUsageRecords.AsNoTracking()
            .Where(r => r.CreatedAt >= from && r.CreatedAt < to)
            .GroupBy(r => r.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Tokens = g.Sum(r => r.PromptTokens + r.CompletionTokens),
                Cost = g.Sum(r => r.CostUsd ?? 0),
                Runs = g.Count(),
            })
            .OrderByDescending(g => g.Cost)
            .ToListAsync(ct);
        return rows.Select(r => new UserCostRow(
            r.UserId, r.Tokens, r.Cost, r.Runs)).ToList();
    }
}

/// <summary>Linha do agregado por chat.</summary>
public sealed record ChatCostRow(string ChatId, string UserId, double CostUsd);

/// <summary>Linha do agregado por usuário.</summary>
public sealed record UserCostRow(
    string UserId, long Tokens, double CostUsd, int Runs);
