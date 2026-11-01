using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Resolveu um modelo efetivo de um combo.</summary>
/// <param name="ComboId">Id do combo resolvido.</param>
/// <param name="ModelId">Modelo efetivo escolhido pela estratégia.</param>
/// <param name="Candidates">Candidatos disponíveis (inclui o escolhido) — teto de tentativas de failover.</param>
public sealed record ComboPick(string ComboId, string ModelId, int Candidates);

/// <summary>Combo indisponível: nenhum candidato ou janela de rate limit cheia.</summary>
public sealed class ComboUnavailableException : Exception
{
    /// <summary><c>combo-exhausted</c> (todos em cooldown) ou <c>combo-rate-limit</c>.</summary>
    public string Code { get; }

    /// <summary>Epoch seconds sugerido para retry (menor cooldown / fim da janela).</summary>
    public long? RetryAt { get; }

    /// <summary>Cria a exceção com código e sugestão de retry.</summary>
    public ComboUnavailableException(string code, long? retryAt)
        : base(code) => (Code, RetryAt) = (code, retryAt);
}

/// <summary>
/// Combos de modelos com failover (SPEC-20261016-model-combos):
/// <c>ResolveAsync</c> escolhe o próximo modelo respeitando estratégia,
/// cooldown por falha e janela deslizante de req/tokens por minuto;
/// <c>ReportAsync</c> alimenta cooldown e contabiliza tokens.
/// Cooldowns e janelas são process-local (ConcurrentDictionary estática);
/// o cursor do round-robin persiste na entidade (RF-002 "persiste em kv").
/// </summary>
public sealed class ComboService(AppDbContext db)
{
    /// <summary>Prefixo de referência aceito onde um modelo é esperado.</summary>
    public const string Prefix = "combo:";

    /// <summary>Cooldown default por falha reportada.</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Teto do cooldown (Retry-After capturado).</summary>
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan WindowSize = TimeSpan.FromSeconds(60);

    /// <summary>Estratégias suportadas (RF-002).</summary>
    public static readonly HashSet<string> Strategies =
        ["priority", "round-robin", "priority-round-robin"];

    // comboId|modelId → fim do cooldown (process-local).
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Cooldowns = new();

    // comboId → eventos (instante, tokens) na janela de 60s.
    private static readonly ConcurrentDictionary<string, List<(DateTimeOffset At, int Tokens)>> Windows = new();
    private static readonly object WindowLock = new();

    /// <summary>Clock injetável para testes (RF-T5).</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Limpa cooldowns/janelas (isolamento de testes).</summary>
    internal static void ResetState()
    {
        Cooldowns.Clear();
        lock (WindowLock)
        {
            Windows.Clear();
        }
    }

    /// <summary>
    /// Resolve o próximo modelo do combo (por id ou nome, visível ao usuário).
    /// Registra a requisição na janela. Lança <see cref="ComboUnavailableException"/>
    /// com <c>combo-exhausted</c> ou <c>combo-rate-limit</c>.
    /// </summary>
    public async Task<ComboPick> ResolveAsync(
        string comboRef, string userId, CancellationToken ct = default)
    {
        var combo = await LoadAsync(comboRef, userId, ct)
            ?? throw new ComboUnavailableException("combo-not-found", null);
        if (!combo.Enabled)
        {
            throw new ComboUnavailableException("combo-disabled", null);
        }

        var now = Now();
        CheckWindow(combo, now);

        var available = combo.Items
            .Where(i => !InCooldown(combo.Id, i.ModelId, now))
            .OrderBy(i => i.Position)
            .Select(i => i.ModelId)
            .ToList();
        if (available.Count == 0)
        {
            var retryAt = combo.Items
                .Select(i => CooldownUntil(combo.Id, i.ModelId))
                .Where(u => u > now)
                .OrderBy(u => u)
                .Select(u => (long?)u.ToUnixTimeSeconds())
                .FirstOrDefault();
            throw new ComboUnavailableException("combo-exhausted", retryAt);
        }

        var modelId = combo.Strategy switch
        {
            "round-robin" => available[(combo.RoundRobinCursor % available.Count)],
            "priority-round-robin" => RoundRobinInGroup(combo, available),
            _ => available[0],
        };
        if (combo.Strategy is not "priority")
        {
            combo.RoundRobinCursor++;
            combo.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync(ct);
        }

        RecordRequest(combo.Id, now, 0);
        return new ComboPick(combo.Id, modelId, available.Count);
    }

    /// <summary>
    /// Reporta o resultado do modelo efetivo: falha → cooldown (60s ou
    /// <paramref name="retryAfter"/> capado a 5min); sucesso → soma tokens na janela.
    /// Cancelamento do usuário nunca deve ser reportado (guardrail da SPEC).
    /// </summary>
    public async Task ReportAsync(
        string comboRef, string modelId, bool ok,
        int tokens = 0, TimeSpan? retryAfter = null, CancellationToken ct = default)
    {
        // Normaliza ref (id ou nome) → id canônico, casando com a chave do
        // cooldown usada no ResolveAsync.
        var reference = comboRef.StartsWith(Prefix, StringComparison.Ordinal)
            ? comboRef[Prefix.Length..]
            : comboRef;
        var combo = await db.ModelCombos.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == reference || c.Name == reference, ct);
        var comboId = combo?.Id ?? comboRef;
        var now = Now();
        if (ok)
        {
            if (tokens > 0)
            {
                RecordRequest(comboId, now, tokens);
            }
            return;
        }

