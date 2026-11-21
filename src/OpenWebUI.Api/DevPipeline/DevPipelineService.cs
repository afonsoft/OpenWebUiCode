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
        public bool Push { get; set; }
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
        /// <summary>Substituições por stall por runKey (SPEC-20261123) —
        /// uma run travada não consome o budget das irmãs do fan-out.</summary>
        public Dictionary<string, int> StallRetriesByKey { get; init; } = new();
        /// <summary>Teto de substituições por stall por runKey (default 1).</summary>
        public int MaxStallRetries { get; init; } = 1;
        /// <summary>Fase <c>plan</c> habilitada (default true).</summary>
        public bool EnablePlan { get; init; } = true;
        /// <summary>Fan-out: implement paralelo por item independente do
        /// plano (SPEC-20261123; default true, exige binding de repo).</summary>
        public bool EnableFanout { get; init; } = true;
        /// <summary>Cap de workers paralelos do fan-out (default 4).</summary>
        public int MaxFanout { get; set; } = 4;
        /// <summary>Item → chat irmão do fan-out (<c>implement:{id}</c>
        /// → chatId) — mapping p/ wait-all, merge e links do relatório.</summary>
        public Dictionary<string, string> FanoutItemChats { get; init; } = new();
        /// <summary>Passos declarativos do <c>run_workflow</c>
        /// (SPEC-20261124) — quando presentes, o drive itera esta lista
        /// em vez das fases fixas plan→implement→test.</summary>
        public List<WorkflowStep>? Steps { get; init; }
        /// <summary>Índice do próximo step do workflow (resume por kv).</summary>
        public int StepIndex { get; set; }
        /// <summary>Respostas registradas de steps <c>gate</c> via
        /// <c>dev_pipeline op=answer</c> (SPEC-20261124-workflow-v2).</summary>
        public Dictionary<string, string> GateAnswers { get; init; } = new();
        /// <summary>Key do gate que estacionou o workflow — persistida
        /// para status/resume saberem o que está pendente.</summary>
        public string? AwaitingGate { get; set; }
        /// <summary>Steps pulados por <c>when</c> não satisfeito —
        /// auditoria no relatório final.</summary>
        public List<string> SkippedSteps { get; init; } = new();
        public long CreatedAt { get; init; }
        public long UpdatedAt { get; set; }
    }

    /// <summary>Passo declarativo do <c>run_workflow</c> (SPEC-20261124).
    /// <c>Kind</c>: <c>llm</c> | <c>parallel</c> | <c>tool</c> |
    /// <c>validate</c> | <c>commit</c> | <c>gate</c> (v2).</summary>
    public sealed record WorkflowStep
    {
        public required string Kind { get; init; }
        public required string Key { get; init; }
        /// <summary>Prompt do step <c>llm</c> — aceita placeholders
        /// <c>{task}</c>, <c>{plan}</c>, <c>{results:key}</c>.</summary>
        public string? Prompt { get; init; }
        /// <summary>Itens do step <c>parallel</c> (workers em ondas de
        /// <see cref="State.MaxFanout"/>).</summary>
        public List<WorkflowItem>? Items { get; init; }
        /// <summary>Nome da builtin do step <c>tool</c> (ex.
        /// <c>builtin:agent_validate</c> ou <c>agent_validate</c>).</summary>
        public string? Tool { get; init; }
        /// <summary>Args JSON do step <c>tool</c> (string ou objeto).</summary>
        public string? Args { get; init; }
        /// <summary>Step <c>commit</c>: push além do commit.</summary>
        public bool? Push { get; init; }
        /// <summary>Condição (v2): <c>passed:key</c>/<c>failed:key</c>
        /// sobre o veredito de um step anterior — não satisfeita → skip.</summary>
        public string? When { get; init; }
        /// <summary><c>true</c> → falha do step registra verdict
        /// <c>issues</c> e o workflow continua (v2).</summary>
        public bool? Optional { get; init; }
        /// <summary>Budget de wallclock do step em segundos (v2) —
        /// estoura → step falha com issue <c>timeout</c>; a run filha
        /// órfã é varrida pelo watchdog global.</summary>
        public int? TimeoutSec { get; init; }
    }

    /// <summary>Item de um step <c>parallel</c> do workflow — um worker
    /// (chat irmão + worktree isolado) por item.</summary>
    public sealed record WorkflowItem(
        string Id, string Prompt, List<string>? Files = null);

    /// <summary>Workflow nomeado persistido (SPEC-20261124-named-workflows):
    /// steps + defaults reutilizáveis — <c>run_workflow workflow=nome</c>
    /// executa sem redeclarar os steps.</summary>
    public sealed record NamedWorkflow(
        List<WorkflowStep> Steps, bool? Push, string? WorkerModel,
        string? FixModel, int? MaxParallel);

    /// <summary>Bloco <c>&lt;pipeline_result&gt;</c> estruturado emitido por uma fase LLM.</summary>
    /// <param name="Files">Arquivos criados/alterados declarados pela fase.</param>
    /// <param name="Summary">Resumo da fase (fix: formato handoff).</param>
    /// <param name="Verdict">"pass"|"issues" — só a fase verify emite.</param>
    /// <param name="Issues">Problemas apontados pela verify.</param>
    /// <param name="Plan">Plano decomposto — só a fase plan emite.</param>
    /// <param name="Items">Itens do plano (SPEC-20261123) — só a fase
    /// plan emite; <c>independent</c> habilita implement paralelo.</param>
    public sealed record PhaseResult(
        List<string>? Files, string? Summary, string? Verdict,
        List<string>? Issues, string? Plan, List<PlanItem>? Items);

    /// <summary>Item decomposto do plano — candidato a worker paralelo.</summary>
    public sealed record PlanItem(
        string? Id, string? Title, List<string>? Files, bool Independent);

    private static string Key(string id) => $"devpipeline:{id}";

    /// <summary>Cria o kv + chat filho (build, binding herdado) e devolve o estado inicial.</summary>
    /// <param name="enablePlan">Fase <c>plan</c> antes de implement (RF-006).</param>
    /// <param name="workerModel">Modelo das fases plan/implement/test/verify (RF-007).</param>
    /// <param name="fixModel">Modelo da fase fix — tipicamente mais forte (RF-007).</param>
    /// <param name="enableFanout">Implement paralelo por item independente
    /// do plano (SPEC-20261123; exige binding de repo no filho).</param>
    public async Task<State> StartAsync(
        BuiltinToolContext context, string task, string? extraContext,
        bool push, int maxFixAttempts, CancellationToken ct,
        bool enablePlan = true, string? workerModel = null,
        string? fixModel = null, bool enableFanout = true,
        List<WorkflowStep>? steps = null, int? maxFanout = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var state = new State
        {
            Steps = steps is { Count: > 0 } ? steps : null,
            Id = $"dp-{Guid.NewGuid():N}"[..12],
            UserId = context.UserId,
            ParentChatId = context.ChatId ?? string.Empty,
            ParentRunId = context.RunId ?? string.Empty,
            Task = task,
            ExtraContext = extraContext,
            Push = push,
            MaxFixAttempts = Math.Max(0, maxFixAttempts),
            EnablePlan = enablePlan,
            EnableFanout = enableFanout,
            MaxFanout = maxFanout is > 0
                ? Math.Clamp(maxFanout.Value, 1, 8)
                : int.TryParse(
                    cfg["Agent:DevPipeline:MaxFanout"], out var mf)
                        ? Math.Clamp(mf, 1, 8) : 4,
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
        // SPEC-20261124: steps llm/parallel do workflow roteiam pelo
        // worker model — EnqueuePhaseRunAsync resolve state.Models[step.Key].
        if (!string.IsNullOrWhiteSpace(worker) && steps is { } declared)
        {
            foreach (var s in declared.Where(
                s => s.Kind is "llm" or "parallel"))
            {
                state.Models[s.Key] = worker;
            }
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

    /// <summary>Registra a resposta de um gate estacionado
    /// (SPEC-20261124-workflow-v2): devolve a key respondida, <c>null</c>
    /// se a pipeline não existe ou <c>""</c> se não há gate pendente.</summary>
    public async Task<string?> AnswerGateAsync(
        string pipelineId, string verdict, string? answer,
        CancellationToken ct)
    {
        var state = await GetAsync(pipelineId, ct);
        if (state is null)
        {
            return null;
        }
        if (state.AwaitingGate is not { } gate)
        {
            return "";
        }
        state.GateAnswers[gate] = answer is { } a
            ? $"{verdict}: {a}"
            : verdict;
        state.AwaitingGate = null;
        await SaveAsync(null, state, ct);
        return gate;
    }

    // ---------- workflows nomeados (SPEC-20261124-named-workflows) ----------

    private static string WfKey(string userId, string name) =>
        $"wf:{userId}:{name}";

    /// <summary>Nome válido de workflow nomeado — vira sufixo de chave kv;
    /// alfanumérico + <c>-_.</c>, até 64 chars.</summary>
    internal static bool ValidWorkflowName(string? name) =>
        name is { Length: > 0 and <= 64 }
            && name.All(c => char.IsLetterOrDigit(c)
                || c is '-' or '_' or '.');

    /// <summary>Persiste um workflow nomeado do usuário (kv
    /// <c>wf:{user}:{name}</c>).</summary>
    public async Task SaveWorkflowAsync(
        string userId, string name, NamedWorkflow wf,
        CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ConfigService>()
            .SetAsync(WfKey(userId, name), wf, ct);
    }

    /// <summary>Carrega um workflow nomeado — <c>null</c> se não existe
    /// (ou foi removido).</summary>
    public async Task<NamedWorkflow?> GetWorkflowAsync(
        string userId, string name, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ConfigService>()
            .GetAsync<NamedWorkflow?>(WfKey(userId, name), null, ct);
    }

    /// <summary>Nomes dos workflows do usuário — prefix scan no kv,
    /// excluindo entradas removidas (null).</summary>
    public async Task<IReadOnlyList<string>> ListWorkflowsAsync(
        string userId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var prefix = WfKey(userId, "");
        var rows = await scope.ServiceProvider
            .GetRequiredService<ConfigService>()
            .ListByPrefixAsync<NamedWorkflow>(prefix, ct);
        return rows.Where(r => r.Value is not null)
            .Select(r => r.Key[prefix.Length..]).Order().ToList();
    }

    /// <summary>Remove um workflow nomeado — kv é append-only; grava
    /// <c>null</c> (GetAsync devolve como ausente).</summary>
    public Task DeleteWorkflowAsync(
        string userId, string name, CancellationToken ct) =>
        SaveWorkflowAsync(userId, name, null!, ct);

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
        // SPEC-20261124: workflow declarativo — itera State.Steps em vez
        // das fases fixas (resume por StepIndex persistido no kv).
        if (state.Steps is { Count: > 0 })
        {
            await RunStepsAsync(state, ct);
            return;
        }

        // plan? → implement → test: runs LLM encadeadas no chat filho. Resume:
        // fase com run completed salta; run ainda ativa é aguardada.
        // SPEC-20261123: implement vira fan-out quando o plano decompôs
        // ≥2 itens independentes e o filho tem binding de repo (worktrees).
        foreach (var (phase, prompt) in LlmPhases(state))
        {
            var existing = state.Runs.TryGetValue(phase, out var e) ? e : null;
            if (existing is not null
                && await RunStatusAsync(existing, ct)
                    is { Status: ChatRunStatus.Completed })
            {
                continue;
            }
            if (phase == "implement"
                && FanoutPlan(state) is { Fanned.Count: >= 2 } fanout
                && await HasRepoBindingAsync(state, ct))
            {
                if (!await RunFanoutImplementAsync(
                        state, fanout.Fanned, fanout.Remainder, ct))
                {
                    return;
                }
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
                    + "Decomponha a tarefa em itens ordenados — diga quais "
                    + "arquivos cada item deve tocar e o que 'pronto' "
                    + "significa. Marque independent=true SÓ quando o item "
                    + "não divide arquivo nem depende de outro item (eles "
                    + "rodam em paralelo em workspaces isolados). NÃO "
                    + "implemente nada — só planeje. Termine com "
                    + "<pipeline_result>{\"plan\":\"itens numerados\","
                    + "\"items\":[{\"id\":\"i1\",\"title\":\"título\","
                    + "\"files\":[\"paths\"],\"independent\":true}],"
                    + "\"summary\":\"resumo em 1 linha\"}</pipeline_result>.");
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
        string? existing = null, string? runKey = null,
        string? parentRunId = null)
    {
        var runId = existing ?? await EnqueuePhaseRunAsync(
            state, phase, prompt, ct, parentRunId: parentRunId);
        state.Runs[runKey ?? phase] = runId;
        state.Phase = phase;
        await SaveAsync(null, state, ct);
        var (status, error) = await WaitRunAsync(
            runId, state, phase, prompt, ct, runKey ?? phase);
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

    /// <summary>Seleção do fan-out (SPEC-20261123): itens
    /// <c>independent</c> do plano até <see cref="State.MaxFanout"/> —
    /// ≥2 habilita. <c>Remainder</c> = dependentes + overflow além do cap.</summary>
    internal static (List<PlanItem> Fanned, List<PlanItem> Remainder) FanoutPlan(
        State state)
    {
        var empty = (new List<PlanItem>(), new List<PlanItem>());
        if (!state.EnableFanout || !state.EnablePlan
            || !state.PhaseResults.TryGetValue("plan", out var pr)
            || pr.Items is not { } items)
        {
            return empty;
        }
        var independent = items.Where(i => i.Independent).ToList();
        if (independent.Count < 2)
        {
            return empty;
        }
        var fanned = independent.Take(state.MaxFanout).ToList();
        var rest = items.Where(i => !i.Independent)
            .Concat(independent.Skip(state.MaxFanout)).ToList();
        return (fanned, rest);
    }

    /// <summary>O filho tem repo vinculado — pré-condição do fan-out
    /// (scratch não tem git → sem worktree isolado para os workers).</summary>
    private async Task<bool> HasRepoBindingAsync(State state, CancellationToken ct)
    {
        if (state.ChildChatId is null)
        {
            return false;
        }
        using var scope = scopes.CreateScope();
        var repos = scope.ServiceProvider.GetRequiredService<WorkspaceRepoService>();
        return await repos.GetChatBindingAsync(state.ChildChatId, ct) is not null;
    }

    /// <summary>Fan-out do implement (SPEC-20261123): um chat irmão +
    /// <summary>Orquestração declarativa (SPEC-20261124): itera
    /// <see cref="State.Steps"/> a partir de <see cref="State.StepIndex"/>
    /// — cada step bem-sucedido avança o índice e persiste, então resume
    /// retoma do ponto. Sem auto-commit: <c>kind:commit</c> é explícito.</summary>
    private async Task RunStepsAsync(State state, CancellationToken ct)
    {
        for (var i = state.StepIndex; i < state.Steps!.Count; i++)
        {
            var step = state.Steps[i];
            state.Phase = step.Key;
            if (!EvaluateWhen(step.When, state, out var whenError))
            {
                if (whenError is not null)
                {
                    await MarkFailedAsync(state, $"workflow: {whenError}");
                    return;
                }
                state.SkippedSteps.Add($"{step.Key}({step.When})");
                state.StepIndex = i + 1;
                await SaveAsync(null, state, ct);
                continue;
            }
            var anchor = AnchorRunId(state, i);
            bool ok;
            using (var budget = step.TimeoutSec is { } secs
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null)
            {
                var stepCt = ct;
                if (budget is not null)
                {
                    budget.CancelAfter(TimeSpan.FromSeconds(secs));
                    stepCt = budget.Token;
                }
                try
                {
                    ok = step.Kind switch
                    {
                        "llm" => await RunLlmPhaseAsync(
                            state, step.Key,
                            Interpolate(step.Prompt ?? state.Task, state)
                                + ResultTrailer, stepCt, runKey: step.Key,
                            parentRunId: anchor),
                        "parallel" => await RunParallelStepAsync(
                            state, step, anchor, stepCt),
                        "tool" => await RunToolStepAsync(state, step, stepCt),
                        "validate" => await RunValidateStepAsync(
                            state, step, stepCt),
                        "commit" => await RunCommitStepAsync(
                            state, step, stepCt),
                        "gate" => await GateStepAsync(state, step, stepCt),
                        _ => await IssueAsync(state, step,
                            $"kind desconhecido '{step.Kind}' "
                                + $"no step '{step.Key}'"),
                    };
                }
                catch (OperationCanceledException)
                    when (budget?.IsCancellationRequested == true
                        && !ct.IsCancellationRequested)
                {
                    // Só o budget do step estourou — a run filha órfã é
                    // varrida pelo watchdog global (SPEC-20261124-run-watchdog).
                    ok = await IssueAsync(state, step,
                        $"timeout após {secs}s");
                }
            }
            if (!ok)
            {
                // Runner LLM já marcou failed; gate parqueou AwaitingGate —
                // ambos retornam sem nova marcação.
                if (state.Status == "failed" || state.AwaitingGate is not null)
                {
                    return;
                }
                if (step.Optional != true)
                {
                    var issue = state.PhaseResults.GetValueOrDefault(step.Key)
                        ?.Summary ?? $"step '{step.Key}' falhou";
                    await MarkFailedAsync(state, $"workflow: {issue}");
                    return;
                }
                // optional: verdict 'issues' já registrado — o workflow segue.
            }
            state.StepIndex = i + 1;
            await SaveAsync(null, state, ct);
        }
        state.Status = "done";
        await SaveAsync(null, state, ct);
        await ReportAsync(state, ct);
    }

    private const string ResultTrailer =
        "\nTermine com <pipeline_result>{\"files\":[\"paths\"],"
            + "\"summary\":\"resumo\"}</pipeline_result>.";

    /// <summary>Run âncora do step: a última run concluída de step
    /// anterior no chat filho — a run seguinte herda o worktree isolado
    /// dela, então as mudanças dos steps se acumulam (como implement→test
    /// na pipeline fixa). Fallback: a run do plan ou a run pai.</summary>
    internal static string? AnchorRunId(State state, int beforeStep)
    {
        for (var j = beforeStep - 1; j >= 0; j--)
        {
            var key = state.Steps![j].Key;
            if (state.Runs.TryGetValue(key, out var id))
            {
                return id;
            }
            var worker = state.Runs.Keys.FirstOrDefault(
                k => k.StartsWith(key + ":", StringComparison.Ordinal));
            if (worker is not null)
            {
                return state.Runs[worker];
            }
        }
        return state.ParentRunId;
    }

    /// <summary>Run efetiva de uma key: a última sucessora
    /// <c>-stallN</c> quando existe (mesma convenção da telemetria).</summary>
    internal static string? EffectiveRunId(State state, string key)
    {
        if (!state.Runs.TryGetValue(key, out var runId))
        {
            return null;
        }
        for (var n = 1;
            state.Runs.TryGetValue($"{key}-stall{n}", out var s); n++)
        {
            runId = s;
        }
        return runId;
    }

    /// <summary>Registra uma run nova de <paramref name="key"/>: a
    /// primeira ocupa a key base; retentativas (run anterior não
    /// completou) usam o próximo sufixo <c>-stallN</c> — o histórico
    /// de tentativas se preserva p/ telemetria e merge.</summary>
    internal static void RecordRun(State state, string key, string runId)
    {
        if (!state.Runs.ContainsKey(key))
        {
            state.Runs[key] = runId;
            return;
        }
        var n = 1;
        while (state.Runs.ContainsKey($"{key}-stall{n}"))
        {
            n++;
        }
        state.Runs[$"{key}-stall{n}"] = runId;
    }

    /// <summary>Condição <c>when</c> do step (v2): <c>passed:key</c> se o
    /// veredito de key é 'pass'; <c>failed:key</c> se qualquer não-pass.
    /// Key sem resultado (step pulado/a rodar) → não satisfeita → skip.
    /// Formato inválido → <paramref name="error"/> (autor corrige; nunca
    /// skip silencioso de condição malformada).</summary>
    internal static bool EvaluateWhen(
        string? when, State state, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(when) || when == "always")
        {
            return true;
        }
        var colon = when.IndexOf(':', StringComparison.Ordinal);
        var cond = colon > 0 ? when[..colon] : null;
        var refKey = colon > 0 ? when[(colon + 1)..] : null;
        if (cond is not ("passed" or "failed") || string.IsNullOrEmpty(refKey))
        {
            error = $"when inválido '{when}' — use 'passed:<key>' "
                + "ou 'failed:<key>'.";
            return false;
        }
        var has = state.PhaseResults.TryGetValue(refKey, out var r);
        return cond == "passed"
            ? has && r!.Verdict == "pass"
            : has && r!.Verdict != "pass";
    }

    /// <summary>Step <c>gate</c> (v2): sem resposta em
    /// <see cref="State.GateAnswers"/> estaciona a pipeline —
    /// <c>AwaitingGate</c> persistido, status segue running e a pergunta
    /// é postada no chat pai; <c>dev_pipeline op=answer</c> resolve e
    /// re-dispara. Resolvido: approved→verdict 'pass'; rejected→'issues',
    /// fatal salvo <c>optional:true</c>.</summary>
    private async Task<bool> GateStepAsync(
        State state, WorkflowStep step, CancellationToken ct)
    {
        if (!state.GateAnswers.TryGetValue(step.Key, out var answer))
        {
            state.AwaitingGate = step.Key;
            await SaveAsync(null, state, ct);
            await PostToParentAsync(state,
                $"[dev_pipeline {state.Id}] Workflow parou no gate "
                    + $"'{step.Key}' — aguardando decisão.\n\n"
                    + $"{step.Prompt ?? state.Task}\n\n"
                    + "Responda nesta conversa — eu chamo dev_pipeline "
                    + $"op=answer pipeline_id={state.Id} "
                    + "approved=true|false answer=\"<sua decisão>\" "
                    + "para o workflow seguir.",
                $"gate:{step.Key}", ct);
            return false;
        }
        state.AwaitingGate = null;
        var approved = answer.StartsWith(
            "approved", StringComparison.Ordinal);
        state.PhaseResults[step.Key] = new PhaseResult(
            null, Tail(answer, 500), approved ? "pass" : "issues",
            null, null, null);
        if (!approved && step.Optional != true)
        {
            await MarkFailedAsync(state,
                $"workflow: gate '{step.Key}' rejeitado — "
                    + Tail(answer, 300));
            return false;
        }
        return true;
    }

    /// <summary>Registra a falha do step como verdict <c>issues</c> (não
    /// marca a pipeline — o loop decide fatal × <c>optional</c>).
    /// Devolve false para o dispatch.</summary>
    private Task<bool> IssueAsync(
        State state, WorkflowStep step, string error)
    {
        state.PhaseResults.TryAdd(step.Key, new PhaseResult(
            null, Tail(error, 500), "issues", null, null, null));
        return Task.FromResult(false);
    }

    /// <summary>Interpolação de prompt (RF-003): <c>{task}</c>,
    /// <c>{plan}</c> e <c>{results:key}</c> — o equivalente leve do
    /// <c>trajectory()</c>: cada step pode ler o resultado dos anteriores.</summary>
    internal static string Interpolate(string text, State state)
    {
        var result = text.Replace("{task}", state.Task);
        if (state.Plan is { } plan)
        {
            result = result.Replace("{plan}", plan);
        }
        foreach (var (key, value) in state.PhaseResults)
        {
            result = result.Replace(
                $"{{results:{key}}}", value.Verdict ?? value.Summary ?? "");
        }
        return result;
    }

    /// <summary>Step <c>tool</c>: executa builtin server-side com os args
    /// declarados — o gate aconteceu na chamada do run_workflow (Exec).
    /// Resultado (texto) vira <see cref="PhaseResult"/> da key.</summary>
    private async Task<bool> RunToolStepAsync(
        State state, WorkflowStep step, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var registry = scope.ServiceProvider
            .GetRequiredService<BuiltinToolRegistry>();
        var repos = scope.ServiceProvider
            .GetRequiredService<WorkspaceRepoService>();
        var env = scope.ServiceProvider
            .GetRequiredService<IHostEnvironment>();
        var bare = step.Tool is { } t
            && t.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)
                ? t["builtin:".Length..] : step.Tool;
        var builtin = bare is null ? null : registry.All.FirstOrDefault(
            x => string.Equals(x.Name, bare, StringComparison.OrdinalIgnoreCase));
        if (builtin is null)
        {
            return await IssueAsync(state, step,
                $"tool '{step.Tool}' não existe ou está desabilitada");
        }
        JsonElement argsEl;
        try
        {
            argsEl = BuiltinToolRegistry.NormalizeArgs(step.Args ?? "{}");
        }
        catch (JsonException)
        {
            return await IssueAsync(state, step,
                $"args inválidos p/ '{bare}' — JSON malformado");
        }
        var workdir = await repos.ResolveWorkdirAsync(
            state.UserId, state.ChildChatId, ct);
        var uploads = Path.Join(
            DataPaths.Root(env.ContentRootPath), "uploads", state.UserId);
        BuiltinToolResult result;
        try
        {
            result = await builtin.ExecuteAsync(argsEl,
                new BuiltinToolContext(state.UserId, state.ChildChatId, null,
                    workdir, uploads,
                    state.Models.GetValueOrDefault(step.Key)), ct);
        }
        catch (Exception ex)
        {
            return await IssueAsync(state, step,
                $"tool '{bare}' lançou exceção: {ex.Message}");
        }
        state.PhaseResults[step.Key] = new PhaseResult(
            null, Tail(result.Text ?? "", 500),
            result.Refused ? "issues" : "pass", null, null, null);
        if (result.Refused)
        {
            return await IssueAsync(state, step,
                $"tool '{bare}' recusou: {result.RefuseReason}");
        }
        return true;
    }

    /// <summary>Step <c>validate</c>: ProjectValidation determinístico —
    /// falha encerra a pipeline (sem fix automático; o autor encadeia um
    /// step <c>llm</c> de correção lendo <c>{results:key}</c>).</summary>
    private async Task<bool> RunValidateStepAsync(
        State state, WorkflowStep step, CancellationToken ct)
    {
        var outcome = await ValidateAsync(state, ct);
        state.ValidationOutput = outcome?.Summary;
        state.PhaseResults[step.Key] = new PhaseResult(
            null, outcome?.Summary,
            outcome is { Passed: false } ? "issues" : "pass",
            null, null, null);
        if (outcome is { Passed: false })
        {
            return await IssueAsync(state, step,
                $"validate '{step.Key}' falhou — {Tail(outcome.Output, 500)}");
        }
        return true;
    }

    /// <summary>Step <c>commit</c>: git determinístico no workdir do
    /// filho; <c>push:true</c> no step habilita o push nesta pipeline.</summary>
    private async Task<bool> RunCommitStepAsync(
        State state, WorkflowStep step, CancellationToken ct)
    {
        if (step.Push == true)
        {
            state.Push = true;
        }
        try
        {
            var sha = await CommitAsync(state, ct);
            state.CommitSha ??= sha;
            state.PhaseResults[step.Key] = new PhaseResult(
                null, sha is { } ? $"commit {sha}" : "commit skipped",
                "pass", null, null, null);
            return true;
        }
        catch (Exception ex)
        {
            return await IssueAsync(state, step,
                $"commit '{step.Key}' falhou: {ex.Message}");
        }
    }

    /// <summary>Step <c>parallel</c> (RF-002): workers em chats irmãos +
    /// worktree isolado, ondas de <see cref="State.MaxFanout"/>, wait-all
    /// por onda e merge sequencial no workdir do filho. Prompt do item é
    /// o declarado no workflow (+ trailer de resultado). Exige binding.</summary>
    private async Task<bool> RunParallelStepAsync(
        State state, WorkflowStep step, string? anchor,
        CancellationToken ct)
    {
        var items = step.Items ?? [];
        if (items.Count == 0)
        {
            return await IssueAsync(state, step,
                $"parallel '{step.Key}' sem items");
        }
        if (!await HasRepoBindingAsync(state, ct))
        {
            return await IssueAsync(state, step,
                $"parallel '{step.Key}' exige binding de repo no chat filho");
        }
        foreach (var wave in items.Chunk(Math.Max(1, state.MaxFanout)))
        {
            var workers = new List<(string Key, string ChatId, string RunId, string Prompt)>();
            foreach (var item in wave)
            {
                var key = $"{step.Key}:{item.Id}";
                var prompt =
                    $"[run_workflow — {step.Key} · {item.Id}]\n"
                        + Interpolate(item.Prompt, state)
                        + (item.Files is { Count: > 0 } f
                            ? $"\nEscopo de arquivos: {string.Join(", ", f)}"
                            : string.Empty)
                        + ResultTrailer;
                state.FanoutItemChats.TryGetValue(key, out var chatId);
                var runId = EffectiveRunId(state, key);
                if (runId is not null
                    && await RunStatusAsync(runId, ct)
                        is { Status: ChatRunStatus.Completed })
                {
                    if (chatId is not null)
                    {
                        workers.Add((key, chatId, runId, prompt));
                    }
                    continue; // resume: completou — só merge
                }
                chatId ??= await CreateSiblingChatAsync(state, item.Id, ct);
                if (chatId is null)
                {
                    return await IssueAsync(state, step,
                        $"parallel '{key}': sem chat irmão (sem binding?)");
                }
                state.FanoutItemChats[key] = chatId;
                // Retry por item: run anterior não completou → nova
                // tentativa em sufixo -stallN; completas seguem reusadas.
                var fresh = await EnqueuePhaseRunAsync(
                    state, step.Key, prompt, ct,
                    chatId: chatId, parentRunId: anchor);
                RecordRun(state, key, fresh);
                workers.Add((key, chatId, fresh, prompt));
            }
            await SaveAsync(null, state, ct);
            var results = await Task.WhenAll(workers.Select(w =>
                WaitRunAsync(w.RunId, state, step.Key, w.Prompt, ct,
                    w.Key, w.ChatId)));
            for (var i = 0; i < workers.Count; i++)
            {
                var w = workers[i];
                if (results[i].Status is not ChatRunStatus.Completed)
                {
                    return await IssueAsync(state, step,
                        $"parallel '{w.Key}' terminou como "
                            + $"'{results[i].Status}'"
                            + (results[i].Error is { } e
                                ? $": {e}" : string.Empty));
                }
                await ParsePhaseResultAsync(
                    state, step.Key, ct, resultKey: w.Key, chatId: w.ChatId);
            }
            var conflicts = await MergeFanoutAsync(state, workers, ct);
            if (conflicts.Count > 0)
            {
                return await IssueAsync(state, step,
                    $"parallel '{step.Key}': merge não aplicou "
                        + $"{conflicts.Count} arquivo(s): "
                        + Tail(string.Join("; ", conflicts), 400));
            }
        }
        return true;
    }

    /// worktree isolado por item, wait-all em paralelo, merge sequencial
    /// no workdir do filho e implement-rest sequencial p/ itens fora do
    /// lote. Conflitos de merge alimentam o fix; nunca são forçados.</summary>
    private async Task<bool> RunFanoutImplementAsync(
        State state, List<PlanItem> fanned, List<PlanItem> rest,
        CancellationToken ct)
    {
        // Dispatch: reutiliza chat/run existentes no resume; cria irmãos novos.
        var workers = new List<(string Key, string ChatId, string RunId, string Prompt)>();
        for (var idx = 0; idx < fanned.Count; idx++)
        {
            var item = fanned[idx];
            var key = $"implement:{ItemKey(item, idx)}";
            var prompt = ItemPrompt(state, item, key);
            state.FanoutItemChats.TryGetValue(key, out var chatId);
            var runId = EffectiveRunId(state, key);
            if (runId is not null
                && await RunStatusAsync(runId, ct)
                    is { Status: ChatRunStatus.Completed })
            {
                if (chatId is not null)
                {
                    workers.Add((key, chatId, runId, prompt));
                }
                continue; // resume: já completou — entra só p/ o merge
            }
            chatId ??= await CreateSiblingChatAsync(
                state, item.Title ?? item.Id ?? "item", ct);
            if (chatId is null)
            {
                await MarkFailedAsync(state,
                    $"fan-out: sem chat irmão p/ worker '{key}' (sem binding?)");
                return false;
            }
            state.FanoutItemChats[key] = chatId;
            // Worker que não completou ganha retentativa em sufixo
            // -stallN — a run morta não é reusada (resume cirúrgico).
            var fresh = await EnqueuePhaseRunAsync(
                state, "implement", prompt, ct,
                chatId: chatId, parentRunId: PlanRunId(state));
            RecordRun(state, key, fresh);
            workers.Add((key, chatId, fresh, prompt));
        }
        state.Phase = "implement";
        await SaveAsync(null, state, ct);

        // Wait-all paralelo — stall/replace é por item (runKey).
        var results = await Task.WhenAll(workers.Select(w =>
            WaitRunAsync(w.RunId, state, "implement", w.Prompt, ct,
                w.Key, w.ChatId)));
        for (var i = 0; i < workers.Count; i++)
        {
            var w = workers[i];
            if (results[i].Status is not ChatRunStatus.Completed)
            {
                await MarkFailedAsync(state,
                    $"fan-out: worker '{w.Key}' terminou como "
                        + $"'{results[i].Status}'"
                        + (results[i].Error is { } e ? $": {e}" : string.Empty));
                return false;
            }
            await ParsePhaseResultAsync(
                state, "implement", ct, resultKey: w.Key, chatId: w.ChatId);
        }

        // Merge sequencial item a item no workdir do filho; depois os
        // itens dependentes/overflow numa run sequencial no chat filho.
        var conflicts = await MergeFanoutAsync(state, workers, ct);
        if (rest.Count > 0
            && !await RunLlmPhaseAsync(
                state, "implement", RestPrompt(state, rest), ct,
                runKey: "implement-rest"))
        {
            return false;
        }
        if (conflicts.Count > 0 && state.FixAttempts < state.MaxFixAttempts)
        {
            state.FixAttempts++;
            var issue = "merge do fan-out não aplicou "
                + $"{conflicts.Count} arquivo(s) — recrie as mudanças "
                + "desses itens direto no workdir principal:\n"
                + string.Join("\n", conflicts.Select(c => $"- {c}"));
            if (!await RunFixAsync(state, issue, null, ct))
            {
                return false;
            }
        }
        else if (conflicts.Count > 0)
        {
            await MarkFailedAsync(state,
                "fan-out: conflitos de merge sem tentativas de correção "
                    + $"restantes: {Tail(string.Join("; ", conflicts), 500)}");
            return false;
        }
        return true;
    }

    /// <summary>Chat irmão do worker: mesmo pai/preset do filho, binding
    /// herdado → worktree próprio do mesmo repo (isolamento real).</summary>
    private async Task<string?> CreateSiblingChatAsync(
        State state, string label, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repos = scope.ServiceProvider.GetRequiredService<WorkspaceRepoService>();
        if (state.ChildChatId is null
            || await db.Chats.AsNoTracking()
                .Where(c => c.Id == state.ChildChatId)
                .Select(c => new { c.ApprovalPreset, c.ModelsJson })
                .FirstOrDefaultAsync(ct) is not { } child)
        {
            return null;
        }
        if (await repos.GetChatBindingAsync(state.ChildChatId, ct) is not { } binding)
        {
            return null;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sib = new Chat
        {
            UserId = state.UserId,
            Title = $"pipeline: {(label.Length > 48 ? label[..48] + "…" : label)}",
            ParentChatId = state.ChildChatId,
            ModelsJson = child.ModelsJson,
            ApprovalPreset = child.ApprovalPreset,
            Mode = PermissionRuleset.BuildMode,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Chats.Add(sib);
        await repos.SetChatBindingAsync(
            sib.Id, repos.InheritChatBinding(sib.Id, binding), ct);
        await db.SaveChangesAsync(ct);
        return sib.Id;
    }

    /// <summary>Merge dos worktrees dos workers no workdir do filho —
    /// origem = worktree isolado da run (fallback: workdir do irmão, que
    /// já é worktree do repo). Conflito remove o worktree e vira issue
    /// do fix — o fix recria o conteúdo no workdir principal.</summary>
    private async Task<List<string>> MergeFanoutAsync(
        State state,
        List<(string Key, string ChatId, string RunId, string Prompt)> workers,
        CancellationToken ct)
    {
        var conflicts = new List<string>();
        using var scope = scopes.CreateScope();
        var repos = scope.ServiceProvider.GetRequiredService<WorkspaceRepoService>();
        var worktrees = scope.ServiceProvider.GetRequiredService<WorktreeService>();
        var main = await repos.ResolveWorkdirAsync(
            state.UserId, state.ChildChatId, ct);
        foreach (var w in workers)
        {
            var source = worktrees.ResolveIsolated(state.UserId, w.RunId)
                ?? await repos.ResolveWorkdirAsync(state.UserId, w.ChatId, ct);
            var merge = await worktrees.MergeAsync(main, source, ct);
            if (merge.Merged)
            {
                continue;
            }
            if (merge.Error is { } err)
            {
                conflicts.Add($"{w.Key}: {err}");
            }
            conflicts.AddRange(merge.Conflicts.Select(c => $"{w.Key}: {c}"));
            await worktrees.RemoveAsync(main, source, ct);
        }
        return conflicts;
    }

    /// <summary>Run âncora dos workers/rest: a run do plan no chat filho
    /// (chat diferente do irmão → worktree isolado garantido ao worker).</summary>
    private static string? PlanRunId(State state) =>
        state.Runs.GetValueOrDefault("plan") ?? state.ParentRunId;

    /// <summary>Chave estável do item p/ <c>Runs</c>/<c>PhaseResults</c>.</summary>
    private static string ItemKey(PlanItem item, int index)
    {
        var raw = item.Id ?? $"item{index + 1}";
        var chars = raw.Select(
            c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        return new string(chars.ToArray());
    }

    /// <summary>Prompt do worker: escopo do item + contexto global —
    /// histórico do irmão é vazio, então o prompt carrega o necessário.</summary>
    private static string ItemPrompt(State state, PlanItem item, string key) =>
        $"[dev_pipeline — IMPLEMENTAÇÃO · {key}]\n"
            + $"Item do plano: {item.Title ?? item.Id ?? key}\n"
            + (item.Files is { Count: > 0 } f
                ? $"Escopo de arquivos: {string.Join(", ", f)}\n"
                : string.Empty)
            + $"Tarefa global: {state.Task}\n"
            + (state.ExtraContext is { } xc ? $"Contexto: {xc}\n" : string.Empty)
            + (state.Plan is { } p ? $"Plano aprovado:\n{Tail(p, 2000)}\n" : string.Empty)
            + "Implemente APENAS este item — os demais rodam em workspaces "
            + "paralelos e são mergeados depois. Fique no escopo, NÃO "
            + "escreva testes (fase própria após o merge) e NÃO faça "
            + "commit. Termine com <pipeline_result>{\"files\":[\"paths\"],"
            + "\"summary\":\"resumo\"}</pipeline_result>.";

    /// <summary>Prompt do implement-rest: itens dependentes/overflow no
    /// workdir do filho, já com o merge do fan-out aplicado.</summary>
    private static string RestPrompt(State state, List<PlanItem> rest) =>
        "[dev_pipeline — IMPLEMENTAÇÃO · itens restantes]\n"
            + "Os itens independentes já foram implementados em paralelo e "
            + "mergeados neste workdir. Implemente agora os itens "
            + "restantes do plano (dependentes ou fora do lote paralelo):\n"
            + string.Join("\n", rest.Select(r =>
                $"- {r.Id}: {r.Title ?? r.Id}"
                    + (r.Files is { Count: > 0 } f
                        ? $" [{string.Join(", ", f)}]" : string.Empty)))
            + $"\nTarefa global: {state.Task}\n"
            + "NÃO escreva testes e NÃO faça commit. Termine com "
            + "<pipeline_result>{\"files\":[\"paths\"],\"summary\":"
            + "\"resumo\"}</pipeline_result>.";

    /// <summary>
    /// Cria mensagem <c>user</c> + run na fase dentro do chat filho —
    /// mesmo padrão do drain de steers (histórico encadeado preserva o
    /// contexto das fases anteriores; <c>ParentRunId</c> mantém a cadeia e
    /// reutiliza o worktree isolado da fase anterior via sameChatParent).
    /// </summary>
    private async Task<string> EnqueuePhaseRunAsync(
        State state, string phase, string prompt, CancellationToken ct,
        string? modelOverride = null, string? chatId = null,
        string? parentRunId = null)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var boundaries = scope.ServiceProvider.GetRequiredService<ContextBoundaryService>();
        var targetChat = chatId ?? state.ChildChatId;
        var chat = await db.Chats.Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == targetChat, ct)
            ?? throw new InvalidOperationException(
                $"chat filho {targetChat} sumiu");
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
        // RF-007: modelo dedicado da fase (worker/fix) sobrescreve o
        // herdado; <paramref name="modelOverride"/> (substituta por stall)
        // vence os dois.
        var phaseModel = modelOverride
            ?? (state.Models.TryGetValue(phase, out var pm) ? pm : null);
        if (!string.IsNullOrWhiteSpace(phaseModel))
        {
            request = request with { Model = phaseModel };
        }
        var previousRunId = parentRunId
            ?? (state.Runs.TryGetValue("test", out var t)
                ? t
                : state.Runs.TryGetValue("implement", out var i)
                    ? i
                    : state.ParentRunId);
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

    /// <summary>Espera do término da run — wake event-driven + poll de
    /// staleness (padrão WaitForChildAsync). O stream do broadcaster
    /// fecha quando o worker conclui a run → o join acorda na hora em vez
    /// de esperar o próximo tick; o poll segue porque run travada não
    /// emite evento (staleness de heartbeat só se detecta olhando).
    /// Deadline configurável <c>Agent:DevPipeline:PhaseTimeoutSec</c>
    /// (default 1200s). RF-008: <c>Running</c> com <c>HeartbeatAt</c> velho
    /// (&gt;<c>Agent:DevPipeline:StallSec</c>, default 240s, por 2 polls) é
    /// considerada travada → substituída com prompt de retomada, até
    /// <see cref="State.MaxStallRetries"/> vezes; "timeout"/"stalled" nas
    /// bordas.</summary>
    private async Task<(string Status, string? Error)> WaitRunAsync(
        string runId, State state, string phase, string prompt,
        CancellationToken ct, string? runKey = null, string? chatId = null)
    {
        var timeout = int.TryParse(
            cfg["Agent:DevPipeline:PhaseTimeoutSec"], out var s)
                ? Math.Max(30, s) : 1200;
        var stallSec = int.TryParse(
            cfg["Agent:DevPipeline:StallSec"], out var ss)
                ? Math.Max(30, ss) : 240;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeout);
        var stallTicks = 0;
        using var scope = scopes.CreateScope();
        var broadcaster = scope.ServiceProvider
            .GetRequiredService<ChatRunBroadcaster>();
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamClosed = WaitStreamClosedAsync(
            broadcaster, runId, wake.Token, logger);
        for (;;)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return ("timeout", $"run {runId} excedeu {timeout}s");
            }
            var delay = Task.Delay(PollInterval, ct);
            if (streamClosed is null)
            {
                await delay;
            }
            else if (await Task.WhenAny(streamClosed, delay) == streamClosed)
            {
                streamClosed = null; // stream fechou — poll assume daqui
            }
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
            // Retry por runKey (SPEC-20261123): worker travado do fan-out
            // não consome o budget das irmãs.
            var key = runKey ?? phase;
            var used = state.StallRetriesByKey.GetValueOrDefault(key);
            if (used >= state.MaxStallRetries)
            {
                return ("stalled",
                    $"run {runId} sem heartbeat há >{stallSec}s e sem "
                        + "retries restantes");
            }
            state.StallRetriesByKey[key] = used + 1;
            state.StallRetries++;
            runId = await ReplaceStalledRunAsync(
                state, phase, runId, prompt, ct, key, used + 1, chatId);
            streamClosed = WaitStreamClosedAsync(
                broadcaster, runId, wake.Token, logger);
            stallTicks = 0;
        }
    }

    /// <summary>Completa quando o stream da run fecha (o dispatcher chama
    /// <c>Complete</c> no fim da execução). Falhas de assinatura degradam
    /// para "fechado" — o poll regular continua sendo a fonte da verdade.</summary>
    internal static async Task WaitStreamClosedAsync(
        ChatRunBroadcaster broadcaster, string runId, CancellationToken ct,
        ILogger? logger = null)
    {
        try
        {
            await foreach (var _ in broadcaster.SubscribeAsync(runId, 0, ct))
            {
                // Drena eventos até o stream fechar — o fechamento é o
                // sinal de wake; o conteúdo já foi visto pelo publisher.
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelamento externo — o wait degrada para o poll de status.
        }
        catch (Exception ex)
        {
            // Assinatura quebrada não pode derrubar o wait — o poll de
            // status segue cobrindo o ciclo da run.
            logger?.LogDebug(ex,
                "Assinatura do stream da run {RunId} falhou — wait segue no poll.",
                runId);
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
    /// retomada (RF-008) — registrada como <c>{runKey}-stall{n}</c>.</summary>
    private async Task<string> ReplaceStalledRunAsync(
        State state, string phase, string runId, string prompt,
        CancellationToken ct, string runKey, int attempt,
        string? chatId = null)
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
        // Stall pode ser hang/quota do próprio modelo — a substituta avança
        // a chain para o próximo candidato (o retry→replace do agy).
        var replacementModel = state.Models.TryGetValue(phase, out var pm)
            ? RotateChain(pm, attempt)
            : null;
        var newRunId = await EnqueuePhaseRunAsync(
            state, phase, resumePrompt, ct, replacementModel, chatId);
        state.Runs[$"{runKey}-stall{attempt}"] = newRunId;
        await SaveAsync(null, state, ct);
        return newRunId;
    }

    /// <summary>Roda uma ref <c>chain:{m1,…}</c> <paramref name="index"/>
    /// posições — a substituta de uma run travada começa do candidato
    /// seguinte (stall pode ser hang/quota do próprio modelo). Ref que
    /// não é chain ou índice fora do range passa intacto.</summary>
    internal static string RotateChain(string model, int index)
    {
        if (index <= 0
            || !ChainModelRef.TryParse(model, out var chain)
            || index >= chain.Count)
        {
            return model;
        }
        return ChainModelRef.Format(
            chain.Skip(index).Concat(chain.Take(index)).ToList());
    }

    /// <summary>Extrai o bloco <c>&lt;pipeline_result&gt;</c> da última
    /// resposta assistant do chat filho para a fase (RF-010) — a fase plan
    /// ainda alimenta <see cref="State.Plan"/>. Fan-out lê no chat irmão
    /// (<paramref name="chatId"/>) e grava com a chave do item
    /// (<paramref name="resultKey"/>).</summary>
    private async Task ParsePhaseResultAsync(
        State state, string phase, CancellationToken ct,
        string? resultKey = null, string? chatId = null)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var target = chatId ?? state.ChildChatId;
        var lastAssistant = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == target && m.Role == "assistant")
            .OrderByDescending(m => m.Timestamp)
            .Select(m => m.Content)
            .FirstOrDefaultAsync(ct);
        var parsed = ParsePhaseResult(lastAssistant);
        if (parsed is null)
        {
            return;
        }
        state.PhaseResults[resultKey ?? phase] = parsed;
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
            // SPEC-20261123: "items":[{id,title,files[],independent}] —
            // só a fase plan emite; independent habilita worker paralelo.
            List<PlanItem>? items = null;
            if (root.TryGetProperty("items", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                items = arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Object)
                    .Select(e => new PlanItem(
                        Str(e, "id"), Str(e, "title"), Strings(e, "files"),
                        e.TryGetProperty("independent", out var ind)
                            && ind.ValueKind == JsonValueKind.True))
                    .ToList();
            }
            return new PhaseResult(
                Strings(root, "files"), Str(root, "summary"),
                Str(root, "verdict"), Strings(root, "issues"),
                Str(root, "plan"), items);
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

        // Merge dos worktrees isolados: a última run de cada base key
        // (sucessora -stallN representa o estado final da fase). "plan"
        // não mergeia — fase read-only; implement:{id} já mergearam no
        // fan-out (worktree removido → ResolveIsolated null → skip).
        foreach (var key in state.Runs.Keys
            .Where(k => !k.Contains("-stall") && k != "plan").ToList())
        {
            var runId = state.Runs[key];
            for (var n = 1;
                state.Runs.TryGetValue($"{key}-stall{n}", out var s);
                n++)
            {
                runId = s;
            }
            if (worktrees.ResolveIsolated(state.UserId, runId) is { } iso)
            {
                var merge = await worktrees.MergeAsync(workdir, iso, ct);
                if (!merge.Merged)
                {
                    throw new InvalidOperationException(
                        $"merge do worktree '{key}' parcial — "
                            + $"{merge.Conflicts.Count} conflito(s): "
                            + string.Join("; ", merge.Conflicts));
                }
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
        var telemetry = await CollectTelemetryAsync(state, ct);
        await PostToParentAsync(
            state, BuildReport(state, telemetry), "report", ct);
    }

    /// <summary>Posta mensagem <c>user</c> <c>[dev_pipeline]</c> no chat
    /// pai + run filha para o agente apresentar — usado pelo relatório
    /// final e pelas perguntas de <c>gate</c> (v2).</summary>
    private async Task PostToParentAsync(
        State state, string content, string phase, CancellationToken ct)
    {
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
            Content = content,
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
                ["devPipeline"] = new { id = state.Id, phase },
            }, JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync(ct);
        scope.ServiceProvider.GetRequiredService<Runs.ChatRunDispatcher>()
            .Enqueue(run.Id);
    }

    /// <summary>Telemetria por key de run (RF fan-out hardening): status
    /// da run efetiva (sucessora -stallN), duração em segundos e stalls
    /// acumulados na key base.</summary>
    internal sealed record RunTelemetry(string Status, long? DurationSec, int Stalls);

    /// <summary>Coleta status/duração das runs do estado numa query só —
    /// a run efetiva de cada key base é a última <c>-stallN</c> quando há.</summary>
    private async Task<Dictionary<string, RunTelemetry>> CollectTelemetryAsync(
        State state, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var runIds = state.Runs.Values.ToList();
        var rows = await db.ChatRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Status, r.StartedAt, r.CompletedAt })
            .ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.Id);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var map = new Dictionary<string, RunTelemetry>();
        foreach (var (key, runId) in state.Runs)
        {
            if (key.Contains("-stall"))
            {
                continue; // agregada na key base via cadeia abaixo
            }
            var effective = runId;
            for (var n = 1;
                state.Runs.TryGetValue($"{key}-stall{n}", out var s);
                n++)
            {
                effective = s;
            }
            byId.TryGetValue(effective, out var row);
            long? dur = row?.StartedAt is { } st
                ? (row.CompletedAt ?? now) - st
                : null;
            map[key] = new RunTelemetry(
                row?.Status ?? "missing", dur,
                state.StallRetriesByKey.GetValueOrDefault(key));
        }
        return map;
    }

    /// <summary>Texto do relatório visto pelo agente pai e pelo usuário.</summary>
    internal static string BuildReport(
        State state, IReadOnlyDictionary<string, RunTelemetry>? telemetry = null)
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
            + (telemetry is { Count: > 0 }
                ? "Runs: " + string.Join(" · ", telemetry.Select(
                    kv => FormatRunTelemetry(kv.Key, kv.Value))) + "\n"
                : string.Empty)
            + (state.AwaitingGate is { } gate
                ? $"Gate pendente: '{gate}' — responda via dev_pipeline "
                    + "op=answer.\n"
                : string.Empty)
            + (state.SkippedSteps.Count > 0
                ? $"Pulados (when): {string.Join(", ", state.SkippedSteps)}\n"
                : string.Empty)
            + (state.StallRetries > 0
                ? $"Stalls recuperados: {state.StallRetries}\n" : string.Empty)
            + $"Entrega: {commit}"
            + (state.Push ? " + push" : string.Empty)
            + (state.Error is { } e ? $"\nErro: {e}" : string.Empty)
            + $"\nChat da pipeline: /c/{state.ChildChatId}\n"
            + (state.FanoutItemChats.Count > 0
                ? "Workers do fan-out: "
                    + string.Join(", ", state.FanoutItemChats.Select(
                        kv => $"{kv.Key}→/c/{kv.Value}"
                            + (telemetry is not null
                                && telemetry.TryGetValue(kv.Key, out var t)
                                    ? $" ({FormatTelemetryValue(t)})"
                                    : string.Empty))) + "\n"
                : string.Empty)
            + "Apresente este resultado ao usuário de forma resumida.";
    }

    private static string FormatRunTelemetry(string key, RunTelemetry t) =>
        $"{key}={FormatTelemetryValue(t)}";

    private static string FormatTelemetryValue(RunTelemetry t) =>
        t.Status
            + (t.DurationSec is { } d ? $" {FormatDuration(d)}" : string.Empty)
            + (t.Stalls > 0 ? $" ({t.Stalls} stall{(t.Stalls > 1 ? "s" : "")})" : string.Empty);

    private static string FormatDuration(long sec) =>
        sec < 60 ? $"{sec}s" : $"{sec / 60}m{sec % 60:D2}s";

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
