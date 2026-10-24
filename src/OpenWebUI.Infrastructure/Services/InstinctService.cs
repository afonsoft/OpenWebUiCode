using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Núcleo dos instincts (SPEC-20261023-instincts-learning): CRUD +
/// dedup por (domínio, trigger normalizado, escopo) + lifecycle de
/// confiança. Trust boundary: instincts são contexto observado — o
/// serviço nunca executa, interpreta como código ou transforma o
/// <see cref="Instinct.Body"/> em tool call; tudo vira texto marcado
/// "not policy" no prompt.
/// </summary>
public sealed class InstinctService(
    AppDbContext db,
    ConfigService config,
    UserConfigOverlayService overlay,
    ILogger<InstinctService> logger)
{
    /// <summary>Confiança inicial de um candidato extraído.</summary>
    public const double InitialConfidence = 0.5;

    /// <summary>Reforço por hit/observação repetida.</summary>
    public const double ReinforceStep = 0.05;

    /// <summary>Penalidade por erro atribuível.</summary>
    public const double PenaltyStep = 0.15;

    /// <summary>Teto de confiança — nunca vira "certeza".</summary>
    public const double ConfidenceCap = 0.95;

    /// <summary>Piso ao aprovar manualmente um pending (threshold+ε).</summary>
    public const double ApprovalEpsilon = 0.01;

    /// <summary>Dias sem hit antes de expirar.</summary>
    public const int ExpireDays = 30;

    /// <summary>Máximo de pending+active mantidos por usuário+escopo.</summary>
    public const int MaxPerScope = 50;

    /// <summary>Máximo de refs de origem mescladas por instinct.</summary>
    public const int MaxSourceRefs = 10;

    /// <summary>Tamanho máximo do corpo de um instinct.</summary>
    public const int MaxBodyChars = 500;

    /// <summary>Status: aguardando threshold ou aprovação humana.</summary>
    public const string StatusPending = "pending";

    /// <summary>Status: elegível para injeção.</summary>
    public const string StatusActive = "active";

    /// <summary>Status: descartado pelo usuário.</summary>
    public const string StatusRejected = "rejected";

    /// <summary>Status: expirado por falta de reforço.</summary>
    public const string StatusExpired = "expired";

    /// <summary>Escopo: vale em qualquer sessão do usuário.</summary>
    public const string ScopeUser = "user";

    /// <summary>Escopo: só sessões bound ao repo <see cref="Instinct.WorkspaceId"/>.</summary>
    public const string ScopeWorkspace = "workspace";

    /// <summary>
    /// Threshold efetivo de injeção (kv <c>instincts.threshold</c>,
    /// default 0.7).
    /// </summary>
    public async Task<double> ThresholdAsync(CancellationToken ct) =>
        Math.Clamp(await config.GetAsync("instincts.threshold", 0.7, ct), 0.05, 1.0);

    /// <summary>
    /// Modo efetivo de injeção para o usuário
    /// (<c>u:{uid}:instincts.mode</c> → <c>instincts.mode</c> → auto):
    /// <c>off</c> nunca injeta; <c>on</c> injeta ignorando threshold de
    /// confiança (ainda exige <c>active</c>); <c>auto</c> só injeta quando
    /// há instincts ativos acima do threshold.
    /// </summary>
    public async Task<string> ModeAsync(string userId, CancellationToken ct)
    {
        var perUser = (await overlay.GetAsync(
            userId, "instincts.mode", (string?)null, ct)).Value;
        var mode = perUser ?? await config.GetAsync("instincts.mode", "auto", ct);
        return mode is "on" or "off" ? mode : "auto";
    }

    /// <summary>Contagem de pending — badge de revisão na UI (RF-E).</summary>
    public async Task<int> PendingCountAsync(string userId, CancellationToken ct) =>
        await db.Instincts.AsNoTracking()
            .CountAsync(i => i.UserId == userId && i.Status == StatusPending, ct);

    /// <summary>Lista instincts do usuário filtrando por status/escopo.</summary>
    public async Task<List<Instinct>> ListAsync(
        string userId, string? status, string? scope, CancellationToken ct)
    {
        var q = db.Instincts.AsNoTracking().Where(i => i.UserId == userId);
        if (!string.IsNullOrEmpty(status))
        {
            q = q.Where(i => i.Status == status);
        }
        if (!string.IsNullOrEmpty(scope))
        {
            q = q.Where(i => i.Scope == scope);
        }
        return await q
            .OrderByDescending(i => i.Status == StatusPending)
            .ThenByDescending(i => i.Confidence)
            .ThenByDescending(i => i.UpdatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Candidatos elegíveis para injeção numa sessão: <c>active</c>,
    /// escopo compatível (user ou workspace matching <paramref name="repoSlug"/>),
    /// e — no modo <c>auto</c> — confiança ≥ threshold.
    /// </summary>
    public async Task<List<Instinct>> InjectionCandidatesAsync(
        string userId, string? repoSlug, CancellationToken ct)
    {
        var mode = await ModeAsync(userId, ct);
        if (mode == "off")
        {
            return [];
        }

        var q = db.Instincts.AsNoTracking()
            .Where(i => i.UserId == userId && i.Status == StatusActive
                && (i.Scope == ScopeUser
                    || (i.Scope == ScopeWorkspace && i.WorkspaceId == repoSlug)));
        if (mode == "auto")
        {
            var threshold = await ThresholdAsync(ct);
            q = q.Where(i => i.Confidence >= threshold);
        }
        return await q.ToListAsync(ct);
    }

    /// <summary>
    /// Upsert de candidato extraído: trigger normalizado igual no mesmo
    /// (domínio, escopo) reforça o existente (+0.05, HitCount++, refs
    /// mescladas até 10) em vez de duplicar. Devolve o instinct gravado
    /// ou null quando inválido/poda.
    /// </summary>
    public async Task<Instinct?> UpsertCandidateAsync(
        string userId, string scope, string? workspaceId,
        string domain, string trigger, string body,
        string sourceRef, CancellationToken ct)
    {
        domain = (domain ?? string.Empty).Trim().ToLowerInvariant();
        trigger = (trigger ?? string.Empty).Trim();
        body = (body ?? string.Empty).Trim();
        if (domain.Length == 0 || trigger.Length == 0 || body.Length == 0)
        {
            return null;
        }
        if (body.Length > MaxBodyChars)
        {
            body = body[..MaxBodyChars];
        }

        var normalized = NormalizeTrigger(trigger);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Dedup: trigger normalizado + domain + escopo. A normalização não
        // é coluna — compara em memória só dentro do universo do usuário.
        var sameDomain = await db.Instincts
            .Where(i => i.UserId == userId && i.Domain == domain
                && i.Scope == scope && i.WorkspaceId == workspaceId
                && i.Status != StatusRejected && i.Status != StatusExpired)
            .ToListAsync(ct);
        var existing = sameDomain.FirstOrDefault(
            i => NormalizeTrigger(i.Trigger) == normalized);
        if (existing is not null)
        {
            existing.HitCount++;
            existing.Confidence = Math.Min(
                ConfidenceCap, existing.Confidence + ReinforceStep);
            existing.LastSeenAt = now;
            existing.UpdatedAt = now;
            existing.SourceRefs = MergeRefs(existing.SourceRefs, sourceRef);
            await db.SaveChangesAsync(ct);
            return existing;
        }

        await PruneAsync(userId, scope, workspaceId, ct);
        var instinct = new Instinct
        {
            UserId = userId,
            Scope = scope,
            WorkspaceId = workspaceId,
            Domain = domain,
            Trigger = trigger.Length > 300 ? trigger[..300] : trigger,
            Body = body,
            Confidence = InitialConfidence,
            Status = StatusPending,
            HitCount = 1,
            SourceRefs = JsonSerializer.Serialize(new[] { sourceRef }),
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now,
        };
        db.Instincts.Add(instinct);
        await db.SaveChangesAsync(ct);
        return instinct;
    }

    /// <summary>
    /// Hit implícito (RF-D): a run do usuário completou sem erro —
    /// reforça os instincts que eram elegíveis para injeção naquele
    /// contexto (mesmo recorte do contributor). +0.05, cap 0.95.
    /// </summary>
    public async Task<int> RecordInjectionHitsAsync(
        string userId, string? repoSlug, CancellationToken ct)
    {
        var candidates = await InjectionCandidatesAsync(userId, repoSlug, ct)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ids = candidates.Select(c => c.Id).ToList();
        var tracked = await db.Instincts
            .Where(i => ids.Contains(i.Id))
            .ToListAsync(ct);
        foreach (var i in tracked)
        {
            i.Confidence = Math.Min(ConfidenceCap, i.Confidence + ReinforceStep);
            i.HitCount++;
            i.LastSeenAt = now;
            i.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return tracked.Count;
    }

    /// <summary>
    /// Penaliza instincts elegíveis quando a run falhou
    /// (erro atribuível — heurística v1): -0.15, piso 0.05.
    /// </summary>
    public async Task<int> RecordFailureAsync(
        string userId, string? repoSlug, CancellationToken ct)
    {
        var candidates = await InjectionCandidatesAsync(userId, repoSlug, ct)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var ids = candidates.Select(c => c.Id).ToList();
        var tracked = await db.Instincts
            .Where(i => ids.Contains(i.Id))
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var i in tracked)
        {
            i.Confidence = Math.Max(0.05, i.Confidence - PenaltyStep);
            i.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return tracked.Count;
    }

    /// <summary>
    /// Lifecycle por usuário (chamado no tick do MemorySyncService):
    /// pending com confiança ≥ threshold promove a active; sem hit em
    /// 30 dias vira expired.
    /// </summary>
    public async Task ApplyLifecycleAsync(string userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var threshold = await ThresholdAsync(ct);
        var expireBefore = now - ExpireDays * 86400L;

        var items = await db.Instincts
            .Where(i => i.UserId == userId
                && (i.Status == StatusPending || i.Status == StatusActive))
            .ToListAsync(ct);
        var changed = false;
        foreach (var i in items)
        {
            if (i.LastSeenAt < expireBefore)
            {
                i.Status = StatusExpired;
                i.UpdatedAt = now;
                changed = true;
            }
            else if (i.Status == StatusPending && i.Confidence >= threshold)
            {
                i.Status = StatusActive;
                i.UpdatedAt = now;
                changed = true;
            }
        }
        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Ação manual: <c>approve</c> (pending→active, confiança sobe ao
    /// threshold+ε), <c>reject</c> (→rejected), <c>delete</c> (remove).
    /// Devolve o instinct afetado ou null se não achou do usuário.
    /// </summary>
    public async Task<Instinct?> ApplyActionAsync(
        string userId, string id, string action, CancellationToken ct)
    {
        var instinct = await db.Instincts
            .FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct);
        if (instinct is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        switch (action)
        {
            case "approve":
                instinct.Status = StatusActive;
                instinct.Confidence = Math.Max(
                    instinct.Confidence,
                    await ThresholdAsync(ct) + ApprovalEpsilon);
                instinct.UpdatedAt = now;
                instinct.LastSeenAt = now;
                break;
            case "reject":
                instinct.Status = StatusRejected;
                instinct.UpdatedAt = now;
                break;
            case "delete":
                db.Instincts.Remove(instinct);
                break;
            default:
                throw new ArgumentException($"ação inválida: {action}");
        }
        await db.SaveChangesAsync(ct);
        return instinct;
    }

    /// <summary>
    /// Evolve (RF-005): agrupa os instincts <c>active</c> do domínio e
    /// gera rascunho de SKILL.md como texto — nunca escreve em disco.
    /// Devolve null quando não há massa mínima (≥3).
    /// </summary>
    public async Task<(string Draft, IReadOnlyList<string> Sources)?> EvolveAsync(
        string userId, string domain, string? scope, CancellationToken ct)
    {
        domain = (domain ?? string.Empty).Trim().ToLowerInvariant();
        if (domain.Length == 0)
        {
            return null;
        }

        var q = db.Instincts.AsNoTracking()
            .Where(i => i.UserId == userId && i.Domain == domain
                && i.Status == StatusActive);
        if (!string.IsNullOrEmpty(scope))
        {
            q = q.Where(i => i.Scope == scope);
        }
        var items = await q.OrderByDescending(i => i.Confidence).ToListAsync(ct);
        if (items.Count < 3)
        {
            return null;
        }

        var name = $"learned-{Slug(domain)}";
        var body = string.Join("\n",
            items.Select(i => $"- {i.Trigger} → {i.Body}"));
        var draft = $"""
            ---
            name: {name}
            description: Learned instincts for {domain} — observed patterns, review before adopting.
            ---

            # Learned instincts: {domain}

            > Auto-generated draft from {items.Count} active instinct(s).
            > These are observed patterns, not policy — verify each before
            > keeping. Edit freely, then save under .agents/skills/{name}/.

            {body}
            """;
        return (draft, items.Select(i => i.Id).ToList());
    }

    /// <summary>Poda para manter no máximo MaxPerScope pending+active
    /// por usuário+escopo — remove os mais antigos primeiro.</summary>
    private async Task PruneAsync(
        string userId, string scope, string? workspaceId, CancellationToken ct)
    {
        var overflow = await db.Instincts
            .Where(i => i.UserId == userId && i.Scope == scope
                && i.WorkspaceId == workspaceId
                && (i.Status == StatusPending || i.Status == StatusActive))
            .OrderByDescending(i => i.LastSeenAt)
            .Skip(MaxPerScope - 1)
            .ToListAsync(ct);
        if (overflow.Count > 0)
        {
            db.Instincts.RemoveRange(overflow);
            await db.SaveChangesAsync(ct);
            logger.LogDebug(
                "instincts: podou {Count} instincts excedentes de {UserId}",
                overflow.Count, userId);
        }
    }

    /// <summary>Normaliza trigger para dedup: minúsculas, whitespace
    /// colapsado, pontuação das bordas removida.</summary>
    public static string NormalizeTrigger(string trigger)
    {
        var t = (trigger ?? string.Empty).Trim().ToLowerInvariant();
        t = string.Join(' ', t.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Trim('.', ',', ';', ':', '!', '?', '"', '\'');
    }

    private static string MergeRefs(string existingJson, string newRef)
    {
        List<string> refs;
        try
        {
            refs = JsonSerializer.Deserialize<List<string>>(existingJson)
                ?? [];
        }
        catch (JsonException)
        {
            refs = [];
        }
        if (!refs.Contains(newRef))
        {
            refs.Add(newRef);
        }
        if (refs.Count > MaxSourceRefs)
        {
            refs = refs.Skip(refs.Count - MaxSourceRefs).ToList();
        }
        return JsonSerializer.Serialize(refs);
    }

    private static string Slug(string domain)
    {
        var chars = domain.Select(
            c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }
        return slug.Length == 0 ? "domain" : slug;
    }
}
