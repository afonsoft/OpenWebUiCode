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
        /// <summary>Runs por fase (plan/implement/test/verify/fixN/report) — runId.</summary>
        public Dictionary<string, string> Runs { get; set; } = new();
        public string? ValidationOutput { get; set; }
        public string? CommitSha { get; set; }
        /// <summary>Plano emitido pela fase <c>plan</c> (RF-006).</summary>
        public string? Plan { get; set; }
        /// <summary>Modelo por fase lógica (RF-007): worker_model→plan/
        /// implement/test/verify; fix_model→fix.</summary>
        public Dictionary<string, string> Models { get; init; } = new();
        /// <summary>Resultado estruturado por fase (RF-010).</summary>
        public Dictionary<string, PhaseResult> PhaseResults { get; init; } = new();
        /// <summary>Substituições por stall já feitas na pipeline (RF-008).</summary>
        public int StallRetries { get; set; }
        /// <summary>Teto de substituições por stall (default 1).</summary>
        public int MaxStallRetries { get; init; } = 1;
        /// <summary>Fase <c>plan</c> habilitada (default true).</summary>
        public bool EnablePlan { get; init; } = true;
        public long CreatedAt { get; init; }
        public long UpdatedAt { get; set; }
    }

    /// <summary>Bloco <c>&lt;pipeline_result&gt;</c> estruturado emitido por uma fase LLM.</summary>
    /// <param name="Files">Arquivos criados/alterados declarados pela fase.</param>
    /// <param name="Summary">Resumo da fase (fix: formato handoff).</param>
    /// <param name="Verdict">"pass"|"issues" — só a fase verify emite.</param>
    /// <param name="Issues">Problemas apontados pela verify.</param>
    /// <param name="Plan">Plano decomposto — só a fase plan emite.</param>
    public sealed record PhaseResult(
        List<string>? Files, string? Summary, string? Verdict,
        List<string>? Issues, string? Plan);

    private static string Key(string id) => $"devpipeline:{id}";

    /// <summary>Cria o kv + chat filho (build, binding herdado) e devolve o estado inicial.</summary>
    /// <param name="enablePlan">Fase <c>plan</c> antes de implement (RF-006).</param>
    /// <param name="workerModel">Modelo das fases plan/implement/test/verify (RF-007).</param>
    /// <param name="fixModel">Modelo da fase fix — tipicamente mais forte (RF-007).</param>
    public async Task<State> StartAsync(
        BuiltinToolContext context, string task, string? extraContext,
        bool push, int maxFixAttempts, CancellationToken ct,
        bool enablePlan = true, string? workerModel = null,
        string? fixModel = null)
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
            EnablePlan = enablePlan,
            MaxStallRetries = int.TryParse(
                cfg["Agent:DevPipeline:MaxStallRetries"], out var msr)
                    ? Math.Max(0, msr) : 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repos = scope.ServiceProvider.GetRequiredService<WorkspaceRepoService>();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();

        // RF-007: modelo por fase. Sem override explícito e a run pai em
        // fusion:{L}>{S}, os papéis herdam o par — worker (implement/
        // test/verify) ← sidekick (a kv já vem como chain:{S,…,L} com os
        // leaders como fallback final) e fixer ← leader, o split
        // planner/worker do orquestrador. Refs chain:/fusion: também são
        // aceitos nos args — o executor resolve a cadeia na run filha.
        var worker = workerModel;
        var fixer = fixModel;
        string? fusionLeader = null;
        if ((worker is null || fixer is null) && context.RunId is { } parentRun)
        {
            fusionLeader = await config.GetAsync(
                $"r:{parentRun}:fusion.leader", (string?)null, ct);
            var fusionSidekick = await config.GetAsync(
                $"r:{parentRun}:fusion.sidekick", (string?)null, ct);
            fixer ??= fusionLeader;
            worker ??= fusionSidekick;
        }
        if (!string.IsNullOrWhiteSpace(worker))
        {
            foreach (var p in new[] { "plan", "implement", "test", "verify" })
            {
                state.Models[p] = worker;
            }
        }
        if (!string.IsNullOrWhiteSpace(fixer))
        {
            state.Models["fix"] = fixer;
        }
        // plan é trabalho de planner: sem worker_model explícito, o leader
        // do pai roteia a fase — não o sidekick dos workers.
        if (workerModel is null && !string.IsNullOrWhiteSpace(fusionLeader))
        {
            state.Models["plan"] = fusionLeader;
        }

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
        // plan? → implement → test: runs LLM encadeadas no chat filho. Resume:
        // fase com run completed salta; run ainda ativa é aguardada.
        foreach (var (phase, prompt) in LlmPhases(state))
        {
            var existing = state.Runs.TryGetValue(phase, out var e) ? e : null;
            if (existing is not null
                && await RunStatusAsync(existing, ct)
                    is { Status: ChatRunStatus.Completed })
            {
                continue;
            }
            if (!await RunLlmPhaseAsync(state, phase, prompt, ct, existing))
            {
                return;
            }
        }

        // validate: determinístico no workdir efetivo do filho. fix→validate
        // até MaxFixAttempts; verify (LLM read-only, RF-009) corre quando os
        // testes passam — issues dela também contam como falha e alimentam
        // o fix.
        state.Phase = "validate";
        await SaveAsync(null, state, ct);
        var outcome = await ValidateAsync(state, ct);
        var verifyIssues = false;
        for (;;)
        {
            if (outcome is { Passed: false })
            {
                if (state.FixAttempts >= state.MaxFixAttempts)
                {
                    break;
                }
                state.FixAttempts++;
                if (!await RunFixAsync(state, outcome.Output, null, ct))
                {
                    return;
                }
                outcome = await ValidateAsync(state, ct);
                continue;
            }

            var verifyClean = await RunVerifyAsync(state, ct);
            if (verifyClean is null)
            {
                return; // run de verify falhou — pipeline já marcada failed
            }
            verifyIssues = !verifyClean.Value;
            if (!verifyIssues || state.FixAttempts >= state.MaxFixAttempts)
            {
                break;
            }
            state.FixAttempts++;
            var issues = state.PhaseResults.TryGetValue("verify", out var vr)
                ? string.Join("; ", vr.Issues ?? [])
                : null;
            if (!await RunFixAsync(state, null, issues, ct))
            {
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
        if (verifyIssues)
        {
            var issues = state.PhaseResults.TryGetValue("verify", out var vr)
                ? string.Join("; ", vr.Issues ?? [])
                : "sem detalhe";
            await MarkFailedAsync(state,
                "verificação read-only apontou issues sem tentativas de "
                    + $"correção restantes: {Tail(issues, 500)}");
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

    /// <summary>Runs LLM da pipeline com os prompts de fase (RF-006/010:
    /// plan opcional primeiro; toda fase fecha com
    /// <c>&lt;pipeline_result&gt;</c>).</summary>
    internal static IEnumerable<(string Phase, string Prompt)> LlmPhases(State state)
    {
        if (state.EnablePlan)
        {
            yield return ("plan",
                "[dev_pipeline — PLANO]\n"
                    + $"Tarefa: {state.Task}\n"
                    + (state.ExtraContext is { } x ? $"Contexto: {x}\n" : string.Empty)
                    + "Decomponha a tarefa em itens ordenados e independentes — "
                    + "diga quais arquivos cada item deve tocar e o que "
                    + "'pronto' significa. NÃO implemente nada — só planeje. "
                    + "Termine com <pipeline_result>{\"plan\":\"itens "
                    + "numerados\",\"summary\":\"resumo em 1 linha\"}"
                    + "</pipeline_result>.");
        }
        yield return ("implement",
            "[dev_pipeline — IMPLEMENTAÇÃO]\n"
                + $"Tarefa: {state.Task}\n"
                + (state.ExtraContext is { } xc ? $"Contexto: {xc}\n" : string.Empty)
                + (state.Plan is { } p ? $"Plano aprovado:\n{p}\n" : string.Empty)
                + "Implemente a tarefa no workdir atual. NÃO escreva testes "
                + "ainda (fase seguinte) e NÃO faça commit — testes, "
                + "validação e commit são orquestrados depois. Termine com "
                + "<pipeline_result>{\"files\":[\"paths criados/alterados\"],"
                + "\"summary\":\"resumo\"}</pipeline_result>.");
        yield return ("test",
            "[dev_pipeline — TESTES]\n"
                + "A implementação anterior alterou o workdir. Escreva ou "
                + "ajuste os testes do que foi implementado seguindo as "
                + "convenções do projeto. NÃO faça commit — validação e "
                + "commit são orquestrados. Termine com "
                + "<pipeline_result>{\"files\":[\"paths de teste\"],"
                + "\"summary\":\"resumo\"}</pipeline_result>.");
    }

    /// <summary>Enfileira + aguarda + parseia uma fase LLM; marca failed e
    /// devolve false quando a run não completa. <paramref name="existing"/>
    /// reutiliza run ativa no resume.</summary>
    private async Task<bool> RunLlmPhaseAsync(
        State state, string phase, string prompt, CancellationToken ct,
        string? existing = null, string? runKey = null)
    {
        var runId = existing ?? await EnqueuePhaseRunAsync(
            state, phase, prompt, ct);
        state.Runs[runKey ?? phase] = runId;
        state.Phase = phase;
        await SaveAsync(null, state, ct);
        var (status, error) = await WaitRunAsync(
            runId, state, phase, prompt, ct);
        if (status is ChatRunStatus.Completed)
        {
            await ParsePhaseResultAsync(state, phase, ct);
            return true;
        }
        await MarkFailedAsync(state,
            $"fase '{phase}' terminou como '{status}'"
                + (error is { } e ? $": {e}" : string.Empty));
        return false;
    }

    /// <summary>Run de correção (RF-009/010): alimentada pela saída da
    /// validação ou pelas issues da verify; proíbe adulterar testes e exige
    /// handoff com método de verificação.</summary>
    private async Task<bool> RunFixAsync(
        State state, string? validationOutput, string? verifyIssues,
        CancellationToken ct)
    {
        var fixPrompt =
            "[dev_pipeline — CORREÇÃO]\n"
            + (verifyIssues is { } vi
                ? $"A verificação read-only encontrou problemas:\n{Tail(vi, 4000)}\n"
                : $"A validação falhou:\n{Tail(validationOutput ?? "", 4000)}\n")
            + "Corrija a causa raiz no workdir. NÃO faça commit — o commit "
            + "é orquestrado. NÃO altere nem remova testes para fazê-los "
            + "passar — ajuste o código; se um teste estiver de fato errado, "
            + "justifique no resultado.\n"
            + "Termine com <pipeline_result>{\"files\":[...],\"summary\":"
            + "\"Observação; Causa raiz; Correção; Método de verificação "
            + "(comando e saída real)\"}</pipeline_result>.";
        return await RunLlmPhaseAsync(
            state, "fix", fixPrompt, ct,
            runKey: $"fix{state.FixAttempts}");
    }

    /// <summary>Run de verificação read-only (RF-009): lê o diff e emite
    /// verdict. Devolve null quando a run falhou (pipeline já failed),
    /// true=limpa, false=issues.</summary>
    private async Task<bool?> RunVerifyAsync(State state, CancellationToken ct)
    {
        var workdir = await EffectiveWorkdirAsync(state, ct);
        var diff = await WorktreeService.DiffAsync(workdir, ct);
        var prompt =
            "[dev_pipeline — VERIFICAÇÃO (somente leitura)]\n"
            + "Leia o diff abaixo e, se precisar, os arquivos do workdir. "
            + "Confira: (a) o código implementa de fato o pedido; (b) os "
            + "testes cobrem o implementado sem terem sido adulterados para "
            + "passar; (c) não há restos (TODO, debug, arquivos órfãos). "
            + "NÃO edite, crie ou apague nada — somente leitura.\n"
            + $"Tarefa: {state.Task}\n"
            + $"Diff:\n{Tail(diff ?? "(sem diff)", 6000)}\n"
            + "Termine com <pipeline_result>{\"verdict\":\"pass\"|\"issues\","
            + "\"issues\":[\"descrições curtas\"],\"summary\":\"resumo\"}"
            + "</pipeline_result>.";
        if (!await RunLlmPhaseAsync(state, "verify", prompt, ct))
        {
            return null;
        }
        return state.PhaseResults.TryGetValue("verify", out var r)
            && r.Verdict != "issues";
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
        // RF-007: modelo dedicado da fase (worker/fix) sobrescreve o herdado.
        if (state.Models.TryGetValue(phase, out var phaseModel)
            && !string.IsNullOrWhiteSpace(phaseModel))
        {
            request = request with { Model = phaseModel };
        }
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
    /// (default 1200s). RF-008: <c>Running</c> com <c>HeartbeatAt</c> velho
    /// (&gt;<c>Agent:DevPipeline:StallSec</c>, default 240s, por 2 polls) é
    /// considerada travada → substituída com prompt de retomada, até
    /// <see cref="State.MaxStallRetries"/> vezes; "timeout"/"stalled" nas
    /// bordas.</summary>
    private async Task<(string Status, string? Error)> WaitRunAsync(
        string runId, State state, string phase, string prompt,
        CancellationToken ct)
    {
        var timeout = int.TryParse(
            cfg["Agent:DevPipeline:PhaseTimeoutSec"], out var s)
                ? Math.Max(30, s) : 1200;
        var stallSec = int.TryParse(
            cfg["Agent:DevPipeline:StallSec"], out var ss)
                ? Math.Max(30, ss) : 240;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
        var stallTicks = 0;
        for (;;)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return ("timeout", $"run {runId} excedeu {timeout}s");
            }
            await Task.Delay(PollInterval, ct);
            var (status, error, heartbeatAt) = await RunStatusAsync(runId, ct);
            if (status is not (null or ChatRunStatus.Queued
                or ChatRunStatus.Running or ChatRunStatus.Paused))
            {
                return (status, error);
            }
            var stale = status == ChatRunStatus.Running
                && heartbeatAt is { } hb
                && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - hb > stallSec;
            stallTicks = stale ? stallTicks + 1 : 0;
            if (stallTicks < 2)
            {
                continue;
            }
            if (state.StallRetries >= state.MaxStallRetries)
            {
                return ("stalled",
                    $"run {runId} sem heartbeat há >{stallSec}s e sem "
                        + "retries restantes");
            }
            state.StallRetries++;
            runId = await ReplaceStalledRunAsync(
                state, phase, runId, prompt, ct);
            stallTicks = 0;
        }
    }

    /// <summary>Status + heartbeat de uma run (null quando inexistente).</summary>
    private async Task<(string? Status, string? Error, long? HeartbeatAt)> RunStatusAsync(
        string runId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ChatRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.Status, r.Error, r.HeartbeatAt })
            .FirstOrDefaultAsync(ct);
        return run is null ? (null, null, null) : (run.Status, run.Error, run.HeartbeatAt);
    }

    /// <summary>Para a run travada e dispara substituta com prompt de
    /// retomada (RF-008) — registrada como <c>{phase}-stall{n}</c>.</summary>
    private async Task<string> ReplaceStalledRunAsync(
        State state, string phase, string runId, string prompt,
        CancellationToken ct)
    {
        logger.LogWarning(
            "Pipeline {Id}: run {RunId} da fase {Phase} travada — substituindo.",
            state.Id, runId, phase);
        using var scope = scopes.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<Runs.ChatRunDispatcher>()
            .TryStop(runId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stalled = await db.ChatRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (stalled is { Status: ChatRunStatus.Running or ChatRunStatus.Queued })
        {
            stalled.Status = ChatRunStatus.Stopped;
            stalled.Error = "dev_pipeline: sem heartbeat — substituída.";
            stalled.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync(ct);
        }
        var resumePrompt = prompt + "\n\n[dev_pipeline — RETOMADA] A run "
            + "anterior desta fase travou e foi substituída; o workdir pode "
            + "conter trabalho parcial — continue a partir do estado atual "
            + "sem recomeçar do zero.";
        var newRunId = await EnqueuePhaseRunAsync(
            state, phase, resumePrompt, ct);
        state.Runs[$"{phase}-stall{state.StallRetries}"] = newRunId;
        await SaveAsync(null, state, ct);
        return newRunId;
    }

    /// <summary>Extrai o bloco <c>&lt;pipeline_result&gt;</c> da última
    /// resposta assistant do chat filho para a fase (RF-010) — a fase plan
    /// ainda alimenta <see cref="State.Plan"/>.</summary>
    private async Task ParsePhaseResultAsync(
        State state, string phase, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lastAssistant = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == state.ChildChatId && m.Role == "assistant")
            .OrderByDescending(m => m.Timestamp)
            .Select(m => m.Content)
            .FirstOrDefaultAsync(ct);
        var parsed = ParsePhaseResult(lastAssistant);
        if (parsed is null)
        {
            return;
        }
        state.PhaseResults[phase] = parsed;
        if (phase == "plan" && parsed.Plan is { Length: > 0 } plan)
        {
            state.Plan = plan;
        }
        await SaveAsync(null, state, ct);
    }

    /// <summary>Parser do bloco <c>&lt;pipeline_result&gt;{…}&lt;/pipeline_result&gt;</c>
    /// — ausente/malformado → null (nunca quebra a fase).</summary>
    internal static PhaseResult? ParsePhaseResult(string? text)
    {
        const string open = "<pipeline_result>";
        const string close = "</pipeline_result>";
        if (text is null)
        {
            return null;
        }
        var start = text.IndexOf(open, StringComparison.Ordinal);
        var end = text.LastIndexOf(close, StringComparison.Ordinal);
        if (start < 0 || end <= start)
        {
            return null;
        }
        try
        {
            var root = JsonDocument.Parse(
                text[(start + open.Length)..end].Trim()).RootElement;
            static List<string>? Strings(JsonElement el, string name) =>
                el.TryGetProperty(name, out var arr)
                    && arr.ValueKind == JsonValueKind.Array
                        ? arr.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString()!).ToList()
                        : null;
            static string? Str(JsonElement el, string name) =>
                el.TryGetProperty(name, out var s)
                    && s.ValueKind == JsonValueKind.String
                        ? s.GetString()
                        : null;
            return new PhaseResult(
                Strings(root, "files"), Str(root, "summary"),
                Str(root, "verdict"), Strings(root, "issues"),
                Str(root, "plan"));
        }
        catch (JsonException)
        {
            return null;
        }
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
            + (state.Plan is { } p ? $"Plano: {Tail(p, 400)}\n" : string.Empty)
            + $"Validação: {state.ValidationOutput ?? "(não executada)"}\n"
            + (state.PhaseResults.Count > 0
                ? "Resultados: " + string.Join("; ", state.PhaseResults.Select(
                    kv => $"{kv.Key}={kv.Value.Verdict ?? kv.Value.Summary ?? "ok"}")) + "\n"
                : string.Empty)
            + (state.StallRetries > 0
                ? $"Stalls recuperados: {state.StallRetries}\n" : string.Empty)
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
