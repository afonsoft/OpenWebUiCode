using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Limites efetivos de budget resolvidos para um usuário
/// (SPEC-20261023-run-cost-budgets): precedência por campo —
/// política do usuário → menor valor entre os grupos do usuário → global.
/// Todos nulos = sem teto (comportamento atual).
/// </summary>
public sealed record BudgetLimits(
    double? UsdPerRun,
    double? UsdPerMonth,
    long? TokensPerMonth,
    int? MaxModelTier)
{
    /// <summary>Verdadeiro quando nenhum limite está definido.</summary>
    public bool IsEmpty =>
        UsdPerRun is null && UsdPerMonth is null
            && TokensPerMonth is null && MaxModelTier is null;
}

/// <summary>
/// Veredito do gate de budget: <see cref="Code"/> é
/// <c>budget_exceeded</c> ou <c>no_model_in_tier</c> quando
/// <see cref="Allowed"/> é falso.
/// </summary>
public sealed record BudgetGate(bool Allowed, string? Code, string? Detail)
{
    /// <summary>Gate liberado.</summary>
    public static readonly BudgetGate Ok = new(true, null, null);

    /// <summary>Gate negado com código e detalhe explicável.</summary>
    public static BudgetGate Deny(string code, string detail) => new(false, code, detail);
}

