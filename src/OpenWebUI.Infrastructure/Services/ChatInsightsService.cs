using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Session Insights v2 (SPEC-20261104-session-insights-v2): heurística
/// determinística de tamanho/categoria/issues por chat, timeline de eventos
/// das runs e análise LLM sob demanda persistida em <see cref="ChatInsight"/>.
/// </summary>
public sealed class ChatInsightsService(AppDbContext db, WorkspaceRepoService repos)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Faixas de tamanho por total de tokens reportados pelas runs (RF-001).
    private const long SizeS = 5_000, SizeM = 25_000, SizeL = 100_000, SizeXl = 300_000;
    private const int TimelineCap = 100;
    private const int EventScanCap = 500;

    /// <summary>Garante o insight materializado do chat — recomputa quando há
    /// runs novas (recálculo é barato: varre runs+eventos do próprio chat).</summary>
    public async Task<ChatInsight> EnsureComputedAsync(Chat chat, CancellationToken ct)
    {
        var runs = await db.ChatRuns.AsNoTracking()
            .Where(r => r.ChatId == chat.Id)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        var latestRunId = runs.Count > 0 ? runs[^1].Id : null;

        var insight = await db.ChatInsights.FindAsync([chat.Id], ct);
        if (insight is not null && insight.LastRunId == latestRunId)
        {
            return insight;
        }

        var runIds = runs.Select(r => r.Id).ToHashSet();
        var events = await db.ChatRunEvents.AsNoTracking()
            .Where(e => runIds.Contains(e.RunId))
            .OrderBy(e => e.CreatedAt)
            .Take(EventScanCap)
            .ToListAsync(ct);
        var userMessages = await db.ChatMessages.AsNoTracking()
            .CountAsync(m => m.ChatId == chat.Id && m.Role == "user", ct);

        var metrics = ComputeMetrics(runs, userMessages);
        var issues = ComputeIssues(runs, events);
        var timeline = ComputeTimeline(runs, events);
        var size = ClassifySize(metrics.TokensIn + metrics.TokensOut);
        var category = ClassifyCategory(chat, events);

        if (insight is null)
        {
            insight = new ChatInsight { ChatId = chat.Id, UserId = chat.UserId };
            db.ChatInsights.Add(insight);
        }
        insight.SizeClass = size;
        insight.Category = category;
        insight.IssuesJson = JsonSerializer.Serialize(issues, JsonOpts);
        insight.TimelineJson = JsonSerializer.Serialize(timeline, JsonOpts);
        insight.MetricsJson = JsonSerializer.Serialize(metrics, JsonOpts);
        insight.LastRunId = latestRunId;
        insight.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return insight;
    }

    /// <summary>Score de tokens acumulados nas MetaJson das runs (RF-001).</summary>
    private static InsightMetrics ComputeMetrics(List<ChatRun> runs, int userMessages)
    {
        long tin = 0, tout = 0;
        decimal cost = 0m;
        foreach (var run in runs)
        {
            if (run.MetaJson is null)
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(run.MetaJson);
                if (!doc.RootElement.TryGetProperty("usage", out var usage))
                {
                    continue;
                }
                if (usage.TryGetProperty("promptTokens", out var pt))
                {
                    tin += pt.GetInt64();
                }
                if (usage.TryGetProperty("completionTokens", out var ctk))
                {
                    tout += ctk.GetInt64();
                }
                if (usage.TryGetProperty("costUsd", out var cu))
                {
                    cost += cu.GetDecimal();
                }
            }
            catch (JsonException)
            {
                // MetaJson corrompida não derruba a heurística.
            }
        }
        return new InsightMetrics(tin, tout, cost, runs.Count, userMessages);
    }

    /// <summary>Classe de tamanho por total de tokens (faixas documentadas no RF-001).</summary>
    private static string ClassifySize(long tokens) => tokens switch
    {
        < SizeS => "XS",
        < SizeM => "S",
        < SizeL => "M",
        < SizeXl => "L",
        _ => "XL",
    };

    /// <summary>Issues agregadas a partir de runs falhas e eventos (RF-002).</summary>
    private static List<InsightIssue> ComputeIssues(List<ChatRun> runs, List<ChatRunEvent> events)
    {
        var issues = new List<InsightIssue>();

        var failed = runs.Count(r => r.Status == ChatRunStatus.Failed);
        if (failed > 0)
        {
            issues.Add(new InsightIssue("run_failed",
                failed >= 3 ? "high" : "medium",
                $"{failed} run(s) terminaram em erro", failed));
        }

        var retries = events.Count(e =>
            e.Type == "note" && ContainsAny(e.PayloadJson, "retry", "retries", "recover"));
        if (retries > 0)
        {
            issues.Add(new InsightIssue("provider_retry",
                retries >= 3 ? "high" : "medium",
                $"{retries} retry/recuperação de provider", retries));
        }

        var toolFails = events.Count(e =>
            e.Type == "tool_result" && ContainsAny(e.PayloadJson, "error", "failed", "exception"));
        if (toolFails > 0)
        {
            issues.Add(new InsightIssue("tool_failure",
                toolFails >= 3 ? "medium" : "low",
                $"{toolFails} tool call(s) com falha", toolFails));
        }

        var overflow = events.Count(e =>
            ContainsAny(e.PayloadJson, "overflow", "compact"));
        if (overflow > 0)
        {
            issues.Add(new InsightIssue("context_overflow", "medium",
                $"{overflow} recuperação(ões) de contexto", overflow));
        }

        var deadEnd = runs.Count(r =>
            r.Status == ChatRunStatus.Completed && string.IsNullOrWhiteSpace(r.PartialContent));
        if (deadEnd >= 2)
        {
            issues.Add(new InsightIssue("empty_run", "high",
                $"{deadEnd} run(s) concluídas sem conteúdo", deadEnd));
        }

        return issues;
    }

    /// <summary>Timeline cor-codificada (cap 100, mais recentes primeiro — RF-002).</summary>
    private static List<InsightTimelineEvent> ComputeTimeline(
        List<ChatRun> runs, List<ChatRunEvent> events)
    {
        var timeline = new List<InsightTimelineEvent>();
        foreach (var e in events)
        {
            var (kind, title) = e.Type switch
            {
                "tool_call" => ("event", $"tool: {PayloadTitle(e.PayloadJson)}"),
                "tool_result" => ContainsAny(e.PayloadJson, "error", "failed", "exception")
                    ? ("error", $"tool falhou: {PayloadTitle(e.PayloadJson)}")
                    : ("event", $"tool ok: {PayloadTitle(e.PayloadJson)}"),
                "step_finished" => ("value", "step concluído"),
                "note" => ContainsAny(e.PayloadJson, "retry", "recover")
                    ? ("warn", "retry/recuperação")
                    : ("event", "nota"),
                _ => ("event", e.Type),
            };
            timeline.Add(new InsightTimelineEvent(
                e.CreatedAt, kind, Truncate(title, 80), e.RunId));
        }
        return timeline
            .OrderByDescending(t => t.At)
            .Take(TimelineCap)
            .ToList();
    }

    /// <summary>Categoria determinística por sinais de tools + keywords (RF-001).</summary>
    private static string ClassifyCategory(Chat chat, List<ChatRunEvent> events)
    {
        var toolNames = events
            .Where(e => e.Type == "tool_call")
            .Select(e => PayloadTitle(e.PayloadJson))
            .ToList();
        var tests = toolNames.Count(n => ContainsAny(n, "test"));
        var research = toolNames.Count(n => ContainsAny(n, "web_search", "fetch", "search"));
        var writes = toolNames.Count(n =>
            ContainsAny(n, "file", "patch", "edit", "write"));

        var title = chat.Title ?? "";
        if (tests > 0 && tests >= writes)
        {
            return "tests";
        }
        if (ContainsAny(title, "bug", "fix", "erro", "error", "falha"))
        {
            return "bugfix";
        }
        if (ContainsAny(title, "refactor", "refator", "cleanup"))
        {
            return "refactor";
        }
        if (ContainsAny(title, "doc", "document", "readme"))
        {
            return "docs";
        }
        if (ContainsAny(title, "deploy", "ci", "docker", "pipeline"))
        {
            return "devops";
        }
        if (writes > 0)
        {
            return "feature_development";
        }
        if (research > 0)
        {
            return "research";
        }
        return "uncategorized";
    }

    /// <summary>Resumo do payload para título — primeiro campo de texto útil.</summary>
    private static string PayloadTitle(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String
                    && prop.Value.GetString() is { Length: > 0 } s)
                {
                    return s;
                }
            }
        }
        catch (JsonException)
        {
            // cai no fallback abaixo.
        }
        return Truncate(payload, 40);
    }

    private static bool ContainsAny(string? haystack, params string[] needles) =>
        haystack is not null && needles.Any(n =>
            haystack.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    /// <summary>
    /// Dreaming-lite (SPEC-20261104-dreaming-lite RF-001): materializa action
    /// items da análise como registros PESSOAIS do chamador —
    /// <c>knowledge</c>→<see cref="KnowledgeNote"/> (trigger=resumo),
    /// <c>memory</c>→<see cref="AgentMemory"/> (escopo repo quando o chat tem
    /// binding, senão global — upsert por título). <c>skill</c> e demais
    /// kinds são skip explícito; falha num item não aborta os demais.
    /// </summary>
    public async Task<InsightApplyResponse> ApplyActionItemsAsync(
        User user, Chat chat, IReadOnlyList<InsightApplyItem> items, CancellationToken ct)
    {
        var applied = new List<InsightApplyResult>();
        var skipped = new List<InsightApplySkip>();
        string? repoSlug = null;
        var repoResolved = false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var item in items)
        {
            var text = item.Text.Trim();
            if (text.Length == 0)
            {
                skipped.Add(new InsightApplySkip(item.Kind, "empty"));
                continue;
            }
            var title = Truncate(text, 120);

            if (item.Kind == "knowledge")
            {
                var exists = await db.KnowledgeNotes.AsNoTracking()
                    .AnyAsync(n => n.UserId == user.Id && n.Title == title, ct);
                if (exists)
                {
                    skipped.Add(new InsightApplySkip(item.Kind, "duplicate"));
                    continue;
                }
                var note = new KnowledgeNote
                {
                    UserId = user.Id,
                    Title = title,
                    Trigger = Truncate(text, 500),
                    Content = text,
                    Pinned = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.KnowledgeNotes.Add(note);
                await db.SaveChangesAsync(ct);
                applied.Add(new InsightApplyResult(item.Kind, note.Id, title));
            }
            else if (item.Kind == "memory")
            {
                if (!repoResolved)
                {
                    var (binding, _) = await repos.ResolveBindingAsync(user.Id, chat.Id, ct);
                    repoSlug = binding?.Repo;
                    repoResolved = true;
                }
                var scope = repoSlug is not null ? "repo" : "global";
                var existing = await db.AgentMemories
                    .FirstOrDefaultAsync(m => m.UserId == user.Id
                        && m.Scope == scope && m.RepoSlug == repoSlug
                        && m.Title == title, ct);
                if (existing is not null)
                {
                    skipped.Add(new InsightApplySkip(item.Kind, "duplicate"));
                    continue;
                }
                var memory = new AgentMemory
                {
                    UserId = user.Id,
                    Scope = scope,
                    RepoSlug = repoSlug,
                    Title = title,
                    Content = text,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.AgentMemories.Add(memory);
                await db.SaveChangesAsync(ct);
                applied.Add(new InsightApplyResult(item.Kind, memory.Id, title));
            }
            else
            {
                skipped.Add(new InsightApplySkip(
                    item.Kind, item.Kind == "skill" ? "requires_draft" : "unsupported"));
            }
        }

        return new InsightApplyResponse(applied, skipped);
    }
}
