using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:delegate_task</c> — delega uma subtarefa a uma run filha
/// (SPEC-20261007-chat-agent-parity RF-016, padrão <c>task</c> do opencode /
/// child session do Devin): cria um chat próprio do usuário com o prompt
/// delegado, enfileira a run (histórico isolado herdando modelo, tools —
/// menos o próprio delegate, para impedir recursão — e preset do chat pai)
/// e aguarda o término até <see cref="TimeoutSeconds"/>; o resultado volta
/// ao transcript do pai como tool_result com o link do chat filho.
/// Um chat separado é necessário: o dispatcher serializa runs por chat —
/// um filho no mesmo chat só executaria depois que o pai terminasse,
/// enquanto o pai o aguarda (deadlock).
/// <para>
/// SPEC-20261010-async-delegate: <c>wait=false</c> retorna imediatamente
/// com <c>childRunId</c> — a run filha continua server-side e o pai
/// colhe o resultado depois via <c>builtin:run_result</c> (paralelismo
/// real entre subtarefas). A profundidade é derivada da cadeia
/// <see cref="ChatRun.ParentRunId"/>: runs com profundidade ≥
/// <see cref="MaxDepth"/> perdem a tool <c>delegate_task</c> (sub-agents
/// encadeados até 3 níveis, estilo opencode-web).
/// </para>
/// </summary>
public sealed class DelegateTaskBuiltinTool(
    IServiceScopeFactory scopeFactory,
    IChatRunDispatcher dispatcher,
    RunEventWriter? events = null) : IBuiltinChatTool
{
    private const string SubagentFresh = "fresh";
    /// <summary>Tempo máximo de espera pela run filha (SPEC: 5min).</summary>
    private const int TimeoutSeconds = 300;

    /// <summary>Teto do transcript do pai injetado no fork
    /// (SPEC-20261103-subagent-fork RF-001): os mais recentes ganham o
    /// orçamento — summaries de compactação sempre entram.</summary>
    private const int MaxForkTranscriptChars = 60_000;

    /// <summary>Intervalo entre polls de status da run filha.</summary>
    private const int PollMs = 1500;

    /// <summary>
    /// Profundidade máxima da cadeia de delegação (SPEC-20261010-async-delegate):
    /// depth 0 = run raiz; filhos a partir de <see cref="MaxDepth"/> não herdam
    /// a tool <c>delegate_task</c>. Derivada caminhando <see cref="ChatRun.ParentRunId"/>
    /// — sem coluna nova no schema.
    /// </summary>
    private const int MaxDepth = 3;

    /// <summary>Caps de entrada/saída.</summary>
    private const int MaxPromptChars = 4000;
    private const int MaxContextChars = 2000;
    private const int MaxResultChars = 6000;
    private const int MaxTitleChars = 60;

    /// <inheritdoc />
    public string Name => "delegate_task";

    /// <inheritdoc />
    public string Description =>
        "Delegate to a child agent — call for parallel work. "
            + "Never delegate understanding: include file paths, line numbers "
            + "and what to change — don't write 'based on your findings'. "
            + "Set wait=false to run in background and collect later via "
            + "builtin_run_result. subagent_type='fork' inherits this "
            + "conversation's context (its tool output still stays out of "
            + "your transcript); 'fork-background' is fork + wait=false.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "prompt": {
              "type": "string",
              "description": "Complete, self-contained instruction for the subtask (the child cannot see this conversation)."
            },
            "context": {
              "type": "string",
              "description": "Optional extra context (paths, decisions, constraints) appended to the prompt."
            },
            "wait": {
              "type": "boolean",
              "description": "true (default): block until the child finishes. false: return childRunId immediately — collect later with builtin_run_result."
            },
            "repo": {
              "type": "string",
              "description": "Optional 'owner/repo' slug — bind the child chat to this repo instead of inheriting the parent's binding (sub-agent on another repository)."
            },
            "branch": {
              "type": "string",
              "description": "Optional branch for 'repo' (default 'main')."
            },
            "clone_url": {
              "type": "string",
              "description": "Optional clone URL override for 'repo' (default https://github.com/<repo>.git)."
            },
            "persona": {
              "type": "string",
              "description": "Optional persona: name or id of an active workspace Skill whose instructions become the sub-agent's system prompt (e.g. 'reviewer', 'tester')."
            },
            "agent": {
              "type": "string",
              "description": "Optional declarative agent name (see builtin_agent_catalog): applies its system prompt, model and tool allowlist to the child run. 'ext:{id}' delegates to an external agent CLI (claude, codex, devin, gemini, opencode) instead of a child run."
            },
            "merge": {
              "type": "boolean",
              "description": "true: auto-merge the child's worktree into the workspace when it completes (wait=true only; with wait=false the parent reviews via builtin_worktree_diff and merges via builtin_worktree_merge)."
            },
            "team": {
              "type": "boolean",
              "description": "true: the child joins the caller's agent team (durable roster via builtin_team_status/team_task/team_message). Creates the team on first use."
            },
            "resume_chat_id": {
              "type": "string",
              "description": "Resume an existing child chat (continuable sub-agent) instead of creating a new one — the prompt is appended and a new run enqueued there."
            },
            "persistent": {
              "type": "boolean",
              "description": "true: reuse one child chat across dispatches of this parent run — the sidekick keeps memory between handoffs (requires/ generates sidekick_id; serialized per sidekick). Not valid for ext:* agents."
            },
            "sidekick_id": {
              "type": "string",
              "description": "Stable id of the persistent sidekick within this parent run (auto-generated when persistent:true and omitted)."
            },
            "subagent_type": {
              "type": "string",
              "enum": ["fresh", "fork", "fork-background"],
              "description": "fresh (default): zero-context child. fork: the child inherits this conversation's transcript minus tool output — multi-step work that continues your reasoning. fork-background: fork + wait=false. Not valid with persistent/resume_chat_id/ext:*. Forks cannot create forks — a fork calling fork gets denied."
            }
          },
          "required": ["prompt"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Exec;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var prompt = ReadString(args, "prompt", MaxPromptChars);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new BuiltinToolResult("Parâmetro 'prompt' é obrigatório.");
        }

        var fullPrompt = BuildFullPrompt(args, prompt);

        // SPEC-20261103-subagent-fork RF-001: `subagent_type` escolhe o
        // modo do filho — fresh (default, comportamento atual), fork
        // (herda o transcript do pai) ou fork-background (fork + wait=false).
        var subagentType = ReadString(args, "subagent_type", 30)
            ?.Trim().ToLowerInvariant() ?? SubagentFresh;
        if (subagentType is not (SubagentFresh or "fork" or "fork-background"))
        {
            return new BuiltinToolResult(
                $"subagent_type '{subagentType}' inválido — use "
                    + "fresh, fork ou fork-background.");
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // SPEC-20261010-delegate-persona: persona = slug (Name ou Id) de uma
        // Skill ativa do usuário — o conteúdo vira system prompt do filho.
        var (personaSkill, personaError) = await ResolvePersonaAsync(db, context, args, ct);
        if (personaError is not null)
        {
            return personaError;
        }

        // SPEC-20261014-agent-definitions RF-003: `agent` referencia uma
        // definição declarativa do catálogo (built-in < admin < user <
        // workspace opt-in). Sub-agentes só podem delegar para nomes da
        // sua allowlist `spawns` (kv gravada no nascimento da run).
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
        var agentName = ReadString(args, "agent", 200)?.Trim();

        // SPEC-20261016-external-agent-registry RF-005: agent:"ext:{id}"
        // executa o CLI externo headless (confined ao workdir, tier Exec já
        // coberto pelo gate da tool) — sem run filha.
        if (agentName?.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (subagentType != SubagentFresh)
            {
                return new BuiltinToolResult(
                    "subagent_type 'fork' não se aplica a agentes ext:* "
                        + "(não há run filha para herdar contexto).");
            }
            return await RunExternalAgentAsync(
                scope, agentName["ext:".Length..], fullPrompt, context, ct);
        }

        return await RouteDispatchAsync(
            new DispatchRoute(args, scope, db, config, context, agentName,
                personaSkill, prompt, fullPrompt, subagentType), ct);
    }

    private static string BuildFullPrompt(JsonElement args, string prompt)
    {
        var extra = ReadString(args, "context", MaxContextChars);
        return string.IsNullOrWhiteSpace(extra)
            ? prompt
            : $"{prompt}\n\nContexto adicional:\n{extra}";
    }

    /// <summary>Gate de spawn → resolução do agente → run pai → rota de
    /// dispatch (sidekick persistente, resume ou one-shot).</summary>
    private async Task<BuiltinToolResult> RouteDispatchAsync(
        DispatchRoute r, CancellationToken ct)
    {
        var (gateError, allowedCsv) = await CheckSpawnGateAsync(
            r.Config, r.Context, r.AgentName, ct);
        if (gateError is not null)
        {
            return gateError;
        }

        var (agent, agentError) = await ResolveAgentAsync(
            r.Scope, r.Context, r.AgentName, allowedCsv, r.PersonaSkill, ct);
        if (agentError is not null)
        {
            return agentError;
        }

        var parentRun = r.Context.RunId is { } parentId
            ? await r.Db.ChatRuns.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == parentId, ct)
            : null;
        if (parentRun is null)
        {
            return new BuiltinToolResult(
                "delegate_task só pode ser chamada dentro de uma run de chat.");
        }

        var flags = ParseDispatchFlags(r.Args);

        var forkGuard = await CheckForkGuardAsync(r, flags, ct);
        if (forkGuard is not null)
        {
            return forkGuard;
        }

        if (flags.Persistent)
        {
            return await PersistentSidekickDispatchAsync(
                new SidekickDispatch(r.Args, r.Config, agent, r.PersonaSkill,
                    r.Prompt, r.FullPrompt, flags.WaitExplicit, flags.AutoMerge,
                    flags.JoinTeam, r.Scope, r.Db, r.Context, parentRun), ct);
        }
        if (flags.ResumeChatId is not null)
        {
            return await ResumeInExistingChatAsync(
                new ResumeRequest(r.Scope, r.Db, r.Context, parentRun,
                    flags.ResumeChatId, r.FullPrompt, flags.WaitExplicit ?? true,
                    flags.JoinTeam), ct);
        }

        var dispatch = await CreateDispatchAsync(
            new DispatchRequest(r.Args, r.Config, agent, r.FullPrompt,
                flags.WaitExplicit, flags.AutoMerge, flags.JoinTeam,
                r.PersonaSkill?.Content, r.Prompt, r.SubagentType),
            r.Scope, r.Db, r.Context, parentRun, ct);
        return dispatch.Result;
    }

    /// <summary>Flags booleanos de dispatch lidos dos args (S3776).</summary>
    private sealed record DispatchFlags(
        bool? WaitExplicit, bool AutoMerge, bool JoinTeam,
        string? ResumeChatId, bool Persistent);

    /// <summary>Extrai wait/merge/team/resume_chat_id/persistent dos args.</summary>
    private static DispatchFlags ParseDispatchFlags(JsonElement args)
    {
        var waitExplicit = args.TryGetProperty("wait", out var waitEl)
            ? waitEl.ValueKind == JsonValueKind.True
            : (bool?)null;
        var autoMerge = args.TryGetProperty("merge", out var mergeEl)
            && mergeEl.ValueKind == JsonValueKind.True;
        // SPEC-20261021-harness-seams RF-E1: `team` junta o filho ao team
        // do chamador (roster durável); `resume_chat_id` reutiliza um chat
        // filho existente — subagente continuável em vez de one-shot.
        var joinTeam = args.TryGetProperty("team", out var teamEl)
            && teamEl.ValueKind == JsonValueKind.True;
        var resumeChatId = args.TryGetProperty("resume_chat_id", out var resumeEl)
            && resumeEl.ValueKind == JsonValueKind.String
                ? resumeEl.GetString() : null;
        // SPEC-20261031-sidekick-orchestration RF-002: `persistent:true`
        // reutiliza o chat filho do sidekick entre dispatches do mesmo pai.
        var persistent = args.TryGetProperty("persistent", out var persEl)
            && persEl.ValueKind == JsonValueKind.True;
        return new DispatchFlags(
            waitExplicit, autoMerge, joinTeam, resumeChatId, persistent);
    }

    /// <summary>SPEC-20261103-subagent-fork RF-003/RF-005: flag global
    /// desligada → denial claro; run nascida de fork não cria outro fork;
    /// fork não combina com persistent/resume (S3776).</summary>
    private static async Task<BuiltinToolResult?> CheckForkGuardAsync(
        DispatchRoute r, DispatchFlags flags, CancellationToken ct)
    {
        if (r.SubagentType == SubagentFresh)
        {
            return null;
        }
        if (!await r.Config.GetAsync("agent:subagent_fork", true, ct))
        {
            return new BuiltinToolResult(
                "subagent_type 'fork' está desabilitado "
                    + "(agent:subagent_fork=false).");
        }
        if (await r.Config.GetAsync<bool?>(
                $"r:{r.Context.RunId}:agent.fork", null, ct) == true)
        {
            return new BuiltinToolResult(
                "forks cannot create forks — execute directly; "
                    + "do not re-delegate.");
        }
        if (flags.Persistent || flags.ResumeChatId is not null)
        {
            return new BuiltinToolResult(
                "subagent_type 'fork' não combina com 'persistent' "
                    + "nem 'resume_chat_id'.");
        }
        return null;
    }

    /// <summary>Parâmetros do roteamento de dispatch (S3776/S107).</summary>
    private sealed record DispatchRoute(
        JsonElement Args, IServiceScope Scope, AppDbContext Db,
        ConfigService Config, BuiltinToolContext Context, string? AgentName,
        Skill? PersonaSkill, string Prompt, string FullPrompt,
        string SubagentType);

    /// <summary>Parâmetros do dispatch do sidekick (S107).</summary>
    private sealed record SidekickDispatch(
        JsonElement Args, ConfigService Config, AgentDefinition? Agent,
        Skill? PersonaSkill, string Prompt, string FullPrompt,
        bool? WaitExplicit, bool AutoMerge, bool JoinTeam,
        IServiceScope Scope, AppDbContext Db, BuiltinToolContext Context,
        ChatRun ParentRun);

    /// <summary>Parâmetros da criação do chat filho (S107).</summary>
    private sealed record ChildChatSpec(
        AppDbContext Db, BuiltinToolContext Context, AgentDefinition? Agent,
        string ChildModel, IReadOnlyList<string>? ChildToolIds,
        string? SystemPrompt, string Preset, string Prompt,
        string FullPrompt, bool JoinTeam, long Now);

    /// <summary>
    /// Caminho do sidekick persistente (SPEC-20261031-sidekick-orchestration
    /// RF-002): kv do pai resolve o chat filho entre dispatches e o
    /// semáforo serializa dispatches concorrentes do mesmo sidekick.
    /// </summary>
    private async Task<BuiltinToolResult> PersistentSidekickDispatchAsync(
        SidekickDispatch d, CancellationToken ct)
    {
        var (args, config, agent, personaSkill, prompt, fullPrompt,
            waitExplicit, autoMerge, joinTeam, scope, db, context,
            parentRun) = d;
        var sidekickId = ReadString(args, "sidekick_id", 100)?.Trim();
        if (string.IsNullOrEmpty(sidekickId))
        {
            sidekickId = $"sk-{Guid.NewGuid():N}"[..20];
        }
        var gate2 = SidekickGate.For(context.RunId!, sidekickId);
        await gate2.WaitAsync(ct);
        try
        {
            var kvKey = $"r:{context.RunId}:sidekick:{sidekickId}";
            var existingChat = await config.GetAsync<string?>(kvKey, null, ct);
            if (existingChat is not null)
            {
                return await ResumeInExistingChatAsync(
                    new ResumeRequest(scope, db, context, parentRun,
                        existingChat, fullPrompt, waitExplicit ?? true,
                        joinTeam), ct);
            }
            var created = await CreateDispatchAsync(
                new DispatchRequest(args, config, agent, fullPrompt,
                    waitExplicit, autoMerge, joinTeam,
                    personaSkill?.Content, prompt, SubagentFresh),
                scope, db, context, parentRun, ct);
            if (created.ChatId is { } createdChatId)
            {
                await config.SetAsync(kvKey, createdChatId, ct);
            }
            return created.Result;
        }
        finally
        {
            gate2.Release();
        }
    }

    /// <summary>
    /// Dispatch one-shot de uma run filha (extraído do fluxo principal para
    /// o caminho persistente do sidekick criar o primeiro handoff).
    /// Devolve o tool result + o chatId criado (para persistência do
    /// sidekick no kv do pai).
    /// </summary>
    private async Task<(BuiltinToolResult Result, string? ChatId)>
        CreateDispatchAsync(
            DispatchRequest input, IServiceScope scope, AppDbContext db,
            BuiltinToolContext context, ChatRun parentRun, CancellationToken ct)
    {
        var (args, config, agent, fullPrompt, waitExplicit, autoMerge,
            joinTeam, personaPrompt, prompt, subagentType) = input;
        var isFork = subagentType is "fork" or "fork-background";
        // Profundidade da cadeia pai (depth 0 = raiz). Runs de profundidade
        // ≥ MaxDepth perdem a tool delegate_task — o filho não pode delegar
        // além do limite (SPEC-20261010-async-delegate).
        var childDepth = await DepthOfAsync(db, parentRun, ct) + 1;
        var plan = PlanChildTools(scope, parentRun, agent, childDepth);

        // RF-003: modelo do filho — `model:` explícito > `role:` (cadeia do
        // papel) > modelo do pai. Com teto de tier herdado (SPEC-20261023
        // run-cost-budgets RF-004) a resolução de `role:` escolhe o melhor
        // candidato ≤ teto; nenhum elegível → erro no_model_in_tier, nunca
        // upgrade silencioso.
        var parentMaxTier = await config.GetAsync<int?>(
            $"r:{parentRun.Id}:agent.maxtier", null, ct);
        var (childModel, modelError) = await ResolveChildModelAsync(
            scope, agent, parentRun, context, parentMaxTier, ct);
        if (modelError is not null)
        {
            return (modelError, null);
        }

        // `blocking` do agente é o default de wait (o arg explícito vence);
        // `fork-background` (SPEC-20261103) equivale a wait=false.
        var wait = waitExplicit
            ?? (subagentType == "fork-background" ? false : agent?.Blocking)
            ?? true;

        // RF-001 `skills`: slugs de skills pessoais ativas autoload —
        // conteúdo concatenado ao system prompt do agente.
        var systemPrompt = agent is null ? personaPrompt
            : await ComposeSystemPromptAsync(db, context, agent, ct);

        var preset = context.ChatId is { } chatId
            ? await db.Chats.AsNoTracking()
                .Where(c => c.Id == chatId)
                .Select(c => c.ApprovalPreset)
                .FirstOrDefaultAsync(ct) ?? "approve-mutations"
            : "approve-mutations";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var childChat = await CreateChildChatAsync(
            new ChildChatSpec(db, context, agent, childModel!, plan.ToolIds,
                systemPrompt, preset, prompt, fullPrompt, joinTeam, now), ct);

        // SPEC-20261103-subagent-fork RF-001/RF-004: `fork` injeta o
        // transcript do pai (sem tool-output nem notas efêmeras) entre o
        // system e o prompt — o filho continua o raciocínio em vez de
        // nascer fresh.
        var forkPrefix = isFork
            ? await BuildForkPrefixAsync(db, context, parentRun, ct)
            : [];
        var childRequest = new ChatCompletionRequest(
            childModel!,
            [
                .. systemPrompt is null
                    ? Array.Empty<ChatCompletionMessage>()
                    : new[] { new ChatCompletionMessage("system", systemPrompt) },
                .. forkPrefix,
                new ChatCompletionMessage("user", fullPrompt),
            ],
            Stream: true,
            ToolIds: plan.ToolIds);
        var childRun = new ChatRun
        {
            ChatId = childChat.Id,
            UserId = context.UserId,
            Model = childModel!,
            RequestJson = JsonSerializer.Serialize(childRequest, JsonOptions),
            ParentRunId = parentRun.Id,
            CreatedAt = now,
        };
        db.ChatRuns.Add(childRun);
        await db.SaveChangesAsync(ct);

        // RF-003: constraints de delegação do filho — allowlist spawns +
        // profundidade residual ficam em kv para enforcement no delegate
        // dele (spawns ∪ maxDepth; enforcement por-hop, não transitivo).
        await PersistChildConstraintsAsync(
            config, agent, childRun, plan, parentMaxTier, ct);

        // RF-003 (SPEC-20261103): run nascida de fork carrega a marca —
        // o delegate_task dela nega fork-in-fork ("forks cannot create
        // forks — execute directly").
        if (isFork)
        {
            await config.SetAsync($"r:{childRun.Id}:agent.fork", true, ct);
        }

        // SPEC-20261010-chat-repo-binding: o filho herda o binding POR CHAT
        // do pai (não o global). SPEC-15: herda repo/branch mas com
        // worktree próprio — subtarefa não escreve no worktree do pai.
        var repos = scope.ServiceProvider.GetRequiredService<WorkspaceRepoService>();
        await InheritWorkspaceAsync(repos, context, childChat, ct);

        var repoError = await BindTargetRepoAsync(scope, context, args, childChat, ct);
        if (repoError is not null)
        {
            childRun.Status = ChatRunStatus.Failed;
            childRun.Error = repoError;
            await db.SaveChangesAsync(ct);
            return (new BuiltinToolResult(repoError), childChat.Id);
        }

        dispatcher.Enqueue(childRun.Id);

        // RF-006 (SPEC-20261103): telemetria do fork na run pai.
        var forkSpawnedAt = DateTimeOffset.UtcNow;
        if (isFork && events is not null && context.RunId is { } parentRunId)
        {
            await events.AppendAsync(parentRunId, RunEventWriter.Types.Note,
                new
                {
                    status = "fork_spawned",
                    parent_run = parentRun.Id,
                    mode = subagentType,
                }, ct);
        }

        var link = $"/c/{childChat.Id}";
        if (!wait)
        {
            // wait=false: devolve já — o pai segue o turno e colhe o
            // resultado depois via builtin:run_result (paralelismo real).
            return (new BuiltinToolResult(
                $"Subtarefa delegada em background — run filha {childRun.Id} "
                    + $"(chat filho: {link}). Chame builtin_run_result com "
                    + $"run_id={childRun.Id} para colher o resultado.",
                new
                {
                    childChatId = childChat.Id,
                    childRunId = childRun.Id,
                    status = "queued",
                }), childChat.Id);
        }

        var waitResult = await WaitForChildAsync(
            new ChildWait(db, scope, context, childChat, childRun,
                autoMerge, link), ct);
        if (isFork && events is not null && context.RunId is { } doneRunId)
        {
            await events.AppendAsync(doneRunId, RunEventWriter.Types.Note,
                new
                {
                    status = "fork_completed",
                    duration_ms = (long)(DateTimeOffset.UtcNow
                        - forkSpawnedAt).TotalMilliseconds,
                    output_tokens = await ForkOutputTokensAsync(
                        db, childRun.Id, ct),
                }, ct);
        }
        return (waitResult, childChat.Id);
    }

    /// <summary>Completion tokens da run filha via
    /// <see cref="ChatRun.MetaJson"/> (<c>usage.completionTokens</c>) —
    /// null quando o provider não reporta.</summary>
    private static async Task<int?> ForkOutputTokensAsync(
        AppDbContext db, string childRunId, CancellationToken ct)
    {
        var meta = await db.ChatRuns.AsNoTracking()
            .Where(r => r.Id == childRunId)
            .Select(r => r.MetaJson)
            .FirstOrDefaultAsync(ct);
        if (meta is null)
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(meta).RootElement
                .TryGetProperty("usage", out var u)
                && u.TryGetProperty("completionTokens", out var tok)
                && tok.ValueKind == JsonValueKind.Number
                    ? tok.GetInt32() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Prefixo de contexto do fork (SPEC-20261103 RF-001/RF-004):
    /// <c>[fork of {runId}]</c> + transcript do chat pai filtrado —
    /// <c>user</c>/<c>assistant</c> (texto) e marcadores
    /// <c>compaction</c> (o resumo corrente sobrevive); <c>tool</c> e
    /// notas efêmeras (<c>&lt;context_usage&gt;</c>, turn reminders)
    /// ficam de fora. Os mais recentes ganham o orçamento
    /// <see cref="MaxForkTranscriptChars"/>; summaries sempre entram.
    /// </summary>
    private static async Task<List<ChatCompletionMessage>> BuildForkPrefixAsync(
        AppDbContext db, BuiltinToolContext context,
        ChatRun parentRun, CancellationToken ct)
    {
        var prefix = new List<ChatCompletionMessage>
        {
            new("user",
                $"[fork of {parentRun.Id}] contexto herdado do pai — "
                    + "If you ARE the fork, execute directly; "
                    + "do not re-delegate forks."),
        };
        if (context.ChatId is null)
        {
            return prefix; // Edge case: transcript vazio → contexto mínimo.
        }
        var msgs = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == context.ChatId)
            .OrderBy(m => m.Position)
            .Select(m => new { m.Role, m.Content })
            .ToListAsync(ct);
        var kept = msgs.Where(m =>
            m.Role is "user" or "assistant" or "compaction"
            && !string.IsNullOrWhiteSpace(m.Content)
            && !m.Content.StartsWith("<context_usage", StringComparison.Ordinal)
            && !m.Content.StartsWith("turn_reminder", StringComparison.Ordinal))
            .ToList();
        var summaries = kept.Where(m => m.Role == "compaction").ToList();
        var rest = kept.Where(m => m.Role != "compaction").ToList();
        var budget = MaxForkTranscriptChars;
        var tail = new List<(string Role, string Content)>();
        for (var i = rest.Count - 1; i >= 0 && budget > 0; i--)
        {
            var m = rest[i];
            budget -= m.Content.Length;
            tail.Insert(0, (m.Role, m.Content));
        }
        foreach (var m in summaries.Concat(tail.Select(t =>
            new { t.Role, t.Content })))
        {
            prefix.Add(new ChatCompletionMessage(
                m.Role == "assistant" ? "assistant" : "user", m.Content));
        }
        return prefix;
    }

    /// <summary>
    /// Semáforo por sidekick (SPEC-20261031-sidekick-orchestration RF-002):
    /// serializa dispatches concorrentes no mesmo sidekick_id do pai.
    /// </summary>
    /// <summary>Argumentos do caminho de dispatch (extração p/ S107).</summary>
    private sealed record DispatchRequest(
        JsonElement Args, ConfigService Config, AgentDefinition? Agent,
        string FullPrompt, bool? WaitExplicit, bool AutoMerge, bool JoinTeam,
        string? PersonaPrompt, string Prompt, string SubagentType);

    private static class SidekickGate
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            string, SemaphoreSlim> Gates = new();

        /// <summary>Semáforo do par (run-pai, sidekick).</summary>
        public static SemaphoreSlim For(string parentRunId, string sidekickId) =>
            Gates.GetOrAdd($"{parentRunId}:{sidekickId}", _ => new SemaphoreSlim(1, 1));
    }


    /// <summary>
    /// Gate de delegação: depth residual esgotado bloqueia sempre; run de
    /// agente com allowlist <c>spawns</c> exige <c>agent</c> explícito.
    /// Devolve o csv da allowlist para a validação seguinte.
    /// </summary>
    private static async Task<(BuiltinToolResult? Error, string? AllowedCsv)>
        CheckSpawnGateAsync(
            ConfigService config, BuiltinToolContext context,
            string? agentName, CancellationToken ct)
    {
        var allowedCsv = await config.GetAsync<string?>(
            $"r:{context.RunId}:agent.spawns", null, ct);
        var callerDepth = await config.GetAsync<int?>(
            $"r:{context.RunId}:agent.depth", null, ct);
        if (callerDepth == 0)
        {
            // Run nascida de agente sem profundidade residual — a tool só
            // existe se vazou por toolIds explícitos; bloqueia sempre.
            return (new BuiltinToolResult(
                "Este sub-agente não pode delegar (maxDepth esgotado)."), allowedCsv);
        }
        if (agentName is null && allowedCsv is not null)
        {
            // Run nascida de agente com spawns: delegação livre bypassaria a
            // allowlist — o filho tem que referenciar um nome permitido.
            return (new BuiltinToolResult(
                "Este sub-agente só pode delegar via parâmetro 'agent' "
                    + $"para: {allowedCsv}."), allowedCsv);
        }
        return (null, allowedCsv);
    }

    /// <summary>
    /// Resolve a definição do agente pedido — existência no catálogo,
    /// presença na allowlist <c>spawns</c> e exclusividade com persona.
    /// </summary>
    private static async Task<(AgentDefinition? Agent, BuiltinToolResult? Error)>
        ResolveAgentAsync(
            IServiceScope scope, BuiltinToolContext context,
            string? agentName, string? allowedCsv, Skill? personaSkill,
            CancellationToken ct)
    {
        if (agentName is null)
        {
            return (null, null);
        }
        var agents = scope.ServiceProvider
            .GetRequiredService<AgentDefinitionService>();
        var agent = await agents.FindAsync(
            context.UserId, context.WorkspacePath, agentName, ct);
        if (agent is null)
        {
            var catalog = await agents.ListAsync(
                context.UserId, context.WorkspacePath, ct);
            return (null, new BuiltinToolResult(
                $"Agente '{agentName}' não encontrado. Disponíveis: "
                + (catalog.Count == 0
                    ? "(nenhum)"
                    : string.Join(", ", catalog.Keys.Order()))));
        }

        if (allowedCsv is not null
            && !allowedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
                .Contains(agent.Name, StringComparer.OrdinalIgnoreCase))
        {
            return (null, new BuiltinToolResult(
                $"Este sub-agente só pode delegar para: {allowedCsv}. "
                    + $"'{agent.Name}' não está na allowlist spawns."));
        }

        if (personaSkill is not null)
        {
            return (null, new BuiltinToolResult(
                "Use 'agent' OU 'persona' — os dois definem o system "
                    + "prompt do filho e não se combinam."));
        }
        return (agent, null);
    }

    /// <summary>Plano de tools/profundidade do filho calculado por
    /// <see cref="PlanChildTools"/>.</summary>
    private sealed record ChildToolPlan(
        IReadOnlyList<string>? ToolIds, int SpawnDepth, bool CanSpawn);

    /// <summary>
    /// Herança de tools do pai ∩ allowlist do agente, com
    /// <c>delegate_task</c> removida ao atingir <see cref="MaxDepth"/> ou
    /// sem capacidade de spawn — e readicionada quando <c>spawns</c>
    /// declarado a implica (mas a explicita a excluía intencionalmente).
    /// </summary>
    private ChildToolPlan PlanChildTools(
        IServiceScope scope, ChatRun parentRun, AgentDefinition? agent,
        int childDepth)
    {
        // Herda tools do pai; exclui o próprio delegate apenas quando o
        // filho atingiria o teto de profundidade.
        IReadOnlyList<string>? childToolIds = null;
        var parentHadDelegate = false;
        try
        {
            var parentRequest = JsonSerializer.Deserialize<ChatCompletionRequest>(
                parentRun.RequestJson, JsonOptions);
            parentHadDelegate = parentRequest?.ToolIds?.Any(id =>
                id.Equals($"{BuiltinToolRegistry.IdPrefix}{Name}",
                    StringComparison.OrdinalIgnoreCase)) == true;
            childToolIds = parentRequest?.ToolIds
                ?.Where(id => childDepth < MaxDepth
                    || !id.Equals(
                        $"{BuiltinToolRegistry.IdPrefix}{Name}",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (JsonException)
        {
            // RequestJson corrompido: delega sem tools, nunca falha por isso.
        }

        // RF-003: o agente filtra o conjunto herdado — allowlist ∩ tools do
        // pai (nunca amplia o preset do chat); ausente = só tools Read-tier;
        // ["*"] = herda integral. `spawns` vazio ou depth residual esgotado
        // também removem delegate_task do filho.
        if (agent is not null)
        {
            var registry = scope.ServiceProvider
                .GetRequiredService<BuiltinToolRegistry>();
            if (agent.Tools is null)
            {
                childToolIds = childToolIds?.Where(id =>
                        registry.TierOfName(StripPrefix(id)) == ApprovalTier.Read)
                    .ToList();
            }
            else if (!agent.Tools.Contains("*"))
            {
                childToolIds = childToolIds?.Where(id => agent.Tools.Contains(
                        StripPrefix(id), StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            // ["*"]: sem restrição — childToolIds passa integral.
        }
        // Depth residual do filho: min(maxDepth do agente, teto global) —
        // gravado como kv para enforcement quando ele próprio delegar.
        var spawnDepth = agent is null ? 0
            : Math.Min(agent.MaxDepth, MaxDepth - childDepth);
        var canSpawn = agent?.Spawns.Count > 0 && spawnDepth > 0;
        if (canSpawn && parentHadDelegate && childToolIds is List<string> mutable
            && (agent!.Tools is null || agent.Tools.Contains("*"))
            && !mutable.Contains($"{BuiltinToolRegistry.IdPrefix}{Name}",
                StringComparer.OrdinalIgnoreCase))
        {
            // `spawns` declarado implica a tool de delegação — readiciona
            // porque o pai a tinha (a allowlist Read-tier a teria filtrado).
            // Tools explícita sem delegate_task é exclusão intencional: honra.
            mutable.Add($"{BuiltinToolRegistry.IdPrefix}{Name}");
        }
        if (!canSpawn)
        {
            childToolIds = childToolIds?.Where(id =>
                    !id.Equals($"{BuiltinToolRegistry.IdPrefix}{Name}",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        return new ChildToolPlan(childToolIds, spawnDepth, canSpawn);
    }

    /// <summary>
    /// Modelo do filho — <c>model:</c> explícito &gt; <c>role:</c> (resolvido
    /// dentro do teto de tier herdado) &gt; modelo do pai.
    /// </summary>
    private static async Task<(string? Model, BuiltinToolResult? Error)>
        ResolveChildModelAsync(
            IServiceScope scope, AgentDefinition? agent, ChatRun parentRun,
            BuiltinToolContext context, int? parentMaxTier,
            CancellationToken ct)
    {
        if (agent?.Model is not { Length: > 0 } agentModel)
        {
            return (parentRun.Model, null);
        }
        if (!agentModel.StartsWith("role:", StringComparison.OrdinalIgnoreCase))
        {
            return (agentModel, null);
        }
        var resolved = await scope.ServiceProvider
            .GetRequiredService<ModelRoleResolver>()
            .ResolveForTierAsync(
                agentModel["role:".Length..], context.UserId,
                parentRun.Model, parentMaxTier, ct);
        return resolved is null
            ? (null, new BuiltinToolResult(
                $"no_model_in_tier: nenhum modelo do papel "
                + $"'{agentModel["role:".Length..]}' cabe no tier "
                + $"máximo {parentMaxTier} herdado da run pai."))
            : (resolved, null);
    }

    /// <summary>
    /// Cria o chat filho com system/user iniciais — e o junta ao team da
    /// raiz quando <c>team:true</c> (leader na primeira delegação).
    /// </summary>
    private static async Task<Chat> CreateChildChatAsync(
        ChildChatSpec s, CancellationToken ct)
    {
        var (db, context, agent, childModel, childToolIds, systemPrompt,
            preset, prompt, fullPrompt, joinTeam, now) = s;
        var title = prompt.Length > MaxTitleChars
            ? prompt[..MaxTitleChars] + "…"
            : prompt;
        var childChat = new Chat
        {
            UserId = context.UserId,
            Title = agent is null ? $"delegado: {title}" : $"agent:{agent.Name}: {title}",
            ParentChatId = context.ChatId,
            ModelsJson = JsonSerializer.Serialize(new[] { childModel }),
            ToolIdsJson = JsonSerializer.Serialize(childToolIds ?? []),
            ApprovalPreset = preset,
            // Subtarefa é workspace-capable por natureza — o modo efetivo
            // cai para chat se o filho ficar sem binding (SPEC-15).
            Mode = PermissionRuleset.BuildMode,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (systemPrompt is not null)
        {
            childChat.Messages.Add(new ChatMessage
            {
                ChatId = childChat.Id,
                Role = "system",
                Content = systemPrompt,
                Position = 0,
                Timestamp = now,
            });
        }
        childChat.Messages.Add(new ChatMessage
        {
            ChatId = childChat.Id,
            Role = "user",
            Content = fullPrompt,
            Position = systemPrompt is null ? 0 : 1,
            Timestamp = now,
        });
        db.Chats.Add(childChat);

        // RF-E1: team:true — a raiz do chamador ganha TeamId próprio +
        // leader na primeira delegação; o filho entra como member.
        if (joinTeam && context.ChatId is { } parentChatIdForTeam)
        {
            var parentChat = await db.Chats
                .FirstOrDefaultAsync(c => c.Id == parentChatIdForTeam, ct);
            if (parentChat is not null)
            {
                childChat.TeamId = await TeamService.EnsureRootTeamAsync(
                    db, parentChat, ct);
                childChat.TeamRole = "member";
            }
        }
        return childChat;
    }

    /// <summary>
    /// Persiste os constraints kv da run filha — allowlist spawns, depth
    /// residual (0 quando não pode delegar), teto de steps e tier de custo
    /// herdado (a cadeia só afunila, nunca alarga).
    /// </summary>
    private static async Task PersistChildConstraintsAsync(
        ConfigService config, AgentDefinition? agent, ChatRun childRun,
        ChildToolPlan plan, int? parentMaxTier, CancellationToken ct)
    {
        if (agent is not null && plan.CanSpawn)
        {
            await config.SetAsync(
                $"r:{childRun.Id}:agent.spawns", string.Join(",", agent.Spawns), ct);
            await config.SetAsync(
                $"r:{childRun.Id}:agent.depth", plan.SpawnDepth, ct);
        }
        else if (agent is not null)
        {
            // Filho sem capacidade de spawn: ainda marca depth 0 — se a tool
            // delegate_task vazar para ele (toolIds explicitamente pedidos),
            // o enforcement bloqueia.
            await config.SetAsync($"r:{childRun.Id}:agent.depth", 0, ct);
        }

        // Teto de steps do agente (SPEC-20261016-agent-steps-usage RF-001):
        // kv da sub-run — o executor resolve min(steps, agentLoop.maxRounds).
        if (agent?.Steps is { } agentSteps)
        {
            await config.SetAsync($"r:{childRun.Id}:agent.steps", agentSteps, ct);
        }

        // Tier de custo herdado (SPEC-20261023-run-cost-budgets RF-004):
        // o gate do executor do filho aplica min(inherited, policy) — a
        // cadeia só afunila, nunca alarga.
        if (parentMaxTier is { } inheritedTier)
        {
            await config.SetAsync(
                $"r:{childRun.Id}:agent.maxtier", inheritedTier, ct);
        }
    }

    /// <summary>
    /// Herança de workspace: binding por chat do pai vira worktree próprio
    /// do filho; pai sem repo faz o filho trabalhar em
    /// <c>scratch/{pai}/agents/{filho}</c> (SPEC-20261027).
    /// </summary>
    private static async Task InheritWorkspaceAsync(
        WorkspaceRepoService repos, BuiltinToolContext context,
        Chat childChat, CancellationToken ct)
    {
        if (context.ChatId is { } boundParent
            && await repos.GetChatBindingAsync(boundParent, ct) is { } parentBinding)
        {
            await repos.SetChatBindingAsync(
                childChat.Id, repos.InheritChatBinding(childChat.Id, parentBinding), ct);
        }
        else if (context.ChatId is { } scratchParent)
        {
            // SPEC-20261027-scratch-workspace: pai sem repo → filho trabalha
            // em scratch/{pai}/agents/{filho} — o publish do pai inclui o
            // trabalho dos subagentes.
            await repos.SetScratchParentAsync(childChat.Id, scratchParent, ct);
        }
    }

    /// <summary>
    /// Persona opcional (SPEC-20261010-delegate-persona): slug (Name ou Id)
    /// de uma Skill ativa do usuário. Erro listando as ativas quando o
    /// slug não existe.
    /// </summary>
    private static async Task<(Skill? Skill, BuiltinToolResult? Error)> ResolvePersonaAsync(
        AppDbContext db, BuiltinToolContext context, JsonElement args, CancellationToken ct)
    {
        if (ReadString(args, "persona", 200) is not { Length: > 0 } persona)
        {
            return (null, null);
        }

        var skill = await db.Skills.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == context.UserId && s.IsActive
                && (s.Name == persona || s.Id == persona), ct);
        if (skill is not null)
        {
            return (skill, null);
        }

        var available = await db.Skills.AsNoTracking()
            .Where(s => s.UserId == context.UserId && s.IsActive)
            .Select(s => s.Name).ToListAsync(ct);
        return (null, new BuiltinToolResult(
            $"Persona '{persona}' não encontrada. Skills ativas: "
            + (available.Count == 0 ? "(nenhuma)" : string.Join(", ", available))));
    }

    /// <summary>
    /// SPEC-20261010-delegate-repo-target: `repo` explícito sobrescreve a
    /// herança — o chat filho é vinculado ao repo alvo (checkout
    /// compartilhado por slug). Retorna mensagem de erro pronta ou null.
    /// </summary>
    private static async Task<string?> BindTargetRepoAsync(
        IServiceScope scope, BuiltinToolContext context, JsonElement args,
        Chat childChat, CancellationToken ct)
    {
        if (ReadString(args, "repo", 200) is not { Length: > 0 } targetRepo)
        {
            return null;
        }

        var targetBranch = ReadString(args, "branch", 200);
        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            targetBranch = "main";
        }
        var cloneUrl = ReadString(args, "clone_url", 2000);
        if (string.IsNullOrWhiteSpace(cloneUrl))
        {
            cloneUrl = $"https://github.com/{targetRepo.Trim()}.git";
        }
        var repos = scope.ServiceProvider.GetRequiredService<WorkspaceRepoService>();
        var token = await scope.ServiceProvider
            .GetRequiredService<GitHubService>()
            .GetTokenAsync(context.UserId, ct);
        var (repoBinding, repoError) = await repos.OpenChatAsync(
            context.UserId, childChat.Id, targetRepo.Trim(),
            targetBranch.Trim(), cloneUrl, token, ct);
        return repoBinding is null
            ? $"Falha ao preparar o repo '{targetRepo}' para a subtarefa: {repoError}"
            : null;
    }

    /// <summary>
    /// wait=true: faz polling da run filha até status terminal ou o
    /// timeout de <see cref="TimeoutSeconds"/> — o resultado inclui a
    /// nota de auto-merge quando habilitado.
    /// </summary>
    /// <summary>
    /// RF-E1 <c>resume_chat_id</c>: nova run num chat filho existente —
    /// subagente continuável que vê o próprio histórico. Alvo tem que ser
    /// do mesmo usuário e (quando <paramref name="joinTeam"/>) entra no
    /// team do chamador.
    /// </summary>
    private sealed record ResumeRequest(
        IServiceScope Scope, AppDbContext Db, BuiltinToolContext Context,
        ChatRun ParentRun, string ResumeChatId, string FullPrompt,
        bool Wait, bool JoinTeam);

    private async Task<BuiltinToolResult> ResumeInExistingChatAsync(
        ResumeRequest r, CancellationToken ct)
    {
        var scope = r.Scope; var db = r.Db; var context = r.Context;
        var parentRun = r.ParentRun; var resumeChatId = r.ResumeChatId;
        var fullPrompt = r.FullPrompt; var wait = r.Wait;
        var joinTeam = r.JoinTeam;
        if (resumeChatId == context.ChatId)
        {
            return new BuiltinToolResult(
                "resume_chat_id não pode ser o próprio chat da run.");
        }
        var target = await db.Chats.Include(c => c.Messages)
            .FirstOrDefaultAsync(
                c => c.Id == resumeChatId && c.UserId == context.UserId, ct);
        if (target is null)
        {
            return new BuiltinToolResult(
                $"resume_chat_id '{resumeChatId}' não encontrado.");
        }
        var hasLiveRun = await db.ChatRuns.AnyAsync(
            r => r.ChatId == target.Id
                && (r.Status == ChatRunStatus.Queued
                    || r.Status == ChatRunStatus.Running
                    || r.Status == ChatRunStatus.Paused), ct);
        if (hasLiveRun)
        {
            return new BuiltinToolResult(
                $"O chat {resumeChatId} já tem uma run ativa — envie a "
                    + "mensagem via builtin_team_message em vez de resumir.");
        }

        await JoinResumeTeamAsync(db, context, target, joinTeam, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        AppendResumeMessage(target, fullPrompt, now);

        var model = parentRun.Model;
        var toolIds = JsonSerializer.Deserialize<List<string>>(
            target.ToolIdsJson, JsonOptions);
        var childRun = new ChatRun
        {
            ChatId = target.Id,
            UserId = context.UserId,
            Model = model,
            RequestJson = JsonSerializer.Serialize(
                new ChatCompletionRequest(
                    model,
                    [new ChatCompletionMessage("user", fullPrompt)],
                    Stream: true, ToolIds: toolIds),
                JsonOptions),
            ParentRunId = parentRun.Id,
            CreatedAt = now,
        };
        db.ChatRuns.Add(childRun);
        await db.SaveChangesAsync(ct);
        dispatcher.Enqueue(childRun.Id);

        var link = $"/c/{target.Id}";
        if (!wait)
        {
            return new BuiltinToolResult(
                $"Subagente retomado em background — run {childRun.Id} no "
                    + $"chat {link}. Chame builtin_run_result com "
                    + $"run_id={childRun.Id} para colher o resultado.",
                new
                {
                    childChatId = target.Id,
                    childRunId = childRun.Id,
                    status = "queued",
                });
        }
        return await WaitForChildAsync(
            new ChildWait(db, scope, context, target, childRun,
                AutoMerge: false, link), ct);
    }

    /// <summary>
    /// Team do resume: junta ao team da raiz do chamador quando pedido ou
    /// mantém o TeamId existente — o alvo entra como member.
    /// </summary>
    private static async Task JoinResumeTeamAsync(
        AppDbContext db, BuiltinToolContext context, Chat target,
        bool joinTeam, CancellationToken ct)
    {
        if (!joinTeam && target.TeamId is null)
        {
            return;
        }
        var teamId = target.TeamId;
        if (joinTeam && teamId is null && context.ChatId is { } pcid)
        {
            var parentChat = await db.Chats
                .FirstOrDefaultAsync(c => c.Id == pcid, ct);
            if (parentChat is not null)
            {
                teamId = await TeamService.EnsureRootTeamAsync(
                    db, parentChat, ct);
            }
        }
        if (teamId is not null && target.TeamId is null)
        {
            target.TeamId = teamId;
            target.TeamRole = "member";
        }
    }

    /// <summary>Append da mensagem de resume no topo da árvore do alvo.</summary>
    private static void AppendResumeMessage(
        Chat target, string fullPrompt, long now)
    {
        var userMsg = new ChatMessage
        {
            ChatId = target.Id,
            Role = "user",
            Content = fullPrompt,
            Position = target.Messages.Count == 0
                ? 0 : target.Messages.Max(m => m.Position) + 1,
            Timestamp = now,
            ParentId = BranchService.ResolveLeaf(target)?.Id,
        };
        target.Messages.Add(userMsg);
        target.LeafMessageId = userMsg.Id;
        target.UpdatedAt = now;
    }

    private sealed record ChildWait(
        AppDbContext Db, IServiceScope Scope, BuiltinToolContext Context,
        Chat ChildChat, ChatRun ChildRun, bool AutoMerge, string Link);

    private static async Task<BuiltinToolResult> WaitForChildAsync(
        ChildWait w, CancellationToken ct)
    {
        var db = w.Db; var scope = w.Scope; var context = w.Context;
        var childChat = w.ChildChat; var childRun = w.ChildRun;
        var autoMerge = w.AutoMerge; var link = w.Link;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(TimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollMs, ct);
            var status = await db.ChatRuns.AsNoTracking()
                .Where(r => r.Id == childRun.Id)
                .Select(r => new { r.Status, r.PartialContent, r.Error })
                .FirstOrDefaultAsync(ct);
            if (status is null || status.Status
                    is ChatRunStatus.Queued or ChatRunStatus.Running or ChatRunStatus.Paused)
            {
                continue;
            }

            var result = new
            {
                childChatId = childChat.Id,
                childRunId = childRun.Id,
                status = status.Status,
            };
            return status.Status switch
            {
                ChatRunStatus.Completed => new BuiltinToolResult(
                    $"{Truncate(status.PartialContent ?? "(sem conteúdo)", MaxResultChars)}\n\n"
                        + $"(subtarefa concluída — chat filho: {link})"
                        + await AutoMergeNoteAsync(
                            autoMerge, scope, context, childChat, childRun, ct),
                    result),
                _ => new BuiltinToolResult(
                    $"Subtarefa terminou como '{status.Status}'"
                        + (status.Error is { } e ? $": {Truncate(e, 500)}" : string.Empty)
                        + $" — chat filho: {link}",
                    result),
            };
        }

        return new BuiltinToolResult(
            $"Subtarefa ainda em andamento após {TimeoutSeconds}s — continua em "
                + $"background; acompanhe o resultado no chat filho: {link}",
            new { childChatId = childChat.Id, childRunId = childRun.Id, status = "running" });
    }

    /// <summary>
    /// Profundidade da run na cadeia <see cref="ChatRun.ParentRunId"/>
    /// (0 = raiz). Limita o walk a <see cref="MaxDepth"/> — ciclos ou
    /// correntes longas não custam mais que MaxDepth lookups.
    /// </summary>
    private static async Task<int> DepthOfAsync(
        AppDbContext db, ChatRun run, CancellationToken ct)
    {
        var depth = 0;
        var cursor = run.ParentRunId;
        while (cursor is not null && depth < MaxDepth)
        {
            depth++;
            cursor = await db.ChatRuns.AsNoTracking()
                .Where(r => r.Id == cursor)
                .Select(r => r.ParentRunId)
                .FirstOrDefaultAsync(ct);
        }
        return depth;
    }

    /// <summary>
    /// Auto-merge opt-in (SPEC-20261010-worktree-review, <c>merge=true</c>
    /// + <c>wait=true</c>): depois da run filha completar, aplica o diff do
    /// worktree dela no workdir do chat filho. Conflitos nunca são
    /// forçados — voltam listados na nota e o worktree permanece.
    /// </summary>
    private static async Task<string> AutoMergeNoteAsync(
        bool enabled,
        IServiceScope scope,
        BuiltinToolContext context,
        Chat childChat,
        ChatRun childRun,
        CancellationToken ct)
    {
        if (!enabled)
        {
            return string.Empty;
        }
        try
        {
            var worktrees = scope.ServiceProvider
                .GetRequiredService<WorktreeService>();
            var worktree = worktrees.ResolveIsolated(context.UserId, childRun.Id);
            if (worktree is null)
            {
                return "\n\n(merge: a run filha não isolou worktree — nada a aplicar)";
            }
            var repos = scope.ServiceProvider
                .GetRequiredService<WorkspaceRepoService>();
            var mainWorkdir = await repos.ResolveWorkdirAsync(
                context.UserId, childChat.Id, ct);
            var merge = await worktrees.MergeAsync(mainWorkdir, worktree, ct);
            return merge.Merged
                ? $"\n\n(merge: {merge.Applied.Count} arquivo(s) aplicados no workspace)"
                : $"\n\n(merge: PARCIAL — {merge.Conflicts.Count} conflito(s) não "
                    + "aplicados; worktree preservado p/ correção manual: "
                    + string.Join("; ", merge.Conflicts) + ")";
        }
        catch (Exception ex)
        {
            return $"\n\n(merge: falhou — {ex.Message}; worktree preservado)";
        }
    }

    /// <summary>
    /// RF-001 `skills`: system prompt do agente = corpo do arquivo +
    /// conteúdo das skills pessoais ativas cujos slugs estão em
    /// <see cref="AgentDefinition.Skills"/> (mesma resolução do persona —
    /// Skill por Name ou Id, IsActive).
    /// </summary>
    private static async Task<string> ComposeSystemPromptAsync(
        AppDbContext db, BuiltinToolContext context, AgentDefinition agent,
        CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder(agent.SystemPrompt);
        if (agent.Skills.Count > 0)
        {
            var userSkills = await db.Skills.AsNoTracking()
                .Where(s => s.UserId == context.UserId && s.IsActive)
                .ToListAsync(ct);
            var skills = userSkills
                .Where(s => agent.Skills.Contains(s.Name, StringComparer.OrdinalIgnoreCase)
                    || agent.Skills.Contains(s.Id, StringComparer.OrdinalIgnoreCase))
                .OrderBy(s => s.Name, StringComparer.Ordinal);
            foreach (var skill in skills)
            {
                sb.Append("\n\n<skill name=\"").Append(skill.Name).Append("\">\n")
                    .Append(skill.Content).Append("\n</skill>");
            }
        }
        return sb.ToString();
    }

    /// <summary>Nome sem o prefixo <c>builtin:</c>/<c>builtin_</c> do id.</summary>
    private static string StripPrefix(string toolId)
    {
        return PermissionRuleset.BuiltinName(toolId);
    }

    private static string? ReadString(JsonElement args, string field, int max)
    {
        if (!args.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.String
            || el.GetString() is not { } s)
        {
            return null;
        }
        return s.Length > max ? s[..max] : s;
    }

    /// <summary>agent:"ext:{id}" — executa o CLI externo headless
    /// confinado ao workdir (RF-005 external-agent-registry).</summary>
    private static async Task<BuiltinToolResult> RunExternalAgentAsync(
        IServiceScope scope, string extId, string fullPrompt,
        BuiltinToolContext context, CancellationToken ct)
    {
        var external = scope.ServiceProvider
            .GetRequiredService<ExternalAgentService>();
        var extResult = await external.RunDelegateAsync(
            extId, fullPrompt, context.WorkspacePath, context.UserId, ct);
        return new BuiltinToolResult(extResult.Result);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
}