/// <summary>
/// Resolve políticas de budget e aplica o gate pré-run
/// (SPEC-20261023-run-cost-budgets RF-002/RF-004). Fail-open apenas na
/// leitura das próprias policies/ledger (exceção = sem teto, logado —
/// regra do SPEC §8); a decisão em si é fail-closed: estourou cap, a run
/// não anda.
/// </summary>
public sealed class BudgetPolicyService(
    AppDbContext db,
    ConfigService config,
    RunUsageLedgerService ledger,
    ILogger<BudgetPolicyService> logger)
{
    /// <summary>Chave kv do kill-switch (default ligado).</summary>
    public const string EnabledKey = "agent.budgets.enabled";

    /// <summary>Chave kv do percentual de aviso (default 80).</summary>
    public const string WarnAtKey = "agent.budgets.warnAtPercent";

    /// <summary>Chave kv dos limites de tier (USD/1M blended, default "1,5,20").</summary>
    public const string TierBoundsKey = "agent.costTiers.bounds";

    /// <summary>Se o enforcement está ativo (config <see cref="EnabledKey"/>, default true).</summary>
    public async Task<bool> EnabledAsync(CancellationToken ct) =>
        await config.GetAsync(EnabledKey, true, ct);

    /// <summary>Percentual de aviso (<see cref="WarnAtKey"/>, default 80).</summary>
    public async Task<int> WarnAtPercentAsync(CancellationToken ct) =>
        Math.Clamp(await config.GetAsync(WarnAtKey, 80, ct), 1, 100);

    /// <summary>
    /// Limites efetivos do usuário: campo a campo, usuário → menor dos
    /// grupos → global. Políticas lidas com falha são tratadas como
    /// "sem policy" (log + sem teto — regra do SPEC §8).
    /// </summary>
    public async Task<BudgetLimits> EffectiveAsync(string userId, CancellationToken ct)
    {
        try
        {
            var policies = await db.BudgetPolicies.AsNoTracking().ToListAsync(ct);
            var user = policies.FirstOrDefault(
                p => p.Subject == $"user:{userId}");
            var groupIds = await db.GroupMembers.AsNoTracking()
                .Where(m => m.UserId == userId)
                .Select(m => m.GroupId)
                .ToListAsync(ct);
            var groups = policies
                .Where(p => p.Subject.StartsWith("group:", StringComparison.Ordinal)
                    && groupIds.Contains(p.Subject["group:".Length..]))
                .ToList();
            var global = policies.FirstOrDefault(p => p.Subject == "global");

            return new BudgetLimits(
                user?.UsdPerRun
                    ?? MinOrNull(groups.Select(g => g.UsdPerRun))
                    ?? global?.UsdPerRun,
                user?.UsdPerMonth
                    ?? MinOrNull(groups.Select(g => g.UsdPerMonth))
                    ?? global?.UsdPerMonth,
                user?.TokensPerMonth
                    ?? MinOrNull(groups.Select(g => g.TokensPerMonth))
                    ?? global?.TokensPerMonth,
                user?.MaxModelTier
                    ?? MinOrNull(groups.Select(g => g.MaxModelTier))
                    ?? global?.MaxModelTier);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Falha ao resolver budget do usuário {UserId} — tratando como sem policy.",
                userId);
            return new BudgetLimits(null, null, null, null);
        }
    }

    /// <summary>
    /// Gate pré-run (RF-002/RF-004): recusa quando o mês já estourou
    /// (USD ou tokens) ou quando o modelo pedido tem tier acima do
    /// permitido. Modelo sem preço conhecido é elegível em qualquer tier
    /// (local/não-precificado = tier desconhecido, não upgrade).
    /// </summary>
    public async Task<BudgetGate> CheckStartAsync(
        string userId, string? modelId, CancellationToken ct,
        int? inheritedMaxTier = null)
    {
        if (!await EnabledAsync(ct))
        {
            return BudgetGate.Ok;
        }
        var limits = await EffectiveAsync(userId, ct);
        var maxTier = Min(limits.MaxModelTier, inheritedMaxTier);
        if (limits.IsEmpty && maxTier is null)
        {
            return BudgetGate.Ok;
        }

        if (modelId is not null && maxTier is { } mt
            && await ModelTierAsync(modelId, userId, ct) is { } tier
            && tier > mt)
        {
            return BudgetGate.Deny("no_model_in_tier",
                $"Modelo '{modelId}' está no tier {tier} de preço, acima do "
                + $"máximo permitido ({mt}) pela política de budget.");
        }

        if (limits.UsdPerMonth is not null || limits.TokensPerMonth is not null)
        {
            var month = await MonthTotalsSafeAsync(userId, ct);
            if (limits.UsdPerMonth is { } usd && month.CostUsd >= usd)
            {
                return BudgetGate.Deny("budget_exceeded",
                    $"Custo do mês (${month.CostUsd:F4}) atingiu o teto "
                    + $"usdPerMonth (${usd:F2}).");
            }
            if (limits.TokensPerMonth is { } tokens
                && month.PromptTokens + month.CompletionTokens >= tokens)
            {
                return BudgetGate.Deny("budget_exceeded",
                    $"Tokens do mês ({month.PromptTokens + month.CompletionTokens}) "
                    + $"atingiram o teto tokensPerMonth ({tokens}).");
            }
        }
        return BudgetGate.Ok;
    }

    /// <summary>
    /// Tier de preço do modelo (RF-004): blended (input+output)/2 USD por
    /// 1M tokens, classificado pelos limites de
    /// <see cref="TierBoundsKey"/> (default <c>1,5,20</c> → tiers
    /// 0:&lt;1, 1:&lt;5, 2:&lt;20, 3:≥20). Sem preço = null (elegível em
    /// qualquer maxTier — modelos locais/não-precificados).
    /// </summary>
    public async Task<int?> ModelTierAsync(
        string modelId, string? userId, CancellationToken ct)
    {
        var metaJson = await db.ModelEntries.AsNoTracking()
            .Where(m => (m.Id == modelId || m.Name == modelId || m.BaseModelId == modelId)
                && (userId == null || m.UserId == userId || m.UserId == "public"))
            .Select(m => m.MetaJson)
            .FirstOrDefaultAsync(ct);
        if (metaJson is null)
        {
            return null;
        }
        try
        {
            var meta = JsonSerializer.Deserialize<JsonElement>(metaJson);
            if (!meta.TryGetProperty("cost", out var cost)
                || !cost.TryGetProperty("input", out var input)
                || !cost.TryGetProperty("output", out var output)
                || !input.TryGetDouble(out var inRate)
                || !output.TryGetDouble(out var outRate))
            {
                return null;
            }
            var blended = (inRate + outRate) / 2d;
            var bounds = (await config.GetAsync(
                    TierBoundsKey, "1,5,20", ct))
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (double?)null)
                .Where(v => v is not null)
                .Select(v => v!.Value)
                .OrderBy(v => v)
                .ToList();
            for (var i = 0; i < bounds.Count; i++)
            {
                if (blended < bounds[i])
                {
                    return i;
                }
            }
            return bounds.Count;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Totais do mês com falha tratada como zero (SPEC §8).</summary>
    public async Task<UsageTotals> MonthTotalsSafeAsync(
        string userId, CancellationToken ct)
    {
        try
        {
            return await ledger.MonthTotalsAsync(userId, DateTimeOffset.UtcNow, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Falha ao ler ledger do usuário {UserId} — mês conta como zero.", userId);
            return new UsageTotals(0, 0, 0, 0, 0);
        }
    }

    private static T? MinOrNull<T>(IEnumerable<T?> values)
        where T : struct, IComparable<T>
    {
        var list = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : list.Min();
    }

    private static int? Min(int? a, int? b) =>
        a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}
