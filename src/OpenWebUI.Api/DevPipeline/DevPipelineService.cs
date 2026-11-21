using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.DevPipeline;

/// <summary>
/// Orquestrador do ciclo de desenvolvimento (SPEC-20261121-dev-pipeline
/// RF-004): uma tool dispara e o servidor encadeia as fases — runs LLM
/// <c>implement</c> → <c>test</c> no chat filho, depois <c>validate</c> e
/// <c>commit</c> determinísticos (sem LLM), com report-back no chat pai.
/// O estado vive em kv (<c>devpipeline:{id}</c>) para sobreviver a restart
/// — <c>resume</c> re-dispara do ponto onde parou. A garantia é estrutural:
/// validação e commit não dependem do modelo lembrar de fazê-los.
/// </summary>
public sealed class DevPipelineService(
    IServiceScopeFactory scopes,
    IConfiguration cfg,
    ILogger<DevPipelineService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Intervalo de poll esperando o término de uma run de fase.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Estado durável da pipeline (kv <c>devpipeline:{id}</c>).</summary>
    public sealed record State
    {
        public required string Id { get; init; }
        public required string UserId { get; init; }
        public required string ParentChatId { get; init; }
        public required string ParentRunId { get; init; }
        public string? ChildChatId { get; set; }
        public required string Task { get; init; }
        public string? ExtraContext { get; init; }
        public bool Push { get; init; }
        public int MaxFixAttempts { get; init; }
        public int FixAttempts { get; set; }
        /// <summary>Fase atual: implement → test → validate → commit → report.</summary>
        public string Phase { get; set; } = "implement";
        /// <summary>Status: running | done | failed.</summary>
        public string Status { get; set; } = "running";
        public string? Error { get; set; }
        /// <summary>Runs por fase (implement/test/fix/report) — runId.</summary>
        public Dictionary<string, string> Runs { get; set; } = new();
        public string? ValidationOutput { get; set; }
        public string? CommitSha { get; set; }
        public long CreatedAt { get; init; }
        public long UpdatedAt { get; set; }
    }

    private static string Key(string id) => $"devpipeline:{id}";

    /// <summary>Cria o kv + chat filho (build, binding herdado) e devolve o estado inicial.</summary>
    public async Task<State> StartAsync(
        BuiltinToolContext context, string task, string? extraContext,
        bool push, int maxFixAttempts, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var state = new State
        {
            Id = $"dp-{Guid.NewGuid():N}"[..12],
            UserId = context.UserId,
            ParentChatId = context.ChatId ?? string.Empty,
            ParentRunId = context.RunId ?? string.Empty,
            Task = task,
            ExtraContext = extraContext,
            Push = push,
            MaxFixAttempts = Math.Max(0, maxFixAttempts),
            CreatedAt = now,
            UpdatedAt = now,
        };

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repos = scope.ServiceProvider.GetRequiredService<WorkspaceRepoService>();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();

        var preset = context.ChatId is { } pid
            ? await db.Chats.AsNoTracking()
                .Where(c => c.Id == pid)
                .Select(c => c.ApprovalPreset).FirstOrDefaultAsync(ct)
            : null;
        var title = task.Length > 56 ? task[..56] + "…" : task;
        var childChat = new Chat
        {
            UserId = context.UserId,
            Title = $"pipeline: {title}",
            ParentChatId = context.ChatId,
            ModelsJson = JsonSerializer.Serialize(new[] { context.Model ?? "default" }, JsonOptions),
            ApprovalPreset = preset ?? "approve-mutations",
            Mode = PermissionRuleset.BuildMode,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Chats.Add(childChat);

        // Binding por chat do pai precede o global — o filho trabalha no
        // mesmo repo (worktree próprio por conversa, SPEC-15).
        if (context.ChatId is { } boundParent
            && await repos.GetChatBindingAsync(boundParent, ct) is { } binding)
        {
            await repos.SetChatBindingAsync(
                childChat.Id,
                repos.InheritChatBinding(childChat.Id, binding), ct);
        }
        await db.SaveChangesAsync(ct);
        state.ChildChatId = childChat.Id;

        await SaveAsync(scope, state, ct);
        return state;
    }

    /// <summary>Lê o estado corrente do kv (null se inexistente).</summary>
    public async Task<State?> GetAsync(string pipelineId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
        var json = await config.GetAsync<string?>(Key(pipelineId), null, ct);
        return json is null
            ? null
            : JsonSerializer.Deserialize<State>(json, JsonOptions);
    }

    /// <summary>Ponto de entrada do runner em background — idempotente por
    /// fase: re-drive salta fases cujas runs já completaram (resume).</summary>
    public async Task RunAsync(string pipelineId)
    {
        var state = await GetAsync(pipelineId, CancellationToken.None);
        if (state is null || state.Status != "running")
        {
            return;
        }
        try
        {
            await DriveAsync(state, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pipeline {Id} falhou com exceção.", pipelineId);
            await MarkFailedAsync(state, $"exceção no orquestrador: {ex.Message}");
        }
    }

    /// <summary>Loop de fases da pipeline.</summary>
    private async Task DriveAsync(State state, CancellationToken ct)
    {
        // implement → test: runs LLM encadeadas no chat filho. Resume:
        // fase com run completed salta; run ainda ativa é aguardada.
        foreach (var (phase, prompt) in LlmPhases(state))
        {
            if (state.Runs.TryGetValue(phase, out var existing)
                && await RunStatusAsync(existing, ct) is { Status: ChatRunStatus.Completed })
            {
                continue;
            }
            var runId = existing ?? await EnqueuePhaseRunAsync(
                state, phase, prompt, ct);
            state.Runs[phase] = runId;
            state.Phase = phase;
            await SaveAsync(null, state, ct);
            var terminal = await WaitRunAsync(runId, ct);
            if (terminal.Status is not ChatRunStatus.Completed)
            {
                await MarkFailedAsync(state,
                    $"fase '{phase}' terminou como '{terminal.Status}'"
                        + (terminal.Error is { } e ? $": {e}" : string.Empty));
                return;
            }
        }

        // validate: determinístico no workdir efetivo do filho — com retry
        // fix→validate até MaxFixAttempts.
        state.Phase = "validate";
        await SaveAsync(null, state, ct);
        var outcome = await ValidateAsync(state, ct);
        while (outcome is { Passed: false } && state.FixAttempts < state.MaxFixAttempts)
        {
            state.FixAttempts++;
            var fixPrompt =
                "[dev_pipeline — CORREÇÃO]\nA validação falhou:\n"
                + Tail(outcome!.Output, 4000)
                + "\nCorrija a causa raiz no workdir. NÃO faça commit — o commit é orquestrado.";
            var fixRunId = await EnqueuePhaseRunAsync(state, "fix", fixPrompt, ct);
            state.Runs[$"fix{state.FixAttempts}"] = fixRunId;
            await SaveAsync(null, state, ct);
            var fixTerminal = await WaitRunAsync(fixRunId, ct);
            if (fixTerminal.Status is not ChatRunStatus.Completed)
            {
                await MarkFailedAsync(state,
                    $"fase 'fix' terminou como '{fixTerminal.Status}'");
                return;
            }
            outcome = await ValidateAsync(state, ct);
        }
        state.ValidationOutput = outcome?.Summary;
        await SaveAsync(null, state, ct);
        if (outcome is { Passed: false })
        {
            await MarkFailedAsync(state,
                $"validação falhou após {state.FixAttempts} correção(ões): "
                    + Tail(outcome.Summary, 500));
            return;
        }

        // commit: determinístico — worktree isolada (se houver) mergeia no
        // workdir do filho antes do commit. Idempotente no resume.
        if (state.CommitSha is not null)
        {
            state.Phase = "report";
            await SaveAsync(null, state, ct);
            await ReportAsync(state, ct);
            return;
        }
        state.Phase = "commit";
        await SaveAsync(null, state, ct);
        var commit = await CommitAsync(state, ct);
        state.CommitSha = commit;
        state.Phase = "report";
        state.Status = "done";
        await SaveAsync(null, state, ct);

        await ReportAsync(state, ct);
    }

    /// <summary>Runs LLM da pipeline com os prompts de fase.</summary>
    private static IEnumerable<(string Phase, string Prompt)> LlmPhases(State state)
    {
        yield return ("implement",
            "[dev_pipeline fase 1/4 — IMPLEMENTAÇÃO]\n"
                + $"Tarefa: {state.Task}\n"
                + (state.ExtraContext is { } x ? $"Contexto: {x}\n" : string.Empty)
                + "Implemente a tarefa no workdir atual. NÃO escreva testes "
                + "ainda (fase seguinte) e NÃO faça commit — testes, "
                + "validação e commit são orquestrados depois. Ao terminar, "
                + "liste os arquivos criados/alterados.");
        yield return ("test",
            "[dev_pipeline fase 2/4 — TESTES]\n"
                + "A implementação anterior alterou o workdir. Escreva ou "
                + "ajuste os testes do que foi implementado seguindo as "
                + "convenções do projeto. NÃO faça commit — validação e "
                + "commit são orquestrados. Ao terminar, liste os arquivos "
                + "de teste.");
    }

    /// <summary>
    /// Cria mensagem <c>user</c> + run na fase dentro do chat filho —
    /// mesmo padrão do drain de steers (histórico encadeado preserva o
    /// contexto das fases anteriores; <c>ParentRunId</c> mantém a cadeia e
    /// reutiliza o worktree isolado da fase anterior via sameChatParent).
    /// </summary>
    private async Task<string> EnqueuePhaseRunAsync(
        State state, string phase, string prompt, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var boundaries = scope.ServiceProvider.GetRequiredService<ContextBoundaryService>();
        var chat = await db.Chats.Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == state.ChildChatId, ct)
            ?? throw new InvalidOperationException(
                $"chat filho {state.ChildChatId} sumiu");
        var parentRun = await db.ChatRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == state.ParentRunId, ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var message = new ChatMessage
        {
            ChatId = chat.Id,
            Role = "user",
            Content = prompt,
            Position = chat.Messages.Count == 0
                ? 0
                : chat.Messages.Max(m => m.Position) + 1,
            Timestamp = now,
            ParentId = BranchService.ResolveLeaf(chat)?.Id,
        };
        chat.Messages.Add(message);
        chat.LeafMessageId = message.Id;
        chat.UpdatedAt = now;

        var path = BranchService.WalkPath(chat);
        var visible = await boundaries.FilterAsync(chat.Id, path, ct);
        var history = visible.Select(m => new ChatCompletionMessage(
            m.Role, m.Content, m.ToolCallId, m.ToolCallsJson,
            FileIds: ParseFileIds(m.FilesJson))).ToList();
        var request = parentRun is not null
            && JsonSerializer.Deserialize<ChatCompletionRequest>(
                parentRun.RequestJson, JsonOptions) is { } baseReq
                ? baseReq with { Messages = history }
                : new ChatCompletionRequest(parentRun?.Model ?? "default", history);
        var previousRunId = state.Runs.TryGetValue("test", out var t)
            ? t
            : state.Runs.TryGetValue("implement", out var i)
                ? i
                : state.ParentRunId;
        var run = new ChatRun
        {
            ChatId = chat.Id,
            UserId = state.UserId,
            Model = request.Model,
            RequestJson = JsonSerializer.Serialize(request, JsonOptions),
            ParentRunId = previousRunId ?? state.ParentRunId,
            MetaJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["devPipeline"] = new { id = state.Id, phase },
            }, JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync(ct);
        scope.ServiceProvider.GetRequiredService<Runs.ChatRunDispatcher>()
            .Enqueue(run.Id);
        return run.Id;
    }

    /// <summary>Poll do término da run (padrão WaitForChildAsync) —
    /// deadline configurável <c>Agent:DevPipeline:PhaseTimeoutSec</c>
    /// (default 1200s). Devolve (status, error); "timeout"/"missing" nas
    /// bordas.</summary>
    private async Task<(string Status, string? Error)> WaitRunAsync(
        string runId, CancellationToken ct)
    {
        var timeout = int.TryParse(
            cfg["Agent:DevPipeline:PhaseTimeoutSec"], out var s)
                ? Math.Max(30, s) : 1200;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, ct);
            var (status, error) = await RunStatusAsync(runId, ct);
            if (status is null or ChatRunStatus.Queued
                or ChatRunStatus.Running or ChatRunStatus.Paused)
            {
                continue;
            }
            return (status, error);
        }
        return ("timeout", $"run {runId} excedeu {timeout}s");
    }

    /// <summary>Status atual de uma run (null quando inexistente).</summary>
    private async Task<(string? Status, string? Error)> RunStatusAsync(
        string runId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ChatRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.Status, r.Error })
            .FirstOrDefaultAsync(ct);
        return run is null ? (null, null) : (run.Status, run.Error);
    }

    /// <summary>Workdir efetivo: worktree isolada da run da fase 1 (runs
    /// encadeadas a reutilizam) ou o workdir do chat filho.</summary>
    private async Task<string> EffectiveWorkdirAsync(State state, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var worktrees = scope.ServiceProvider.GetRequiredService<WorktreeService>();
        var repos = scope.ServiceProvider.GetRequiredService<WorkspaceRepoService>();
        return state.Runs.TryGetValue("implement", out var implRun)
                && worktrees.ResolveIsolated(state.UserId, implRun) is { } iso
            ? iso
            : await repos.ResolveWorkdirAsync(state.UserId, state.ChildChatId, ct);
    }

    /// <summary>Fase validate — determinística, no workdir efetivo.</summary>
    private async Task<ValidationOutcome?> ValidateAsync(State state, CancellationToken ct)
    {
        var workdir = await EffectiveWorkdirAsync(state, ct);
        var command = ProjectValidation.Detect(workdir);
        if (command is null)
        {
            // Nada detectável → não bloqueia a pipeline; reporta o skip.
            return new ValidationOutcome(true, "validate_skipped: nenhum stack reconhecido", "");
        }
        var result = await ProjectValidation.RunAsync(
            workdir, command, ProjectValidation.MaxTimeout, ct);
        return new ValidationOutcome(
            result.Passed,
            $"[validate] {command} → exit {result.Outcome.ExitCode}",
            result.Outcome.Output);
    }

    /// <summary>Fase commit — determinística: merge do worktree isolado no
    /// workdir do filho (quando há), <c>git add -A</c>, commit
    /// Conventional e push opcional.</summary>
    private async Task<string?> CommitAsync(State state, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var repos = scope.ServiceProvider.GetRequiredService<WorkspaceRepoService>();
        var worktrees = scope.ServiceProvider.GetRequiredService<WorktreeService>();
        var workdir = await repos.ResolveWorkdirAsync(
            state.UserId, state.ChildChatId, ct);

        if (state.Runs.TryGetValue("implement", out var implRun)
            && worktrees.ResolveIsolated(state.UserId, implRun) is { } iso)
        {
            var merge = await worktrees.MergeAsync(workdir, iso, ct);
            if (!merge.Merged)
            {
                throw new InvalidOperationException(
                    $"merge do worktree da pipeline parcial — {merge.Conflicts.Count} conflito(s): "
                        + string.Join("; ", merge.Conflicts));
            }
        }

        var porcelain = await WorktreeService.StatusPorcelainAsync(workdir, ct);
        if (string.IsNullOrWhiteSpace(porcelain))
        {
            return null; // commit_skipped — nada a commitar
        }

        var subject = ConventionalSubject(state.Task);
        _ = await WorktreeService.GitOutputAsync(workdir, ct, "add", "-A");
        var commitError = await WorktreeService.GitErrorAsync(workdir, ct,
            "commit", "-m", subject);
        if (commitError is not null)
        {
            throw new InvalidOperationException($"git commit falhou: {commitError}");
        }
        var sha = (await WorktreeService.GitOutputAsync(
            workdir, ct, "rev-parse", "--short", "HEAD"))?.Trim();
        if (state.Push)
        {
            var pushError = await WorktreeService.GitErrorAsync(workdir, ct,
                "push", "-u", "origin", "HEAD");
            if (pushError is not null)
            {
                // Commit existe — o push falho vira erro da pipeline, mas o
                // sha fica registrado para o relatório não perder o trabalho.
                state.CommitSha = sha;
                throw new InvalidOperationException($"git push falhou: {pushError}");
            }
        }
        return sha;
    }

    /// <summary>Report-back: mensagem <c>user</c> <c>[dev_pipeline]</c> no
    /// chat pai + run filha para o agente apresentar o resultado (mesmo
    /// padrão do drain de steers queue).</summary>
    private async Task ReportAsync(State state, CancellationToken ct)
    {
        var report = BuildReport(state);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var boundaries = scope.ServiceProvider.GetRequiredService<ContextBoundaryService>();
        var chat = await db.Chats.Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == state.ParentChatId, ct);
        if (chat is null)
        {
            logger.LogWarning("Pipeline {Id}: chat pai {Chat} sumiu — relatório só no kv.",
                state.Id, state.ParentChatId);
            return;
        }
        var parentRun = await db.ChatRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == state.ParentRunId, ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var message = new ChatMessage
        {
            ChatId = chat.Id,
            Role = "user",
            Content = report,
            Position = chat.Messages.Count == 0
                ? 0
                : chat.Messages.Max(m => m.Position) + 1,
            Timestamp = now,
            ParentId = BranchService.ResolveLeaf(chat)?.Id,
        };
        chat.Messages.Add(message);
        chat.LeafMessageId = message.Id;
        chat.UpdatedAt = now;

        var path = BranchService.WalkPath(chat);
        var visible = await boundaries.FilterAsync(chat.Id, path, ct);
        var history = visible.Select(m => new ChatCompletionMessage(
            m.Role, m.Content, m.ToolCallId, m.ToolCallsJson,
            FileIds: ParseFileIds(m.FilesJson))).ToList();
        var request = parentRun is not null
            && JsonSerializer.Deserialize<ChatCompletionRequest>(
                parentRun.RequestJson, JsonOptions) is { } baseReq
                ? baseReq with { Messages = history }
                : new ChatCompletionRequest(parentRun?.Model ?? "default", history);
        var run = new ChatRun
        {
            ChatId = chat.Id,
            UserId = state.UserId,
            Model = request.Model,
            RequestJson = JsonSerializer.Serialize(request, JsonOptions),
            ParentRunId = state.ParentRunId,
            MetaJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["devPipeline"] = new { id = state.Id, phase = "report" },
            }, JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync(ct);
        scope.ServiceProvider.GetRequiredService<Runs.ChatRunDispatcher>()
            .Enqueue(run.Id);
    }

    /// <summary>Texto do relatório visto pelo agente pai e pelo usuário.</summary>
    private static string BuildReport(State state)
    {
        var icon = state.Status == "done" ? "✅" : "❌";
        var phases = string.Join(" → ", state.Runs.Keys.Concat(
            state.Status == "done" ? new[] { "validate", "commit" } : Array.Empty<string>()));
        var commit = state.CommitSha is { } sha
            ? $"commit {sha}"
            : state.Status == "done" ? "commit skipped (nada a commitar)" : "sem commit";
        return $"[dev_pipeline {state.Id}] {icon} Pipeline {state.Status}: \"{state.Task}\"\n"
            + $"Fases: {phases}\n"
            + $"Validação: {state.ValidationOutput ?? "(não executada)"}\n"
            + $"Entrega: {commit}"
            + (state.Push ? " + push" : string.Empty)
            + (state.Error is { } e ? $"\nErro: {e}" : string.Empty)
            + $"\nChat da pipeline: /c/{state.ChildChatId}\n"
            + "Apresente este resultado ao usuário de forma resumida.";
    }

    /// <summary>Marca a pipeline failed e dispara o relatório.</summary>
    private async Task MarkFailedAsync(State state, string error)
    {
        state.Status = "failed";
        state.Error = error;
        try
        {
            await SaveAsync(null, state, CancellationToken.None);
            await ReportAsync(state, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pipeline {Id}: falha ao reportar.", state.Id);
        }
    }

    /// <summary>"feat: {task}" Conventional — primeira linha, ≤72 chars.</summary>
    internal static string ConventionalSubject(string task)
    {
        var first = task.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "pipeline changes";
        var subject = $"feat: {first}";
        return subject.Length <= 72 ? subject : subject[..72];
    }

    /// <summary>Persiste o estado no kv (scope novo quando omitido).</summary>
    private async Task SaveAsync(IServiceScope? scope, State state, CancellationToken ct)
    {
        var own = scope is null;
        scope ??= scopes.CreateScope();
        try
        {
            var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
            state.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await config.SetAsync(
                Key(state.Id),
                JsonSerializer.Serialize(state, JsonOptions), ct);
        }
        finally
        {
            if (own)
            {
                scope.Dispose();
            }
        }
    }

    private static IReadOnlyList<string>? ParseFileIds(string? filesJson)
    {
        if (string.IsNullOrWhiteSpace(filesJson))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<List<string>>(filesJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Tail(string text, int max) =>
        text.Length <= max ? text : text[^max..];

    /// <summary>Resultado interno da fase validate.</summary>
    private sealed record ValidationOutcome(
        bool Passed, string Summary, string Output);
}