        var span = retryAfter is { } r && r > TimeSpan.Zero
            ? r : DefaultCooldown;
        if (span > MaxCooldown)
        {
            span = MaxCooldown;
        }

        Cooldowns[CooldownKey(comboId, modelId)] = now + span;
    }

    /// <summary>Status do combo para <c>GET /combos/{id}/status</c> (RF-005).</summary>
    public async Task<ComboStatus?> GetStatusAsync(
        string comboRef, string userId, CancellationToken ct = default)
    {
        var combo = await LoadAsync(comboRef, userId, ct);
        if (combo is null)
        {
            return null;
        }

        var now = Now();
        var cooldowns = combo.Items
            .Select(i => (modelId: i.ModelId, until: CooldownUntil(combo.Id, i.ModelId)))
            .Where(c => c.until > now)
            .Select(c => new ComboCooldown(c.modelId, c.until.ToUnixTimeSeconds()))
            .ToList();
        var (requests, tokens) = WindowCounts(combo.Id, now);
        var available = combo.Items
            .Where(i => !InCooldown(combo.Id, i.ModelId, now))
            .OrderBy(i => i.Position)
            .ToList();
        var effective = available.Count == 0
            ? null
            : combo.Strategy switch
            {
                "round-robin" => available[(combo.RoundRobinCursor % available.Count)].ModelId,
                "priority-round-robin" => PeekRoundRobinInGroup(combo, available).ModelId,
                _ => available[0].ModelId,
            };
        return new ComboStatus(combo.Id, effective, cooldowns, requests, tokens);
    }

    /// <summary>Carrega combo por id ou nome visível ao usuário (null = global).</summary>
    public async Task<ModelCombo?> LoadAsync(
        string comboRef, string userId, CancellationToken ct = default)
    {
        var reference = comboRef.StartsWith(Prefix, StringComparison.Ordinal)
            ? comboRef[Prefix.Length..]
            : comboRef;
        return await db.ModelCombos
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c =>
                (c.Id == reference || c.Name == reference) &&
                (c.UserId == userId || c.UserId == null), ct);
    }

    /// <summary>Registra uma requisição (e tokens) na janela deslizante do combo.</summary>
    private static void RecordRequest(string comboId, DateTimeOffset at, int tokens)
    {
        lock (WindowLock)
        {
            var entries = Windows.GetOrAdd(comboId, _ => []);
            entries.Add((at, tokens));
        }
    }

    private static void CheckWindow(ModelCombo combo, DateTimeOffset now)
    {
        if (combo.RequestsPerMinute is null && combo.TokensPerMinute is null)
        {
            return;
        }

        var (requests, tokens) = WindowCounts(combo.Id, now);
        if ((combo.RequestsPerMinute is { } rpm && requests >= rpm)
            || (combo.TokensPerMinute is { } tpm && tokens >= tpm))
        {
            throw new ComboUnavailableException(
                "combo-rate-limit", now.Add(WindowSize).ToUnixTimeSeconds());
        }
    }

    private static (int Requests, long Tokens) WindowCounts(string comboId, DateTimeOffset now)
    {
        lock (WindowLock)
        {
            if (!Windows.TryGetValue(comboId, out var entries))
            {
                return (0, 0);
            }

            entries.RemoveAll(e => now - e.At >= WindowSize);
            return (entries.Count, entries.Sum(e => (long)e.Tokens));
        }
    }

    private static string CooldownKey(string comboId, string modelId) => $"{comboId}|{modelId}";

    private static DateTimeOffset CooldownUntil(string comboId, string modelId) =>
        Cooldowns.TryGetValue(CooldownKey(comboId, modelId), out var until) ? until : DateTimeOffset.MinValue;

    private static bool InCooldown(string comboId, string modelId, DateTimeOffset now) =>
        CooldownUntil(comboId, modelId) > now;

    // priority-round-robin: rotaciona dentro do grupo de menor Position
    // disponível; o cursor avança por resolve (persistido na entidade).
    private static string RoundRobinInGroup(ModelCombo combo, List<string> available)
    {
        var group = FirstGroup(combo, available);
        return group[(combo.RoundRobinCursor % group.Count)];
    }

    private static ModelComboItem PeekRoundRobinInGroup(ModelCombo combo, List<ModelComboItem> available)
    {
        var group = FirstGroupItems(available);
        return group[(combo.RoundRobinCursor % group.Count)];
    }

    private static List<string> FirstGroup(ModelCombo combo, List<string> available)
    {
        var positions = combo.Items
            .Where(i => available.Contains(i.ModelId))
            .Select(i => i.Position).Order().ToList();
        var first = positions[0];
        return combo.Items.Where(i => i.Position == first && available.Contains(i.ModelId))
            .OrderBy(i => i.ModelId).Select(i => i.ModelId).ToList();
    }

    private static List<ModelComboItem> FirstGroupItems(List<ModelComboItem> available)
    {
        var first = available.Min(i => i.Position);
        return available.Where(i => i.Position == first).OrderBy(i => i.ModelId).ToList();
    }
}

/// <summary>Cooldown ativo de um modelo do combo.</summary>
public sealed record ComboCooldown(string ModelId, long Until);

/// <summary>Status exposto por <c>GET /api/v1/combos/{id}/status</c>.</summary>
public sealed record ComboStatus(
    string ComboId, string? EffectiveModel,
    IReadOnlyList<ComboCooldown> Cooldowns, int WindowRequests, long WindowTokens);
