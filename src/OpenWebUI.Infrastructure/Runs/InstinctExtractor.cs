using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.Runs;

/// <summary>
/// Extração de instincts pós-run (SPEC-20261023-instincts-learning RF-001):
/// em <c>run.completed</c> de chat não-efêmero com sinal suficiente
/// (≥3 steps ou feedback positivo), destila o transcript via provider
/// (role <c>memory</c>) em 0–3 candidatos {domain,trigger,body} e grava
/// <c>pending</c> — dedup/reforço pelo <see cref="InstinctService"/>.
/// Em <c>run.failed</c> penaliza os instincts elegíveis (erro atribuível,
/// heurística v1). Sempre fora do caminho crítico: qualquer exceção vira
/// log, nunca propaga para a run.
/// </summary>
public sealed class InstinctExtractor(
    IServiceScopeFactory scopes,
    ILogger<InstinctExtractor> logger) : IAgentRunObserver
{
    /// <summary>Steps mínimos para uma run ser elegível sem feedback.</summary>
    public const int MinSteps = 3;

    /// <summary>Mensagens mais recentes enviadas à destilação.</summary>
    private const int MaxMessages = 20;

    /// <summary>Cap por mensagem no transcript.</summary>
    private const int MaxMessageChars = 500;

    /// <summary>Cap total do transcript enviado ao modelo.</summary>
    private const int MaxTranscriptChars = 6000;

    /// <inheritdoc/>
    public async Task OnRunEventAsync(AgentRunEvent e, CancellationToken ct)
    {
        if (e.Type is not ("run.completed" or "run.failed")
            || e.UserId is null || e.ChatId is null)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
            var instincts = scope.ServiceProvider
                .GetRequiredService<InstinctService>();

            var chat = await db.Chats.AsNoTracking()
                .Where(c => c.Id == e.ChatId)
                .Select(c => new { c.IsEphemeral, c.UserId })
                .FirstOrDefaultAsync(ct);
            if (chat is null || chat.IsEphemeral)
            {
                return;
            }

            // Chats de gateway carregam input de terceiros — não alimentam
            // instincts sem opt-in explícito (SPEC messaging-gateway).
            if (await db.GatewaySessions.AsNoTracking()
                    .AnyAsync(s => s.ChatId == e.ChatId, ct)
                && !await config.GetAsync("instincts.includeGateway", false, ct))
            {
                return;
            }

            var repos = scope.ServiceProvider
                .GetRequiredService<WorkspaceRepoService>();
            var binding = await repos.GetChatBindingAsync(e.ChatId, ct)
                ?? await repos.GetBindingAsync(e.UserId, ct);
            var repoSlug = binding?.Repo;

            if (e.Type == "run.failed")
            {
                await instincts.RecordFailureAsync(e.UserId, repoSlug, ct);
                return;
            }

            await instincts.RecordInjectionHitsAsync(e.UserId, repoSlug, ct);

            if (!await config.GetAsync("instincts.extract", true, ct))
            {
                return;
            }

            var eligible = await IsEligibleAsync(db, e, ct);
            if (!eligible)
            {
                return;
            }

            await ExtractAsync(scope.ServiceProvider, db,
                new ExtractTarget(e.UserId, e.ChatId, e.RunId, repoSlug),
                instincts, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // RF-001: exceção do extractor nunca afeta a run — só log.
            logger.LogWarning(ex,
                "instincts: extração falhou para run {RunId}", e.RunId);
        }
    }

    /// <summary>Elegibilidade: ≥MinSteps no usage da run ou ao menos um
    /// feedback positivo no chat.</summary>
    private static async Task<bool> IsEligibleAsync(
        AppDbContext db, AgentRunEvent e, CancellationToken ct)
    {
        var steps = await db.ChatRuns.AsNoTracking()
            .Where(r => r.Id == e.RunId)
            .Select(r => r.MetaJson)
            .FirstOrDefaultAsync(ct);
        if (ParseSteps(steps) >= MinSteps)
        {
            return true;
        }
        return await db.Feedbacks.AsNoTracking()
            .AnyAsync(f => f.ChatId == e.ChatId && f.Rating > 0, ct);
    }

    /// <summary>Lê <c>MetaJson.usage.steps</c>; formato ausente = 0.</summary>
    private static int ParseSteps(string? metaJson)
    {
        if (string.IsNullOrEmpty(metaJson))
        {
            return 0;
        }
        try
        {
            using var doc = JsonDocument.Parse(metaJson);
            if (doc.RootElement.TryGetProperty("usage", out var usage)
                && usage.TryGetProperty("steps", out var steps)
                && steps.ValueKind == JsonValueKind.Number)
            {
                return steps.GetInt32();
            }
        }
        catch (JsonException)
        {
            // MetaJson malformado = sem sinal de steps.
        }
        return 0;
    }

    /// <summary>Destila o transcript em candidatos e grava via upsert.</summary>
    private sealed record ExtractTarget(
        string UserId, string ChatId, string RunId, string? RepoSlug);

    private async Task ExtractAsync(
        IServiceProvider sp, AppDbContext db, ExtractTarget t,
        InstinctService instincts, CancellationToken ct)
    {
        var roles = sp.GetRequiredService<ModelRoleResolver>();
        var providers = sp.GetRequiredService<ProviderService>();

        var chat = await db.Chats.AsNoTracking()
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == t.ChatId, ct);
        if (chat is null)
        {
            return;
        }
        var transcript = BuildTranscript(chat);
        if (transcript is null)
        {
            return;
        }

        var model = await roles.ResolveAsync(
            "memory", t.UserId, FirstModel(chat.ModelsJson) ?? string.Empty, ct);
        if (string.IsNullOrWhiteSpace(model))
        {
            return;
        }

        string reply;
        try
        {
            reply = await providers.CompleteAsync(
                new ChatCompletionRequest(
                    model,
                    [new ChatCompletionMessage("user", ExtractPrompt + transcript)],
                    Stream: false),
                t.UserId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex,
                "instincts: provider falhou na extração do chat {ChatId}", t.ChatId);
            return;
        }

        var scope = t.RepoSlug is not null
            ? InstinctService.ScopeWorkspace
            : InstinctService.ScopeUser;
        foreach (var c in ParseCandidates(reply))
        {
            await instincts.UpsertCandidateAsync(
                new InstinctService.InstinctCandidate(
                    t.UserId, scope, t.RepoSlug, c.Domain, c.Trigger,
                    c.Body, t.RunId), ct);
        }
    }

    /// <summary>
    /// Parse tolerante da resposta do extrator: espera um array JSON
    /// [{domain,trigger,body}] — a resposta pode vir cercada de prosa ou
    /// code fence; qualquer falha = lista vazia (descarta silenciosamente).
    /// </summary>
    public static IReadOnlyList<(string Domain, string Trigger, string Body)>
        ParseCandidates(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return [];
        }

        var text = reply.Trim();
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var result = new List<(string, string, string)>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object
                    || !el.TryGetProperty("domain", out var d)
                    || !el.TryGetProperty("trigger", out var t)
                    || !el.TryGetProperty("body", out var b))
                {
                    continue;
                }
                var domain = d.GetString() ?? string.Empty;
                var trigger = t.GetString() ?? string.Empty;
                var body = b.GetString() ?? string.Empty;
                if (domain.Length > 0 && trigger.Length > 0 && body.Length > 0)
                {
                    result.Add((domain, trigger, body));
                }
                if (result.Count >= 3)
                {
                    break;
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Transcript com os mesmos caps do MemorySyncService.</summary>
    private static string? BuildTranscript(Chat chat)
    {
        var messages = BranchService.WalkPath(chat)
            .Where(m => m.Role is "user" or "assistant")
            .TakeLast(MaxMessages)
            .ToList();
        if (messages.Count == 0)
        {
            return null;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var m in messages)
        {
            var content = m.Content ?? string.Empty;
            if (content.Length > MaxMessageChars)
            {
                content = content[..MaxMessageChars] + "…";
            }
            sb.Append(m.Role).Append(": ").AppendLine(content);
            if (sb.Length > MaxTranscriptChars)
            {
                break;
            }
        }
        var transcript = sb.ToString();
        return transcript.Length > MaxTranscriptChars
            ? transcript[..MaxTranscriptChars]
            : transcript;
    }

    private static string? FirstModel(string modelsJson)
    {
        try
        {
            var models = JsonSerializer.Deserialize<string[]>(modelsJson);
            return models is { Length: > 0 } ? models[0] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Prompt fixo de destilação — exige JSON puro e instrui
    /// descartar PII (memória factual é papel do MemorySyncService).</summary>
    private const string ExtractPrompt =
        "Below is a transcript of a coding-agent session. Extract at most 3 "
            + "reusable behavioral patterns ('instincts') the agent should "
            + "apply in future sessions: conventions followed, workflow "
            + "choices that worked, domain-specific habits (build/test "
            + "commands, review style, tool preferences). Do NOT extract "
            + "personal data or facts about the user — only operational "
            + "patterns. Answer with a JSON array only, no prose, no code "
            + "fence: [{\"domain\":\"dotnet|git|review|…\",\"trigger\":\"when "
            + "X happens\",\"body\":\"do Y (≤500 chars)\"}]. If nothing "
            + "worthwhile, answer [].\n\n";
}
