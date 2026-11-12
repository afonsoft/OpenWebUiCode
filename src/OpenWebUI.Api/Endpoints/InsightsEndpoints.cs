using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Session Insights v2 (SPEC-20261104-session-insights-v2):
/// <c>GET /api/v1/chats/{id}/insights</c> devolve classificação heurística
/// (tamanho/categoria/issues/timeline) + última análise;
/// <c>POST …/insights/generate</c> roda a análise LLM sob demanda;
/// <c>GET /api/v1/admin/insights/summary</c> agrega por período.
/// </summary>
public static class InsightsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia o grupo de insights por chat + o summary admin.</summary>
    public static IEndpointRouteBuilder MapInsightsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/chats").RequireAuthorization();
        group.MapGet("/{chatId}/insights", GetInsightAsync);
        group.MapPost("/{chatId}/insights/generate", GenerateInsightAsync);
        group.MapPost("/{chatId}/insights/apply", ApplyInsightAsync);

        var admin = app.MapGroup("/api/v1/admin/insights").RequireAuthorization();
        admin.MapGet("/summary", GetSummaryAsync);
        return app;
    }

    /// <summary>GET — computa lazy e devolve classificação + timeline + análise.</summary>
    private static async Task<IResult> GetInsightAsync(
        string chatId, HttpContext http, AppDbContext db,
        ChatInsightsService insights, CancellationToken ct)
    {
        var (user, chat) = await FindOwnerAsync(http, db, chatId, ct);
        if (user is null || chat is null)
        {
            return Results.NotFound();
        }
        var insight = await insights.EnsureComputedAsync(chat, ct);
        return Results.Ok(ToResponse(insight));
    }

    /// <summary>POST — gera (ou regenera) a análise LLM do chat (RF-003).</summary>
    private static async Task<IResult> GenerateInsightAsync(
        string chatId, HttpContext http, AppDbContext db,
        ChatInsightsService insights, ProviderService providers,
        ModelRoleResolver roles, CancellationToken ct)
    {
        var (user, chat) = await FindOwnerAsync(http, db, chatId, ct);
        if (user is null || chat is null)
        {
            return Results.NotFound();
        }

        var model = await roles.ResolveAsync("tasks", user.Id, "", ct);
        if (string.IsNullOrEmpty(model))
        {
            return Results.UnprocessableEntity(new
            {
                detail = "Nenhum modelo configurado para análise — conecte um provider ou BYOK.",
            });
        }

        var insight = await insights.EnsureComputedAsync(chat, ct);
        var prompt = await BuildAnalysisPromptAsync(db, chat, insight, ct);

        var analysis = await TryAnalyzeAsync(providers, model, prompt, user.Id, ct);
        if (analysis is null)
        {
            // Retry 1x com instrução reforçada (RF-003).
            analysis = await TryAnalyzeAsync(
                providers, model, prompt + "\n\nRespond ONLY with a single JSON object.", user.Id, ct);
        }
        if (analysis is null)
        {
            return Results.Problem(
                "O modelo não retornou uma análise JSON válida.",
                statusCode: StatusCodes.Status502BadGateway);
        }

        insight.AnalysisJson = JsonSerializer.Serialize(analysis, JsonOpts);
        insight.ModelId = model;
        insight.GeneratedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(insight));
    }

    /// <summary>POST — materializa action items como registros pessoais do
    /// chamador (SPEC-20261104-dreaming-lite RF-001): knowledge→KnowledgeNote,
    /// memory→AgentMemory (repo|global), skill→skipped requires_draft.</summary>
    private static async Task<IResult> ApplyInsightAsync(
        string chatId, InsightApplyRequest? request, HttpContext http,
        AppDbContext db, ChatInsightsService insights, CancellationToken ct)
    {
        var (user, chat) = await FindOwnerAsync(http, db, chatId, ct);
        if (user is null || chat is null)
        {
            return Results.NotFound();
        }
        if (request is null || request.Items.Count == 0)
        {
            return Results.BadRequest(new { error = "items_required" });
        }
        if (request.Items.Count > 50)
        {
            return Results.BadRequest(new { error = "too_many_items" });
        }
        return Results.Ok(await insights.ApplyActionItemsAsync(
            user, chat, request.Items, ct));
    }

    /// <summary>GET admin — agregados de insights no período (RF-005).</summary>
    private static async Task<IResult> GetSummaryAsync(
        HttpContext http, AppDbContext db, int? days, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }

        var cutoff = DateTimeOffset.UtcNow
            .AddDays(-(days is > 0 and <= 365 ? days.Value : 30))
            .ToUnixTimeSeconds();
        var rows = await db.ChatInsights.AsNoTracking()
            .Where(i => i.UpdatedAt >= cutoff)
            .ToListAsync(ct);

        var sizeDist = rows
            .GroupBy(i => i.SizeClass)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var s in new[] { "XS", "S", "M", "L", "XL" })
        {
            sizeDist.TryAdd(s, 0);
        }

        var byCategory = rows
            .GroupBy(i => i.Category)
            .Select(g => new CategoryCount(g.Key, g.Count()))
            .OrderByDescending(c => c.Count)
            .ToList();

        var issueCounts = new Dictionary<(string Label, string Impact), int>();
        foreach (var row in rows)
        {
            foreach (var issue in Deserialize<List<InsightIssue>>(row.IssuesJson) ?? [])
            {
                var key = (issue.Label, issue.Impact);
                issueCounts[key] = issueCounts.GetValueOrDefault(key) + issue.Count;
            }
        }
        var topIssues = issueCounts
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => new IssueCount(kv.Key.Label, kv.Key.Impact, kv.Value))
            .ToList();

        return Results.Ok(new AdminInsightsSummary(
            sizeDist, byCategory, topIssues,
            rows.Count(r => r.SizeClass is "L" or "XL")));
    }

    /// <summary>Owner ou admin; outros recebem null (404 na superfície).</summary>
    private static async Task<(User? User, Chat? Chat)> FindOwnerAsync(
        HttpContext http, AppDbContext db, string chatId, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null);
        }
        var chat = await db.Chats.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null || (chat.UserId != user.Id && user.Role != UserRoles.Admin))
        {
            return (user, null);
        }
        return (user, chat);
    }

    /// <summary>Prompt estruturado: métricas + issues + transcript truncado (RF-003).</summary>
    private static async Task<string> BuildAnalysisPromptAsync(
        AppDbContext db, Chat chat, ChatInsight insight, CancellationToken ct)
    {
        var messages = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == chat.Id)
            .OrderBy(m => m.Timestamp)
            .Select(m => new { m.Role, m.Content })
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("You are analyzing an agent session. Respond ONLY with a JSON object");
        sb.AppendLine("{\"summary\": string, \"issues\": [string], \"improvedPrompt\": string,");
        sb.AppendLine(" \"changes\": [{\"what\": string, \"why\": string}],");
        sb.AppendLine(" \"actionItems\": [{\"kind\": \"machine\"|\"repo\"|\"skill\"|\"knowledge\", \"text\": string}],");
        sb.AppendLine(" \"knowledgeUsed\": [string]}.");
        sb.AppendLine();
        sb.AppendLine($"Title: {chat.Title}");
        sb.AppendLine($"Size: {insight.SizeClass} · Category: {insight.Category}");
        sb.AppendLine($"Metrics: {insight.MetricsJson}");
        sb.AppendLine($"Detected issues: {insight.IssuesJson}");
        sb.AppendLine();
        sb.AppendLine("### Transcript (truncated)");

        var picked = messages.Count <= 6
            ? messages
            : messages.Take(2).Concat(messages.Skip(messages.Count - 4)).ToList();
        foreach (var m in picked)
        {
            var content = m.Content.Length > 400 ? m.Content[..400] + "…" : m.Content;
            sb.AppendLine($"{m.Role}: {content}");
        }
        return sb.ToString();
    }

    /// <summary>Chama o modelo de tasks do usuário e faz parse tolerante do JSON.</summary>
    private static async Task<InsightAnalysis?> TryAnalyzeAsync(
        ProviderService providers, string model, string prompt,
        string userId, CancellationToken ct)
    {
        string raw;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            raw = await providers.CompleteAsync(
                new ChatCompletionRequest(
                    model, [new ChatCompletionMessage("user", prompt)], Stream: false),
                userId, timeout.Token);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }
        try
        {
            return ParseAnalysis(
                JsonDocument.Parse(raw[start..(end + 1)]).RootElement, model);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parse tolerante do JSON do modelo → <see cref="InsightAnalysis"/>.</summary>
    private static InsightAnalysis ParseAnalysis(JsonElement doc, string model) =>
        new(
            Summary: doc.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
            Issues: StrList(doc, "issues"),
            ImprovedPrompt: doc.TryGetProperty("improvedPrompt", out var ip)
                ? ip.GetString() : null,
            Changes: doc.TryGetProperty("changes", out var ch)
                ? ch.EnumerateArray().Select(e => new InsightChange(
                    e.TryGetProperty("what", out var w) ? w.GetString() ?? "" : "",
                    e.TryGetProperty("why", out var y) ? y.GetString() ?? "" : ""))
                    .ToList()
                : [],
            ActionItems: doc.TryGetProperty("actionItems", out var ai)
                ? ai.EnumerateArray().Select(e => new InsightActionItem(
                    e.TryGetProperty("kind", out var k) ? k.GetString() ?? "repo" : "repo",
                    e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""))
                    .ToList()
                : [],
            KnowledgeUsed: StrList(doc, "knowledgeUsed"),
            ModelId: model,
            GeneratedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static List<string> StrList(JsonElement doc, string name) =>
        doc.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "")
                .Where(s => s.Length > 0).ToList()
            : [];

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static ChatInsightResponse ToResponse(ChatInsight insight) => new(
        insight.SizeClass,
        insight.Category,
        Deserialize<InsightMetrics>(insight.MetricsJson)
            ?? new InsightMetrics(0, 0, 0m, 0, 0),
        Deserialize<List<InsightIssue>>(insight.IssuesJson) ?? [],
        Deserialize<List<InsightTimelineEvent>>(insight.TimelineJson) ?? [],
        Deserialize<InsightAnalysis>(insight.AnalysisJson ?? "null"));
}
