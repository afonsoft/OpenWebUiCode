using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Completions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Runs;

/// <summary>
/// Executa uma run desacoplada (SPEC-20261007-chat-detached-runs): roda o
/// mesmo pipeline de <c>/api/chat/completions</c> (enriquecimento, arena,
/// pipelines, tools, stream) publicando os eventos no
/// <see cref="ChatRunBroadcaster"/> e persistindo checkpoints por iteração
/// em <see cref="ChatRun.PartialContent"/>. Ao final grava a mensagem do
/// assistant no chat — a persistência deixa de depender do cliente.
/// </summary>
public sealed class ChatRunExecutor(
    AppDbContext db,
    ConfigService config,
    ProviderService providers,
    RagService rag,
    ToolExecutor toolExecutor,
    PipelineClientService pipelines,
    WebSearchService webSearch,
    WorkspaceRepoService repos,
    WorktreeService worktrees,
    BuiltinToolRegistry builtins,
    ToolApprovalOverrideService toolOverrides,
    ChatRunBroadcaster broadcaster,
    ChatRunApprovals approvals,
    ChatRunPauses pauses,
    CheckpointService checkpoints,
    ContextCompactionService compaction,
    ContextBoundaryService boundaries,
    RunAdvisorService advisor,
    McpClientService mcp,
    ComboService combos,
    RunEventWriter runEvents,
    IServiceScopeFactory scopes,
    IWebHostEnvironment env,
    ILogger<ChatRunExecutor> logger)
{
    /// <summary>Bytes de delta acumulados antes de gravar um checkpoint.</summary>
    private const int CheckpointBytes = 2048;

    /// <summary>Tamanho máximo do preview de args/resultado publicado no SSE.</summary>
    private const int PreviewChars = 2048;

    /// <summary>Tamanho máximo de um diff publicado no evento <c>changes</c>.</summary>
    private const int ChangeDiffChars = 8192;

    /// <summary>
    /// Arquivos alterados pela run (RF-015): acumulado por path a cada
    /// file_write/file_edit — alimenta a aba Changes do painel.
    /// </summary>
    private readonly Dictionary<string, RunChangeItem> _changes = new();

    /// <summary>
    /// Notas system pendentes → próximo round (conselhos do advisor +
    /// confirmação de compactação forçada por <c>new_context</c>).
    /// </summary>
    private readonly Queue<ChatCompletionMessage> _contextNotes = new();

    /// <summary>"nome → preview" das últimas tool calls (contexto do advisor).</summary>
    private readonly Queue<string> _recentTools = new();

    /// <summary>Conselhos já disparados nesta run (cap do RF-001).</summary>
    private int _advisorCount;

    /// <summary>Tool results desde o último burst de revisão (gatilho N=5).</summary>
    private int _toolResultsSinceAdvise;

    /// <summary>Revisão em voo — no máx. uma por vez (RF-001).</summary>
    private volatile bool _advisorInFlight;

    /// <summary>Último disparo (cooldown do RF-001).</summary>
    private DateTimeOffset _advisorLastAt = DateTimeOffset.MinValue;

    /// <summary>Roda a run até o fim e atualiza o registro com o resultado.</summary>
    public async Task ExecuteAsync(ChatRun run, CancellationToken ct)
    {
        ChatCompletionRequest request;
        try
        {
            request = JsonSerializer.Deserialize<ChatCompletionRequest>(
                run.RequestJson, JsonOptions)!;
        }
        catch (JsonException ex)
        {
            await FailAsync(run, $"Request de run inválido: {ex.Message}", ct);
            return;
        }

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == run.UserId, ct);
        if (user is null)
        {
            await FailAsync(run, "Usuário da run não encontrado.", ct);
            return;
        }

        // SPEC-20261016-run-recovery RF-003/004: run reivindicada pelo sweep
        // com eventos duráveis → replay do último snapshot + delta. Sem
        // snapshot válido o plano é null e a execução segue como nova.
        var storedEvents = await db.ChatRunEvents.AsNoTracking()
            .Where(e => e.RunId == run.Id)
            .OrderBy(e => e.Seq)
            .ToListAsync(ct);
        var resumePlan = storedEvents.Count > 0
            ? RunRecoveryPlanner.Replay(storedEvents)
            : null;
        if (resumePlan is null
            && storedEvents.Any(e => e.Type == RunEventWriter.Types.Snapshot))
        {
            // RF-003 fallback: snapshot durável existe mas não desserializa
            // — estado corrompido; interrompe em vez de re-executar sobre
            // um histórico que não dá para reconstruir.
            run.Status = ChatRunStatus.Interrupted;
            run.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            run.Error ??= "Estado de retomada corrompido (snapshot ilegível).";
            await db.SaveChangesAsync(CancellationToken.None);
            broadcaster.Publish(run.Id,
                $"data: {JsonSerializer.Serialize(new { error = run.Error })}");
            broadcaster.Publish(run.Id, "data: [DONE]");
            return;
        }

        var content = new StringBuilder();
        var sinceCheckpoint = 0;
        IReadOnlyList<ChatCompletionMessage>? toolMessages = null;
        var sw = Stopwatch.StartNew();

        // combo:{id|nome} (SPEC-20261016-model-combos RF-003): cada tentativa
        // resolve o modelo efetivo; falha transitória ANTES do primeiro delta
        // (content vazio ⇒ nenhum SSE parcial foi emitido) → report + próximo
        // candidato. Cancelamento nunca queima cooldown.
        var comboRef = request.Model.StartsWith(ComboService.Prefix, StringComparison.Ordinal)
            ? request.Model[ComboService.Prefix.Length..]
            : null;
        var comboAttemptsLeft = 1;
    combo_resolve:
        if (comboRef is not null)
        {
            ComboPick pick;
            try
            {
                pick = await combos.ResolveAsync(comboRef, run.UserId, ct);
            }
            catch (ComboUnavailableException cue)
            {
                var comboError = cue.RetryAt is { } retryAt
                    ? $"{cue.Code} (retry após {retryAt})"
                    : cue.Code;
                broadcaster.Publish(run.Id,
                    $"data: {JsonSerializer.Serialize(new { error = comboError })}");
                await FailAsync(run, comboError, ct);
                broadcaster.Publish(run.Id, "data: [DONE]");
                return;
            }

            comboAttemptsLeft = Math.Max(1, pick.Candidates);
            request = request with { Model = pick.ModelId };
            broadcaster.Publish(run.Id,
                $"event: combo\ndata: {JsonSerializer.Serialize(new { combo = pick.ComboId, model = pick.ModelId }, JsonOptions)}");
        }

        try
        {
            // Modelos pipeline:{id} → roteia ao servidor de pipelines e repassa as linhas.
            if (request.Model.StartsWith("pipeline:", StringComparison.Ordinal))
            {
                await RunPipelineAsync(request, run, ct);
                // [DONE] só depois do FinishAsync — ver caminho principal.
                await FinishAsync(run, string.Empty, ct);
                broadcaster.Publish(run.Id, "data: [DONE]");
                return;
            }

            // Arena gera as duas respostas e publica o payload {"arena":...}.
            var arenaModel = request.Model.StartsWith("arena:", StringComparison.Ordinal)
                ? request.Model["arena:".Length..]
                : request.Model;
            if (await ChatPipeline.TryRunArenaAsync(
                request, arenaModel, user, db, config, rag, providers, webSearch, repos,
                line =>
                {
                    // O [DONE] do arena é retido: sai depois do FinishAsync.
                    if (line != "data: [DONE]")
                    {
                        broadcaster.Publish(run.Id, line);
                    }
                    return Task.CompletedTask;
                },
                ct))
            {
                await FinishAsync(run, string.Empty, ct);
                broadcaster.Publish(run.Id, "data: [DONE]");
                return;
            }

            // SPEC-20261015 RF-005: telemetria de progresso nas etapas
            // pré-provider — antes a run ficava muda até o primeiro token.
            // Best-effort: uma falha de publish nunca derruba a run.
            await PublishPhaseAsync(run, "preparing_context", null);

            // Compactação de contexto (SPEC-20261010-context-compaction): aplica
            // o resumo persistido, compacta o head se o histórico estourou o
            // limiar e re-aplica — a janela ao modelo sai [resumo] + tail.
            await compaction.MaybeCompactAsync(
                run.ChatId, request.Messages, request.Model, ct, run.UserId);
            var compacted = await compaction.ApplyAsync(
                run.ChatId, request.Messages, ct);
            if (compacted.Count != request.Messages.Count)
            {
                await PublishPhaseAsync(run, "compacting", null);
                request = request with { Messages = compacted };
            }

            // Modo do agente do chat (SPEC-20261009 RF-001): plan/build
            // guia o prompt e o spec de tools anunciado. SPEC-15: build
            // sem repositório vinculado cai para chat.
            var chatMode = await EffectiveModeAsync(run.ChatId, ct);

            // Busca web/arquivos anunciados antes do enrich — o usuário vê
            // "Pesquisando na web…" em vez de uma tela parada.
            if (request.WebSearch == true)
            {
                await PublishPhaseAsync(
                    run, "searching_web",
                    Scrub(Truncate(
                        request.Messages.LastOrDefault(m => m.Role == "user")
                            ?.Content ?? string.Empty, 60)));
            }

            // Memória durável é consultada dentro do enrich quando o
            // memsync está ligado — o gate espelha o do ChatPipeline
            // (global memsync.enabled + override u:{user}) para o chip
            // "Consultando memória…" não aparecer quando está desligado.
            var memAdminOff = await config.GetAsync<bool?>(
                "memsync.enabled", null, ct) == false;
            var memOn = !memAdminOff
                && (await config.GetAsync<bool?>(
                        $"u:{user.Id}:memsync.enabled", null, ct)
                    ?? await config.GetAsync("memsync.enabled", true, ct));
            if (memOn)
            {
                await PublishPhaseAsync(run, "consulting_memory", null);
            }

            var effective = await ChatPipeline.EnrichRequestAsync(
                request, user, db, config, rag, webSearch, repos, ct, chatMode,
                run.ChatId, logger);

            // Notebook persistente do chat (SPEC-20261013-agent-context-
            // management RF-003): injetado como system na entrada de toda
            // run — sobrevive a compactação, reload e troca de provider.
            var contextNotebook = await boundaries.GetNotesAsync(run.ChatId, ct);
            if (!string.IsNullOrWhiteSpace(contextNotebook))
            {
                effective = effective with
                {
                    Messages = [new ChatCompletionMessage(
                        "system", $"[context notes]\n{contextNotebook}"), .. effective.Messages],
                };
            }

            RunLoopCheckpoint? resumeSeed = null;
            if (resumePlan is not null)
            {
                // RF-003/006 run-recovery: a janela reconstruída (base do
                // snapshot + delta pós-snapshot + resultados sintéticos das
                // calls órfãs) vira o histórico da run retomada; a nota de
                // sistema fica visível para o modelo e a fase resumed
                // reporta round/recuperados no SSE.
                resumePlan.Messages.Add(new ChatCompletionMessage(
                    "system",
                    $"[run resumed after restart — {resumePlan.RecoveredTools} "
                    + "tool results recovered]"));
                effective = effective with { Messages = resumePlan.Messages };
                resumeSeed = new RunLoopCheckpoint(
                    resumePlan.Round, resumePlan.Messages,
                    resumePlan.ToolMessages, resumePlan.StepCount,
                    resumePlan.StepUsages, resumePlan.EmptyRounds,
                    resumePlan.LengthContinuations,
                    resumePlan.ContinuedAnswer);
                await BumpResumeMetaAsync(run, resumePlan.LastSeq, ct);
                await runEvents.AppendAsync(run.Id, RunEventWriter.Types.Note,
                    new
                    {
                        note = $"run resumed after restart — "
                            + $"{resumePlan.RecoveredTools} tool results recovered",
                        round = resumePlan.Round,
                        recovered = resumePlan.RecoveredTools,
                    }, ct);
                await PublishPhaseAsync(run, "resumed",
                    $"round {resumePlan.Round} — {resumePlan.RecoveredTools} "
                    + "tool results recuperados");
            }
            else
            {
                // Snapshot inicial (RF-002): toda run ativa ganha base de
                // retomada — morte antes do primeiro round do loop ainda
                // re-executa a partir do request enriquecido.
                await runEvents.AppendAsync(run.Id, RunEventWriter.Types.Snapshot,
                    RunRecoveryPlanner.SerializeCheckpoint(new RunLoopCheckpoint(
                        0, effective.Messages, [], 0, [], 0, 0, "")), ct);
            }

            var outletRules = ModelFilterService.OutletRules(
                ModelFilterService.Parse(await db.ModelEntries.AsNoTracking()
                    .Where(m => m.IsActive && (m.Id == request.Model || m.Name == request.Model)
                        && (m.UserId == user.Id || m.UserId == "public"))
                    .Select(m => m.MetaJson).FirstOrDefaultAsync(ct)));

            if (request.ToolIds is { Count: > 0 })
            {
                await PublishPhaseAsync(run, "loading_tools", null);

                // .agents/mcp.json (global + projeto) materializa servers
                // env:* no boot da run (SPEC-20261016-global-agents-scope
                // RF-006). Falha de sync nunca quebra a run.
                try
                {
                    await mcp.SyncEnvServersAsync(
                        await repos.ResolveWorkdirAsync(user.Id, run.ChatId, ct), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Run {RunId}: sync de .agents/mcp.json falhou — ignorado.", run.Id);
                }
            }
            var tools = request.ToolIds is { Count: > 0 }
                ? await toolExecutor.LoadEnabledAsync(user.Id, request.ToolIds, ct)
                : null;
            await PublishPhaseAsync(run, "connecting_model", null);
            logger.LogInformation(
                "Run {RunId}: model={Model}, tools={ToolCount}, mensagens={MessageCount}, chat={ChatId}.",
                run.Id, effective.Model, tools?.Count ?? 0,
                effective.Messages.Count, run.ChatId);
            if (tools is { Count: > 0 })
            {
                var builtinContext = await BuildToolContextAsync(run, user, ct);
                // Advisor (SPEC-20261013-run-advisor): a entrada da mensagem
                // do usuário dispara a primeira revisão — em background,
                // com o subset read-only do workspace disponível.
                await KickAdvisorAsync(run, "user_message", builtinContext, ct);

                // Checkpoints do workdir (SPEC-20261009-checkpoints-revert):
                // turn 0 = estado pré-run; cada tool mutável depois — o
                // evento `checkpoint` alimenta o Revert da aba Changes.
                var checkpointTurn = 0;
                await PublishCheckpointAsync(
                    run, builtinContext.WorkspacePath, checkpointTurn, ct);
                // plan_exit entra no catálogo sintético da run (RF-003): só
                // é anunciado em modo plan; em build fica invisível — o gate
                // o conhece para interceptar a call se ela escapar.
                if (tools.All(t => t.Url != BuiltinToolRegistry.UrlPrefix + "plan_exit")
                    && builtins.All.FirstOrDefault(t => t.Name == "plan_exit") is { } planExit)
                {
                    tools.Add(builtins.ToSyntheticTool(planExit));
                }
                // Spec da primeira rodada já filtrado pelo modo (RF-002);
                // as próximas passam pelo AnnounceToolsAsync do callback.
                effective = effective with
                {
                    Tools = tools
                        .Where(t => PermissionRuleset.ShouldAnnounce(chatMode, t))
                        .Select(t => JsonSerializer.Deserialize<JsonElement>(t.SpecJson))
                        .ToList(),
                };
                // Teto de steps da run (SPEC-20261016-agent-steps-usage
                // RF-001): min(agent.steps do kv da sub-run,
                // agentLoop.maxRounds global — default 25).
                var agentSteps = await config.GetAsync<int?>(
                    $"r:{run.Id}:agent.steps", null, ct);
                var maxSteps = Math.Min(agentSteps ?? int.MaxValue,
                    await config.GetAsync("agentLoop.maxRounds", 25, ct));
                var currentStep = 0;
                var currentMaxSteps = 0;
                var callbacks = new ChatPipeline.ToolLoopCallbacks(
                    // Checkpoint de pausa em cada iteração do loop (RF-013):
                    // fase nova só começa depois do resume.
                    OnPhaseAsync: async (phase, label, t) =>
                    {
                        await pauses.WaitIfPausedAsync(run.Id, t);
                        // generating carrega {step,maxSteps} (RF-006).
                        await PublishPhaseAsync(run, phase, label,
                            phase == "generating" ? currentStep : null,
                            phase == "generating" ? currentMaxSteps : null);
                    },
                    OnStepAsync: async (step, max, t) =>
                    {
                        currentStep = step;
                        currentMaxSteps = max;
                        await runEvents.AppendAsync(run.Id,
                            RunEventWriter.Types.StepStarted,
                            new { round = step, maxSteps = max }, t);
                    },
                    // RF-002 provider-resilience: fase "retrying" com
                    // tentativa/atraso/motivo + backoff que respeita pausa —
                    // a chamada ao provider só sai depois do resume.
                    OnRetryingAsync: async (attempt, delay, reason, t) =>
                    {
                        await pauses.WaitIfPausedAsync(run.Id, t);
                        await PublishPhaseAsync(run, "retrying",
                            $"{reason} (tentativa {attempt}, {Math.Max(1, (int)delay.TotalMilliseconds)}ms)");
                        await Task.Delay(delay, t);
                        await pauses.WaitIfPausedAsync(run.Id, t);
                    },
                    // RF-004 provider-resilience: rejeição de janela cheia
                    // → compacta o histórico do loop com tail progressivo e
                    // devolve a janela reduzida; null = sem como reduzir.
                    OnOverflowCompactAsync: async (history, tailScale, t) =>
                    {
                        var didCompact = await compaction.MaybeCompactAsync(
                            run.ChatId, history, effective.Model, t, run.UserId,
                            force: true, tailScale: tailScale);
                        if (!didCompact)
                        {
                            return null;
                        }
                        await PublishPhaseAsync(run, "compacting", null);
                        return await compaction.ApplyAsync(run.ChatId, history, t);
                    },
                    OnCallAsync: async (call, t) =>
                    {
                        await PublishToolCallAsync(run, call);
                        await runEvents.AppendAsync(run.Id,
                            RunEventWriter.Types.ToolCall,
                            new
                            {
                                id = call.Id,
                                name = call.Name,
                                args = Scrub(Truncate(
                                    call.ArgumentsJson ?? "", PreviewChars)),
                            }, t);
                    },
                    // RF-002 run-recovery: estado do loop por fronteira.
                    OnCheckpointAsync: (checkpoint, t) =>
                        runEvents.AppendAsync(run.Id,
                            RunEventWriter.Types.Snapshot,
                            RunRecoveryPlanner.SerializeCheckpoint(checkpoint), t),
                    // RF-001/004: ids das calls do round — base do settle
                    // de órfãs na retomada.
                    OnStepFinishedAsync: (round, step, t) =>
                        runEvents.AppendAsync(run.Id,
                            RunEventWriter.Types.StepFinished,
                            new
                            {
                                round,
                                calls = step.ToolCalls.Select(c => c.Id),
                            }, t),
                    OnMessagesAddedAsync: (added, toolAdded, t) =>
                        runEvents.AppendAsync(run.Id,
                            RunEventWriter.Types.MessagesAdded,
                            new { messages = added, toolMessages = toolAdded }, t),
                    GateAsync: (call, t) => GateToolCallAsync(
                        run, tools, call, builtinContext, t),
                    // Modo relido por rodada — o plan_exit aprovado
                    // promove a build já na próxima chamada do modelo.
                    AnnounceToolsAsync: async t =>
                    {
                        var mode = await EffectiveModeAsync(run.ChatId, t);
                        return tools
                            .Where(tool => PermissionRuleset.ShouldAnnounce(mode, tool))
                            .Select(tool => JsonSerializer.Deserialize<JsonElement>(tool.SpecJson))
                            .ToList();
                    },
                    // Steer/queue (SPEC-20261010): steer na fronteira de
                    // rodada; queued só quando a run ficaria ociosa.
                    OnRoundBoundaryAsync: (queuedOnly, t) =>
                        PollSteerAsync(run, queuedOnly, t),
                    // Conselhos do advisor viram notas system no round
                    // seguinte — sem zerar o teto de iterações.
                    OnContextNotesAsync: t => DrainContextNotesAsync(run, t),
                    OnResultAsync: async (call, output, result, denied, t) =>
                    {
                        await PublishToolResultAsync(run, call, output, result, denied);
                        // RF-001: tool_result é o fato que liquida a call na
                        // retomada — gravação mandatória (SPEC run-recovery).
                        await runEvents.AppendAsync(run.Id,
                            RunEventWriter.Types.ToolResult,
                            new
                            {
                                callId = call.Id,
                                output,
                                denied,
                            }, t);
                        lock (_recentTools)
                        {
                            _recentTools.Enqueue(
                                $"{call.Name} → {Scrub(Truncate(output, 120)).ReplaceLineEndings(" ")}");
                            while (_recentTools.Count > 8)
                            {
                                _recentTools.Dequeue();
                            }
                        }
                        // Burst de tools (RF-001): a cada 5 resultados uma
                        // revisão — no máx. 1 por burst, cap/cooldown no kick.
                        if (++_toolResultsSinceAdvise >= 5)
                        {
                            _toolResultsSinceAdvise = 0;
                            await KickAdvisorAsync(run, "tool_burst", builtinContext, t);
                        }
                        if (!denied && ToolExecutor.FindTool(tools, call.Name) is { } tool
                            && (ToolCallRiskClassifier.IsFileMutation(
                                    tool, call.ArgumentsJson, builtinContext.WorkspacePath)
                                || IsApplyPatch(call.Name)))
                        {
                            checkpointTurn++;
                            await PublishCheckpointAsync(
                                run, builtinContext.WorkspacePath, checkpointTurn, t);
                        }
                    },
                    // Texto do modelo sai delta a delta — sem isso a resposta
                    // inteira só aparecia num blob no fim da run.
                    OnDeltaAsync: async (piece, t) =>
                    {
                        await pauses.WaitIfPausedAsync(run.Id, t);
                        var processed = piece;
                        foreach (var (regex, replacement) in outletRules)
                        {
                            processed = regex.Replace(processed, replacement);
                        }
                        if (processed.Length == 0)
                        {
                            return;
                        }
                        var chunk = JsonSerializer.Serialize(new
                        {
                            choices = new[] { new { index = 0, delta = new { content = processed } } },
                        });
                        broadcaster.Publish(run.Id, $"data: {chunk}");
                        content.Append(processed);
                        sinceCheckpoint += processed.Length;
                        if (sinceCheckpoint >= CheckpointBytes)
                        {
                            run.PartialContent = content.ToString();
                            await db.SaveChangesAsync(CancellationToken.None);
                            await runEvents.AppendAsync(run.Id,
                                RunEventWriter.Types.ContentDelta,
                                new { content = content.ToString() },
                                CancellationToken.None);
                            sinceCheckpoint = 0;
                        }
                    },
                    // SPEC-20261015 RF-002: raciocínio/thinking sai como
                    // delta.reasoning — canal separado do content (a UI
                    // renderiza colapsado; nunca entra em PartialContent).
                    // Outlet rules aplicam igual — segredos no pensamento
                    // são scrubados do mesmo jeito.
                    OnReasoningAsync: async (piece, t) =>
                    {
                        await pauses.WaitIfPausedAsync(run.Id, t);
                        var processed = piece;
                        foreach (var (regex, replacement) in outletRules)
                        {
                            processed = regex.Replace(processed, replacement);
                        }
                        if (processed.Length == 0)
                        {
                            return;
                        }
                        var chunk = JsonSerializer.Serialize(new
                        {
                            choices = new[] { new { index = 0, delta = new { reasoning = processed } } },
                        });
                        broadcaster.Publish(run.Id, $"data: {chunk}");
                    });
                var outcome = await ChatPipeline.RunToolLoopAsync(
                    effective, tools, toolExecutor, providers, ct, callbacks,
                    builtinContext, run.UserId, logger, maxSteps,
                    toolExecutor.MaxParallel, resumeSeed);
                toolMessages = outcome?.ToolMessages;
                if (outcome is not null)
                {
                    await ApplyUsageMetaAsync(run, outcome, ct);
                }
            }
            else
            {
                // Sem tool loop o conselho só sai como evento — não há
                // fronteira de rodada para a nota de contexto.
                await KickAdvisorAsync(run, "user_message", null, ct);
                // RF-005: a run sem tools não emitia nenhuma fase — a UI
                // ficava no "Pensando…" estático até o primeiro token.
                await PublishPhaseAsync(run, "generating", null);
                var answered = false;
                // RF-001 provider-resilience: o stream do chat sem tools
                // também retenta falhas transitórias — enquanto nenhum delta
                // de conteúdo saiu (content vazio garante idempotência do
                // recomeço; depois do primeiro delta a run falha como antes).
                var plainAttempt = 0;
                var plainTimeouts = 0;
                while (true)
                {
                    try
                    {
                        await foreach (var line in providers.StreamCompletionAsync(
                            effective, run.UserId, ct))
                        {
                            await pauses.WaitIfPausedAsync(run.Id, ct);
                            var processed = ModelFilterService.ProcessSseLine(line, outletRules);
                            broadcaster.Publish(run.Id, processed);
                            AccumulateDelta(processed, content);
                            // "answering" no 1º delta de conteúdo real — reasoning
                            // não conta (AccumulateDelta só anexa delta.content).
                            if (!answered && content.Length > 0)
                            {
                                answered = true;
                                await PublishPhaseAsync(run, "answering", null);
                            }
                            sinceCheckpoint += processed.Length;
                            if (sinceCheckpoint >= CheckpointBytes)
                            {
                                run.PartialContent = content.ToString();
                                await db.SaveChangesAsync(CancellationToken.None);
                                await runEvents.AppendAsync(run.Id,
                                    RunEventWriter.Types.ContentDelta,
                                    new { content = content.ToString() },
                                    CancellationToken.None);
                                sinceCheckpoint = 0;
                            }
                        }
                        break;
                    }
                    catch (Exception ex)
                        when (!(ex is OperationCanceledException && ct.IsCancellationRequested)
                            && content.Length == 0)
                    {
                        plainTimeouts = ex is TaskCanceledException or TimeoutException
                            ? plainTimeouts + 1 : 0;
                        var decision = ProviderRetryPolicy.Decide(
                            ex, plainAttempt, plainTimeouts,
                            options: providers.RetryOptions);
                        if (!decision.Retry)
                        {
                            throw;
                        }
                        plainAttempt++;
                        await pauses.WaitIfPausedAsync(run.Id, ct);
                        await PublishPhaseAsync(run, "retrying",
                            $"{decision.Reason} (tentativa {plainAttempt}, "
                            + $"{Math.Max(1, (int)decision.Delay.TotalMilliseconds)}ms)");
                        await Task.Delay(decision.Delay, ct);
                        await pauses.WaitIfPausedAsync(run.Id, ct);
                    }
                }
            }

            // [DONE] depois do FinishAsync: o cliente recarrega o chat ao ver o
            // marcador — a mensagem precisa já estar persistida.
            await FinishAsync(run, content.ToString(), ct, toolMessages: toolMessages);
            broadcaster.Publish(run.Id, "data: [DONE]");
            if (comboRef is not null)
            {
                await combos.ReportAsync(comboRef, request.Model, ok: true,
                    ct: CancellationToken.None);
            }
            logger.LogInformation(
                "Run {RunId} concluída em {Elapsed}ms: {Chars} chars, {ToolMessages} msgs de tool.",
                run.Id, sw.ElapsedMilliseconds, content.Length,
                toolMessages?.Count ?? 0);
        }
        catch (OperationCanceledException)
        {
            approvals.Cancel(run.Id);
            await FinishAsync(run, content.ToString(), ct, ChatRunStatus.Stopped, toolMessages);
            broadcaster.Publish(run.Id, "data: [DONE]");
            logger.LogWarning(
                "Run {RunId} cancelada após {Elapsed}ms com {Chars} chars parciais.",
                run.Id, sw.ElapsedMilliseconds, content.Length);
        }
        catch (Exception ex)
        {
            // Combo: falha transitória sem nenhum delta emitido → report e
            // retenta com o próximo candidato (content vazio garante que o
            // cliente ainda não viu resposta parcial).
            if (comboRef is not null
                && ex is (HttpRequestException or TaskCanceledException)
                && content.Length == 0)
            {
                await combos.ReportAsync(comboRef, request.Model, ok: false,
                    ct: CancellationToken.None);
                if (--comboAttemptsLeft > 0)
                {
                    logger.LogWarning(
                        "Run {RunId}: modelo {Model} do combo {Combo} falhou — próximo candidato.",
                        run.Id, request.Model, comboRef);
                    goto combo_resolve;
                }
            }

            // Qualquer falha fecha a run como failed — uma exceção que escapasse
            // deixaria a run eternamente "running" (o dispatcher só loga).
            // InvalidOperationException loga com stack: foi a silenciar
            // exatamente ela que o bug dos args double-encoded ficou
            // invisível nos logs por dias.
            if (ex is not (HttpRequestException or TaskCanceledException))
            {
                logger.LogError(ex,
                    "Run {RunId} falhou com exceção inesperada após {Elapsed}ms.",
                    run.Id, sw.ElapsedMilliseconds);
            }

            var message = ex is HttpRequestException or TaskCanceledException
                ? $"Falha ao contactar o provedor: {ex.Message}"
                : ex.Message;
            approvals.Cancel(run.Id);
            broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error = message })}");
            run.Error = message;
            await FinishAsync(run, content.ToString(), ct, ChatRunStatus.Failed, toolMessages);
            broadcaster.Publish(run.Id, "data: [DONE]");
        }
    }

    /// <summary>Publica o evento <c>status</c> de fase no stream da run.</summary>
    private async Task PublishPhaseAsync(ChatRun run, string phase, string? label,
        int? step = null, int? maxSteps = null)
    {
        broadcaster.Publish(run.Id,
            $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent(phase, label, step, maxSteps), JsonOptions)}");
        await runEvents.AppendAsync(run.Id, RunEventWriter.Types.Phase,
            new { phase, label, step, maxSteps }, CancellationToken.None);
    }

    /// <summary>
    /// Promove mensagens de steer/queue pendentes (SPEC-20261010-steer-queue):
    /// marca a linha, anexa <c>ChatMessage</c> user ao chat (visível no
    /// transcript), publica evento <c>steer</c> no SSE e devolve o payload pra
    /// injeção no histórico do loop. <paramref name="queuedOnly"/> = true só
    /// promove o modo queue (run ficaria ociosa); false só steer.
    /// </summary>
    private async Task<IReadOnlyList<ChatCompletionMessage>> PollSteerAsync(
        ChatRun run, bool queuedOnly, CancellationToken ct)
    {
        var mode = queuedOnly ? "queue" : "steer";
        List<ChatRunSteer> pending;
        try
        {
            pending = await db.ChatRunSteers
                .Where(s => s.RunId == run.Id && s.Status == "pending" && s.Mode == mode)
                .OrderBy(s => s.Timestamp)
                .ToListAsync(ct);
        }
        catch (Exception)
        {
            // Tabela pode não existir em base antiga mid-deploy — não quebra a run.
            return [];
        }
        if (pending.Count == 0)
        {
            return [];
        }

        var chat = await db.Chats.Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == run.ChatId, ct);
        var promoted = new List<ChatCompletionMessage>(pending.Count);
        foreach (var steer in pending)
        {
            steer.Status = "promoted";
            promoted.Add(new ChatCompletionMessage("user", steer.Content));
            if (chat is not null)
            {
                var pos = chat.Messages.Count == 0 ? 0 : chat.Messages.Max(m => m.Position) + 1;
                var message = new ChatMessage
                {
                    ChatId = chat.Id, Role = "user", Content = steer.Content,
                    Position = pos, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ParentId = BranchService.ResolveLeaf(chat)?.Id,
                };
                chat.Messages.Add(message);
                chat.LeafMessageId = message.Id;
            }
            broadcaster.Publish(run.Id,
                $"event: steer\ndata: {JsonSerializer.Serialize(new { mode = steer.Mode, content = steer.Content }, JsonOptions)}");
        }
        if (chat is not null)
        {
            chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        await db.SaveChangesAsync(CancellationToken.None);
        return promoted;
    }

    /// <summary>
    /// Gatilhos do revisor de run (SPEC-20261013-run-advisor RF-001): checa
    /// o flag do chat + cadência (cap por run, cooldown, uma revisão por
    /// vez) e dispara a revisão em task própria com timeout de 20s — o
    /// turno nunca espera pelo advisor (RF-004). Um conselho produzido vira
    /// evento SSE <c>advisor</c> e entra na fila drenada como nota system
    /// na próxima fronteira de rodada (RF-003).
    /// </summary>
    private async Task KickAdvisorAsync(
        ChatRun run, string trigger, BuiltinToolContext? builtinContext,
        CancellationToken ct)
    {
        if (_advisorInFlight
            || _advisorCount >= RunAdvisorService.MaxAdvicesPerRun
            || DateTimeOffset.UtcNow - _advisorLastAt < RunAdvisorService.Cooldown)
        {
            return;
        }
        // Flag relido por gatilho — desligar mid-run vale já aqui (RF-004).
        if (!await advisor.IsEnabledAsync(run.ChatId, ct))
        {
            return;
        }
        _advisorCount++;
        _advisorLastAt = DateTimeOffset.UtcNow;
        _advisorInFlight = true;

        string[] snapshot;
        lock (_recentTools)
        {
            snapshot = [.. _recentTools];
        }
        var (runId, chatId, userId, model) =
            (run.Id, run.ChatId, run.UserId, run.Model);
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(20));
                // Escopo próprio: a revisão roda em paralelo ao DbContext do
                // executor (EF Core não é thread-safe).
                using var scope = scopes.CreateScope();
                var scoped = scope.ServiceProvider;
                var review = scoped.GetRequiredService<RunAdvisorService>();
                var advice = await review.AdviseAsync(
                    new ChatRun
                    {
                        Id = runId, ChatId = chatId, UserId = userId, Model = model,
                    },
                    trigger, snapshot, builtinContext, timeout.Token);
                if (advice is null)
                {
                    return;
                }
                // Run terminada durante a revisão → conselho descartado.
                var status = await scoped.GetRequiredService<AppDbContext>()
                    .ChatRuns.AsNoTracking()
                    .Where(r => r.Id == runId)
                    .Select(r => r.Status)
                    .FirstOrDefaultAsync(timeout.Token);
                if (status is not (ChatRunStatus.Running or ChatRunStatus.Paused))
                {
                    return;
                }
                broadcaster.Publish(runId,
                    $"event: advisor\ndata: {JsonSerializer.Serialize(new RunAdvisorEvent(advice, trigger), JsonOptions)}");
                lock (_contextNotes)
                {
                    _contextNotes.Enqueue(new ChatCompletionMessage(
                        "system", $"[advisor] {advice}"));
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "Advisor falhou no gatilho {Trigger} da run {RunId} — segue sem conselho.",
                    trigger, runId);
            }
            finally
            {
                _advisorInFlight = false;
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Fronteira de rodada (SPEC-20261013-agent-context-management RF-004):
    /// consome o pedido <c>new_context</c> uma vez — compacta o histórico
    /// persistido já (sem esperar o próximo enqueue) e devolve a fila de
    /// notas system (advisor + confirmação) para o próximo round.
    /// </summary>
    private async Task<IReadOnlyList<ChatCompletionMessage>> DrainContextNotesAsync(
        ChatRun run, CancellationToken ct)
    {
        if (await boundaries.ConsumeNewContextAsync(run.Id, ct))
        {
            try
            {
                // Caminho ativo (SPEC-20261013-chat-branching RF-002): fora
                // dele, mensagens de outros ramos nunca voltam ao modelo.
                var persistedChat = await db.Chats.AsNoTracking()
                    .Include(c => c.Messages)
                    .FirstOrDefaultAsync(c => c.Id == run.ChatId, ct);
                var persisted = persistedChat is null
                    ? []
                    : BranchService.WalkPath(persistedChat);
                var visible = ContextBoundaryService.FilterMessages(
                    persisted, await boundaries.ListAsync(run.ChatId, ct));
                var history = visible
                    .Select(m => new ChatCompletionMessage(
                        m.Role, m.Content, m.ToolCallId, m.ToolCallsJson))
                    .ToList();
                if (await compaction.MaybeCompactAsync(
                        run.ChatId, history, run.Model, ct, run.UserId, force: true))
                {
                    lock (_contextNotes)
                    {
                        _contextNotes.Enqueue(new ChatCompletionMessage(
                            "system",
                            "[contexto] Nova janela aplicada — o histórico anterior foi "
                            + "resumido; o que deve persistir pertence ao notebook."));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "new_context falhou na run {RunId} — segue sem compactar.", run.Id);
            }
        }

        List<ChatCompletionMessage> notes;
        lock (_contextNotes)
        {
            notes = [.. _contextNotes];
            _contextNotes.Clear();
        }
        return notes;
    }

    /// <summary>Publica o evento <c>tool_call</c> com preview higienizado dos args.</summary>
    private Task PublishToolCallAsync(ChatRun run, ProviderToolCall call)
    {
        logger.LogInformation(
            "Run {RunId}: tool call {Name} ({Id}), args {Chars} chars.",
            run.Id, call.Name, call.Id, call.ArgumentsJson.Length);
        broadcaster.Publish(run.Id,
            $"event: tool_call\ndata: {JsonSerializer.Serialize(new RunToolCallEvent(call.Id, call.Name, Scrub(Truncate(call.ArgumentsJson, PreviewChars))), JsonOptions)}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Contexto das tools built-in da run (SPEC-20261007-chat-agent-tools):
    /// workspace confinado em <c>data/workspaces/{userId}</c> e uploads em
    /// <c>data/uploads/{userId}</c> (mesma raiz das telas de Imagens).
    /// Com <c>Workspace:RunIsolation=worktree</c> a run ganha um worktree
    /// isolado (E16 S9) — sem git ele cai em <c>shared</c> com aviso.
    /// </summary>
    private async Task<BuiltinToolContext> BuildToolContextAsync(
        ChatRun run, User user, CancellationToken ct)
    {
        // Com repo vinculado o workdir vira o checkout do repo (mesmo jail);
        // binding por chat precede o global (SPEC-20261010-chat-repo-binding).
        var workdir = await repos.ResolveWorkdirAsync(user.Id, run.ChatId, ct);
        // SPEC-20261010-subagent-worktree: sub-runs (delegate_task) SEMPRE
        // isolam em worktree; runs raiz respeitam Workspace:RunIsolation.
        var (isolated, warning) = await worktrees.TryCreateForRunAsync(
            user.Id, run.Id, workdir, ct, force: run.ParentRunId is not null);
        if (warning is not null)
        {
            // Fallback shared: visível no stream como fase informativa.
            await PublishPhaseAsync(run, "worktree", warning);
        }

        return new BuiltinToolContext(
            user.Id,
            run.ChatId,
            run.Id,
            isolated ?? workdir,
            Path.Join(DataPaths.Root(env.ContentRootPath), "uploads", user.Id));
    }

    /// <summary>
    /// Modo efetivo do chat (SPEC-15): o <c>build</c> exige repositório
    /// vinculado à conversa — sem binding o modo resolve como
    /// <see cref="PermissionRuleset.ChatMode"/> (tools de workspace nem
    /// entram no spec anunciado ao provider).
    /// </summary>
    private async Task<string> EffectiveModeAsync(string chatId, CancellationToken ct)
    {
        var mode = PermissionRuleset.Normalize(await db.Chats.AsNoTracking()
            .Where(c => c.Id == chatId)
            .Select(c => c.Mode).FirstOrDefaultAsync(ct));
        return mode == PermissionRuleset.BuildMode
            && await repos.GetChatBindingAsync(chatId, ct) is null
                ? PermissionRuleset.ChatMode : mode;
    }

    /// <summary>apply_patch pode chegar como builtin por nome legado — cobre o nome cru.</summary>
    private static bool IsApplyPatch(string name) =>
        name.EndsWith("apply_patch", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tira um snapshot do workdir e publica o evento <c>checkpoint</c>
    /// (E16 S6). Falhas são engolidas pelo serviço — nunca derrubam a run.
    /// </summary>
    private async Task PublishCheckpointAsync(
        ChatRun run, string workdir, int turn, CancellationToken ct)
    {
        var info = await checkpoints.SnapshotAsync(workdir, run.Id, turn, ct);
        if (info is null)
        {
            return;
        }
        broadcaster.Publish(run.Id,
            $"event: checkpoint\ndata: {JsonSerializer.Serialize(new RunCheckpointEvent(info.Hash, info.Files, turn), JsonOptions)}");
    }

    /// <summary>Publica o evento <c>tool_result</c> (ok=false em erro/negação).</summary>
    private Task PublishToolResultAsync(
        ChatRun run, ProviderToolCall call, string output, JsonElement? result, bool denied)
    {
        var ok = !denied && !output.StartsWith("Erro", StringComparison.Ordinal);
        var (imagePath, videoPath) = ExtractMediaPaths(result);
        PublishTasksSnapshot(run, result);
        if (AccumulateChanges(result))
        {
            broadcaster.Publish(run.Id,
                $"event: changes\ndata: {JsonSerializer.Serialize(new RunChangesEvent(_changes.Values.ToList()), JsonOptions)}");
        }
        broadcaster.Publish(run.Id,
            $"event: tool_result\ndata: {JsonSerializer.Serialize(new RunToolResultEvent(call.Id, call.Name, ok, Scrub(Truncate(output, PreviewChars)), ImagePath: imagePath, Denied: denied, Result: result, VideoPath: videoPath), JsonOptions)}");
        return Task.CompletedTask;
    }

    /// <summary>Extrai imagePath/videoPath do payload estruturado da tool.</summary>
    private static (string? ImagePath, string? VideoPath) ExtractMediaPaths(JsonElement? result)
    {
        string? imagePath = null;
        string? videoPath = null;
        if (result is { } el && el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("imagePath", out var img))
            {
                imagePath = img.GetString();
            }
            if (el.TryGetProperty("videoPath", out var vid))
            {
                videoPath = vid.GetString();
            }
        }
        return (imagePath, videoPath);
    }

    /// <summary>
    /// todo_write publica o snapshot de tarefas como evento <c>tasks</c>
    /// (RF-010 chat-agent-parity) — replay cobre attach tardio.
    /// </summary>
    private void PublishTasksSnapshot(ChatRun run, JsonElement? result)
    {
        if (result is { } res && res.ValueKind == JsonValueKind.Object
            && res.TryGetProperty("tasks", out var tasks)
            && tasks.ValueKind == JsonValueKind.Array)
        {
            broadcaster.Publish(run.Id, $"event: tasks\ndata: {tasks.GetRawText()}");
        }
    }

    /// <summary>
    /// file_write/file_edit (path único) e apply_patch (files[]) acumulam o
    /// diff por path (RF-015) — devolve true quando algum change foi tocado.
    /// </summary>
    private bool AccumulateChanges(JsonElement? result)
    {
        var touched = false;
        if (result is { } chg && chg.ValueKind == JsonValueKind.Object
            && chg.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
            && chg.TryGetProperty("added", out var a) && a.ValueKind == JsonValueKind.Number
            && chg.TryGetProperty("removed", out var r) && r.ValueKind == JsonValueKind.Number)
        {
            TrackChange(p.GetString()!, a.GetInt32(), r.GetInt32(), ExtractDiff(chg));
            touched = true;
        }
        if (result is { } chgFiles && chgFiles.ValueKind == JsonValueKind.Object
            && chgFiles.TryGetProperty("files", out var files)
            && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                if (f.TryGetProperty("path", out var fp) && fp.ValueKind == JsonValueKind.String
                    && f.TryGetProperty("added", out var fa) && fa.ValueKind == JsonValueKind.Number
                    && f.TryGetProperty("removed", out var fr) && fr.ValueKind == JsonValueKind.Number)
                {
                    TrackChange(fp.GetString()!, fa.GetInt32(), fr.GetInt32(), ExtractDiff(f));
                    touched = true;
                }
            }
        }
        return touched;
    }

    /// <summary>Registra o diff do path no acumulador da run (aba Changes do painel).</summary>
    private void TrackChange(string path, int added, int removed, string? diff) =>
        _changes[path] = new RunChangeItem(path, added, removed, diff);

    /// <summary>Diff textual de um item de change, truncado para o snapshot.</summary>
    private static string? ExtractDiff(JsonElement el) =>
        el.TryGetProperty("diff", out var d) && d.ValueKind == JsonValueKind.String
            ? Truncate(d.GetString()!, ChangeDiffChars)
            : null;

    /// <summary>
    /// Gate de aprovação por tool call (RF-003/RF-004): tool não-mutável
    /// ou já lembrada executa direto; preset <c>always-allow</c> libera,
    /// <c>allow-readonly</c> nega; <c>approve-mutations</c> emite
    /// <c>approval_asked</c> e espera a decisão do dono (timeout → deny);
    /// uma negação pode carregar a instrução do dono (RF-002 chat-agent-ux).
    /// O preset é relido a cada call — mudança mid-run vale já no próximo.
    /// </summary>
    private async Task<ToolGateDecision> GateToolCallAsync(
        ChatRun run, IReadOnlyList<Tool> tools, ProviderToolCall call,
        BuiltinToolContext builtinContext, CancellationToken ct)
    {
        // Checkpoint de pausa antes do gate (RF-013) — a call pendente
        // só resolve depois do resume.
        await pauses.WaitIfPausedAsync(run.Id, ct);

        // Preset + modo do chat relidos a cada call — mudança mid-run
        // (como a promoção do plan_exit) vale já na próxima call.
        var gateInfo = await db.Chats.AsNoTracking()
            .Where(c => c.Id == run.ChatId)
            .Select(c => new { c.ApprovalPreset, c.Mode })
            .FirstOrDefaultAsync(ct);
        var mode = PermissionRuleset.Normalize(gateInfo?.Mode);
        // SPEC-15: build sem repo vinculado não executa tools de workspace.
        if (mode == PermissionRuleset.BuildMode
            && await repos.GetChatBindingAsync(run.ChatId, ct) is null)
        {
            mode = PermissionRuleset.ChatMode;
        }

        // ask_user (RF-005): a pergunta é o próprio gate; a resposta volta
        // como ToolGateDecision.Output e vira o resultado da tool. SPEC-15
        // RF-003: presets autônomos respondem sozinhos quando há opções.
        if (string.Equals(call.Name, "builtin:ask_user", StringComparison.OrdinalIgnoreCase))
        {
            return await AskUserAsync(run, call, gateInfo?.ApprovalPreset, ct);
        }

        // plan_exit (RF-003): gate próprio — grava o plano, pergunta o
        // dono e aprovado promove o chat a build. SPEC-15 RF-003: presets
        // autônomos promovem sem perguntar.
        if (string.Equals(call.Name, "builtin:plan_exit", StringComparison.OrdinalIgnoreCase))
        {
            return await PlanExitAsync(
                run, tools, call, builtinContext, mode, gateInfo?.ApprovalPreset, ct);
        }

        var tool = ToolExecutor.FindTool(tools, call.Name);

        // Ruleset do modo do agente (RF-002): negação estruturada — nem
        // anunciada ao provider; uma call que escape devolve erro direto,
        // sem executar e sem prompt de aprovação.
        if (tool is not null
            && PermissionRuleset.Evaluate(mode, tool, call.ArgumentsJson)
                == PermissionDecision.Deny)
        {
            return new ToolGateDecision(true, Output:
                $"Erro: a tool '{call.Name}' está bloqueada no modo {mode} "
                + "(somente leitura). Ajuste o plano com as tools de leitura "
                + "disponíveis e chame builtin_plan_exit quando terminar.");
        }

        // Overrides persistentes do dono (SPEC-20261013-approval-tiers
        // RF-002): deny → erro estruturado; allow → executa (o deny do
        // modo já foi avaliado acima e continua absoluto); prompt →
        // sempre pergunta, inclusive em always-allow.
        var userOverrides = await toolOverrides.GetAsync(run.UserId, ct);
        var forcePrompt = false;
        if (tool is not null)
        {
            switch (ToolApprovalPolicy.OverrideFor(userOverrides, call.Name))
            {
                case ToolOverride.Deny:
                    return new ToolGateDecision(true, Output:
                        $"Erro: a tool '{call.Name}' está bloqueada pelo dono "
                        + "(override deny nas aprovações por ferramenta). "
                        + "Escolha outra tool ou peça para o dono ajustar o override.");
                case ToolOverride.Allow:
                    return ToolGateDecision.Allow;
                case ToolOverride.Prompt:
                    forcePrompt = true;
                    break;
            }
        }

        if (tool is null || (!forcePrompt
            && (!ToolExecutor.IsMutable(tool)
                || approvals.IsRemembered(run.ChatId, call.Name,
                    PermissionRuleset.ArgPattern(call.Name, call.ArgumentsJson)))))
        {
            return ToolGateDecision.Allow;
        }

        var preset = gateInfo?.ApprovalPreset ?? "approve-mutations";
        if (!forcePrompt)
        {
            switch (preset)
            {
            case "always-allow":
                return ToolGateDecision.Allow;
            case "allow-readonly":
                return ToolGateDecision.Deny;
            case "auto":
            case "smart":
            {
                // RF-012 (paridade OpenHands): LOW executa direto; MEDIUM
                // executa com notice no stream; HIGH cai no fluxo de
                // pergunta abaixo. O preset <c>smart</c> segue o mesmo
                // tiers, mas mutações de arquivo caem na pergunta —
                // "executa sem aprovação, com restrição em alterações".
                var workspace = Path.Join(
                    DataPaths.Root(env.ContentRootPath), "workspaces", run.UserId);
                var risk = ToolCallRiskClassifier.Classify(
                    tool, call.ArgumentsJson, workspace);
                var fileMutation = risk == ToolCallRisk.Medium
                    && preset == "smart"
                    && ToolCallRiskClassifier.IsFileMutation(
                        tool, call.ArgumentsJson, workspace);
                if (risk == ToolCallRisk.Low)
                {
                    return ToolGateDecision.Allow;
                }

                if (risk == ToolCallRisk.Medium && !fileMutation)
                {
                    broadcaster.Publish(run.Id,
                        $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent("auto_approved", call.Name), JsonOptions)}");
                    return ToolGateDecision.Allow;
                }

                break;
            }
            }
        }

        var argsPreview = Scrub(Truncate(call.ArgumentsJson, PreviewChars));
        var kind = !string.IsNullOrWhiteSpace(tool.Code)
            ? "python"
            : tool.Url.StartsWith(McpClientService.VirtualUrlPrefix,
                StringComparison.Ordinal) ? "mcp" : "http";
        broadcaster.Publish(run.Id,
            $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent("awaiting_approval", call.Name), JsonOptions)}");
        broadcaster.Publish(run.Id,
            $"event: approval_asked\ndata: {JsonSerializer.Serialize(new RunApprovalAskedEvent(call.Id, call.Name, kind, argsPreview), JsonOptions)}");
        // Pausa por aprovação (RF-001): o advisor revisa enquanto o dono decide.
        await KickAdvisorAsync(run, "pause", builtinContext, ct);
        var result = await approvals.WaitAsync(run.Id, run.ChatId, call.Id,
            call.Name, ct, PermissionRuleset.ArgPattern(call.Name, call.ArgumentsJson));
        var message = string.IsNullOrWhiteSpace(result.Message)
            ? null
            : Scrub(Truncate(result.Message!, PreviewChars));
        return new ToolGateDecision(result.Approved, message);
    }

    /// <summary>
    /// Gate do <c>builtin:plan_exit</c> (SPEC-20261009 RF-003): só existe
    /// no modo plan — grava o plano em <c>.openwebui/plans/</c> e pergunta
    /// "Executar este plano?". Aprovado: promove o chat a build, publica o
    /// evento <c>mode</c> e devolve a instrução de execução como
    /// <see cref="ToolGateDecision.Output"/>; negado: devolve o caminho
    /// para o modelo ajustar o plano.
    /// </summary>
    private async Task<ToolGateDecision> PlanExitAsync(
        ChatRun run, IReadOnlyList<Tool> tools, ProviderToolCall call,
        BuiltinToolContext builtinContext, string mode, string? preset,
        CancellationToken ct)
    {
        if (mode != PermissionRuleset.PlanMode)
        {
            return new ToolGateDecision(true, Output:
                "Erro: plan_exit só existe no modo plan — a conversa já está em build.");
        }

        // 1. Grava o plano no workdir jailed — mesmo negado, fica salvo.
        var write = await toolExecutor.ExecuteAsync(
            tools, call.Name, call.ArgumentsJson, builtinContext, ct);
        if (write.Text.StartsWith("Erro", StringComparison.Ordinal))
        {
            return new ToolGateDecision(true, Output: write.Text);
        }

        // 2. Pergunta ao dono pelo mesmo mecanismo do ask_user — exceto
        //    nos presets autônomos (SPEC-15 RF-003), que aprovam direto e
        //    auditam a escolha via decision_auto.
        if (!IsAutoPickPreset(preset))
        {
            broadcaster.Publish(run.Id,
                $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent("awaiting_approval", call.Name), JsonOptions)}");
            broadcaster.Publish(run.Id,
                $"event: question_asked\ndata: {JsonSerializer.Serialize(new RunQuestionAskedEvent(call.Id, "Executar este plano?", ["Executar o plano", "Continuar planejando"], false), JsonOptions)}");
            var result = await approvals.WaitAsync(
                run.Id, run.ChatId, call.Id, call.Name, ct);

            if (!result.Approved)
            {
                var note = string.IsNullOrWhiteSpace(result.Message)
                    ? string.Empty
                    : $" (nota: {Scrub(Truncate(result.Message!, PreviewChars))})";
                return new ToolGateDecision(true, Output:
                    $"{write.Text}\n\nPlano não aprovado{note} — ajuste o plano "
                    + "com as tools de leitura e chame plan_exit de novo.");
            }
        }
        else
        {
            PublishDecisionAuto(run, call.Id, "plan_exit", "Executar o plano");
        }

        // 3. Promoção plan → build: persiste no chat, publica o evento
        //    (o chip do cliente atualiza ao vivo) e instrui o modelo.
        //    SPEC-15: build exige repo vinculado — sem binding o plano
        //    aprovado sai do plan para chat (conversa, sem workspace).
        var target = await repos.GetChatBindingAsync(run.ChatId, ct) is not null
            ? PermissionRuleset.BuildMode
            : PermissionRuleset.ChatMode;
        await db.Chats.Where(c => c.Id == run.ChatId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Mode, target), ct);
        broadcaster.Publish(run.Id,
            $"event: mode\ndata: {JsonSerializer.Serialize(new RunModeEvent(target), JsonOptions)}");
        return new ToolGateDecision(true, Output:
            target == PermissionRuleset.BuildMode
                ? $"{write.Text}\n\nPlano aprovado — execute o plano agora. "
                    + "O modo build está ativo: tools de escrita/execução liberadas "
                    + "nesta run (o gate de aprovação do preset segue valendo)."
                : $"{write.Text}\n\nPlano aprovado — mas a conversa não tem "
                    + "repositório vinculado, então ela segue no modo chat "
                    + "(sem tools de workspace). Vincule um repositório para "
                    + "executar o plano.");
    }

    /// <summary>
    /// Gate do <c>builtin:ask_user</c> (RF-005): publica
    /// <c>question_asked</c> com a pergunta e opções dos args, espera a
    /// decisão do dono pelo mesmo mecanismo de aprovação e devolve a
    /// resposta como <see cref="ToolGateDecision.Output"/> — a tool não
    /// executa de fato. Negar/pular → "o usuário não respondeu" (a run
    /// segue; o modelo decide o próximo passo).
    /// </summary>
    private async Task<ToolGateDecision> AskUserAsync(
        ChatRun run, ProviderToolCall call, string? preset, CancellationToken ct)
    {
        List<RunQuestionItem> items;
        try
        {
            // NormalizeArgs desembrulha args double-encoded (string com o
            // objeto dentro — Gemini/OmniRoute). Sem isso o TryGetProperty
            // em root String lançava InvalidOperationException e a run
            // inteira ia para failed.
            var args = BuiltinToolRegistry.NormalizeArgs(call.ArgumentsJson);
            if (args.ValueKind != JsonValueKind.Object)
            {
                return new ToolGateDecision(true, Output: "Pergunta malformada (args inválidos).");
            }

            // SPEC-20261016-question-forms RF-001: questions[] é o form
            // multi-pergunta (cap 5); a forma singular vira açúcar de 1 item.
            if (args.TryGetProperty("questions", out var qs)
                && qs.ValueKind == JsonValueKind.Array)
            {
                if (qs.GetArrayLength() > 5)
                {
                    return new ToolGateDecision(
                        true, Output: "Erro: max 5 questions por chamada.");
                }
                items = [.. qs.EnumerateArray().Select(ParseQuestionItem)];
            }
            else
            {
                var question = args.TryGetProperty("question", out var q)
                    && q.GetString() is { Length: > 0 } qt ? qt : "?";
                var options = ParseOptions(args);
                var multiple = args.TryGetProperty("multiple", out var m)
                    && m.ValueKind == JsonValueKind.True;
                items = [new RunQuestionItem(question, null, options, multiple, true)];
            }
            if (items.Count == 0)
            {
                return new ToolGateDecision(true, Output: "Pergunta malformada (questions vazio).");
            }
        }
        catch (JsonException)
        {
            return new ToolGateDecision(true, Output: "Pergunta malformada (args inválidos).");
        }

        var first = items[0];
        var single = items.Count == 1;

        // SPEC-15 RF-003: preset autônomo + opções → responde sozinho com a
        // recomendada (marcada) ou a primeira, sem pausar a run. No form
        // multi, só auto-responde quando TODA pergunta tem opções; uma
        // resposta livre segue aguardando o dono.
        if (IsAutoPickPreset(preset) && items.All(i => i.Options.Length > 0))
        {
            if (single)
            {
                var pick = PickRecommended(first.Options);
                PublishDecisionAuto(run, call.Id, "ask_user", pick);
                return new ToolGateDecision(true, Output: pick);
            }
            var picks = items.Select(i => PickRecommended(i.Options)).ToList();
            PublishDecisionAuto(run, call.Id, "ask_user", picks[0]);
            return new ToolGateDecision(
                true, Output: JsonSerializer.Serialize(new { answers = picks }, JsonOptions));
        }

        broadcaster.Publish(run.Id,
            $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent("awaiting_approval", call.Name), JsonOptions)}");
        broadcaster.Publish(run.Id,
            $"event: question_asked\ndata: {JsonSerializer.Serialize(new RunQuestionAskedEvent(
                call.Id,
                Scrub(Truncate(first.Question, PreviewChars)),
                first.Options, first.Multiple,
                single ? null : items.Select(i => i with
                {
                    Question = Scrub(Truncate(i.Question, PreviewChars))
                }).ToArray(),
                "builtin:ask_user"), JsonOptions)}");

        var result = await approvals.WaitAsync(run.Id, run.ChatId, call.Id, call.Name, ct);

        // RF-004: deny/skip devolve recusa — a run segue, o modelo decide.
        if (!result.Approved || string.IsNullOrWhiteSpace(result.Message))
        {
            return new ToolGateDecision(true,
                Output: single ? "O usuário não respondeu." : "usuário recusou responder");
        }

        // RF-002/003: no form multi a resposta vem como JSON {answers:[..]}
        // alinhado por índice (string | string[] | null por item); texto
        // solto é tratado como resposta do item 0.
        if (!single && TryParseAnswers(result.Message!, items.Count, out var answers))
        {
            return new ToolGateDecision(true, Output: Scrub(answers));
        }
        return new ToolGateDecision(true,
            Output: Scrub(Truncate(result.Message!, PreviewChars)));
    }

    /// <summary>Item do form multi-pergunta (options aceitam string ou {label,description}).</summary>
    private static RunQuestionItem ParseQuestionItem(JsonElement el)
    {
        var question = el.TryGetProperty("question", out var q)
            && q.GetString() is { Length: > 0 } qt ? qt : "?";
        var header = el.TryGetProperty("header", out var h)
            && h.GetString() is { Length: > 0 } ht ? ht : null;
        var options = ParseOptions(el);
        var multiple = el.TryGetProperty("multiple", out var m)
            && m.ValueKind == JsonValueKind.True;
        var required = !el.TryGetProperty("required", out var r)
            || r.ValueKind != JsonValueKind.False;
        return new RunQuestionItem(question, header, options, multiple, required);
    }

    /// <summary>Lê options[] — strings puras ou objetos {label,description}.</summary>
    private static string[] ParseOptions(JsonElement el)
    {
        if (!el.TryGetProperty("options", out var opts)
            || opts.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return [.. opts.EnumerateArray()
            .Select(o => o.ValueKind == JsonValueKind.String
                ? o.GetString()
                : o.ValueKind == JsonValueKind.Object
                    && o.TryGetProperty("label", out var l)
                        ? l.GetString()
                        : null)
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o!)
            .Take(4)];
    }

    /// <summary>
    /// Valida/normaliza o JSON {answers:[..]} da resposta do form: exige
    /// exatamente <paramref name="count"/> posições; cada item é string,
    /// array de strings ou null. Devolve o JSON reemitido (capado).
    /// </summary>
    private static bool TryParseAnswers(string message, int count, out string answers)
    {
        answers = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (!doc.RootElement.TryGetProperty("answers", out var array)
                || array.ValueKind != JsonValueKind.Array
                || array.GetArrayLength() != count)
            {
                return false;
            }

            // JsonElement só é válida enquanto o doc vive — normaliza aqui.
            var normalized = new List<object?>(count);
            foreach (var item in array.EnumerateArray())
            {
                switch (item.ValueKind)
                {
                    case JsonValueKind.String:
                        normalized.Add(item.GetString());
                        break;
                    case JsonValueKind.Array:
                        normalized.Add(item.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()).ToList());
                        break;
                    case JsonValueKind.Null:
                        normalized.Add(null);
                        break;
                    default:
                        return false;
                }
            }
            answers = JsonSerializer.Serialize(new { answers = normalized }, JsonOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Presets que respondem perguntas com opções sozinhos (SPEC-15 RF-003).
    /// <c>approve-mutations</c>/<c>allow-readonly</c> seguem perguntando.
    /// </summary>
    private static bool IsAutoPickPreset(string? preset) =>
        preset is "smart" or "always-allow" or "auto";

    /// <summary>
    /// Opção recomendada = a que o modelo marcou com "(recomendado)" /
    /// "(recommended)"; senão a primeira (SPEC-15 RF-003 — determinístico).
    /// </summary>
    private static string PickRecommended(string[] options) =>
        options.FirstOrDefault(o =>
            o.Contains("recomendad", StringComparison.OrdinalIgnoreCase)
            || o.Contains("recommended", StringComparison.OrdinalIgnoreCase))
        ?? options[0];

    /// <summary>Audita a escolha automática no SSE (SPEC-15 RF-003).</summary>
    private void PublishDecisionAuto(
        ChatRun run, string callId, string source, string chosen) =>
        broadcaster.Publish(run.Id,
            $"event: decision_auto\ndata: {JsonSerializer.Serialize(new RunDecisionAutoEvent(callId, source, Scrub(Truncate(chosen, PreviewChars)), "recommended_option"), JsonOptions)}");

    /// <summary>Esconde padrões óbvios de secret antes de publicar no SSE.</summary>
    private static string Scrub(string value)
    {
        var scrubbed = SecretPattern.Replace(value, m
            => m.Value.StartsWith("Bearer", StringComparison.Ordinal)
                ? "Bearer ***"
                : m.Value[..3] + "***");
        return scrubbed;
    }

    private static readonly System.Text.RegularExpressions.Regex SecretPattern = new(
        @"Bearer\s+[^\s""']+|sk-[A-Za-z0-9_-]{8,}",
        System.Text.RegularExpressions.RegexOptions.Compiled, RegexDefaults.MatchTimeout);

    /// <summary>Roteia `pipeline:{id}` e repassa as linhas SSE do upstream.</summary>
    private async Task RunPipelineAsync(
        ChatCompletionRequest request, ChatRun run, CancellationToken ct)
    {
        var pipeId = request.Model["pipeline:".Length..];
        var (server, body, valvesJson) = await ChatPipeline.PreparePipelineRouteAsync(
            request, db, pipelines, ct);
        if (server is null)
        {
            broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error = $"Pipe '{pipeId}' não encontrado." })}");
            return;
        }

        var proxied = await pipelines.RouteCompletionAsync(server, body, valvesJson, ct);
        if (proxied.Response is null)
        {
            broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error = $"Falha no servidor de pipelines: {proxied.Error}" })}");
            return;
        }

        using var upstream = proxied.Response;
        if (!upstream.IsSuccessStatusCode)
        {
            var body_ = await upstream.Content.ReadAsStringAsync(ct);
            broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error = $"Pipeline devolveu {(int)upstream.StatusCode}: {Truncate(body_, 500)}" })}");
            return;
        }

        using var reader = new StreamReader(await upstream.Content.ReadAsStreamAsync(ct));
        var accumulated = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            broadcaster.Publish(run.Id, line);
            AccumulateDelta(line, accumulated);
        }

        run.PartialContent = accumulated.ToString();
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Extrai o delta de texto de uma linha SSE `data: {...}`.</summary>
    private static void AccumulateDelta(string line, StringBuilder into)
    {
        if (!line.StartsWith("data: ", StringComparison.Ordinal) || line == "data: [DONE]")
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line["data: ".Length..]);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var delta = choices[0].TryGetProperty("delta", out var d) ? d : default;
                var piece = delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out var c)
                        ? c.GetString()
                        : null;
                if (piece is not null)
                {
                    into.Append(piece);
                }
            }
        }
        catch (JsonException)
        {
            // Linha não-JSON (keep-alive etc.): ignora.
        }
    }

    /// <summary>
    /// Persiste <c>run.MetaJson.usage</c> (SPEC-20261016-agent-steps-usage
    /// RF-004/RF-005): steps executados sempre; tokens só quando o provider
    /// reportou; <c>costUsd</c> quando <c>ModelEntry.MetaJson.cost</c> tem
    /// input+output (USD por 1M tokens). Ausência é omitida, nunca zero
    /// inventado.
    /// </summary>
    private async Task ApplyUsageMetaAsync(
        ChatRun run, ChatPipeline.ToolLoopOutcome outcome, CancellationToken ct)
    {
        if (outcome.TotalSteps <= 0)
        {
            return;
        }

        var usages = outcome.StepUsages ?? [];
        var meta = new Dictionary<string, object?>
        {
            ["steps"] = outcome.TotalSteps,
        };
        if (usages.Count > 0)
        {
            meta["promptTokens"] = usages.Sum(u => u.PromptTokens);
            meta["completionTokens"] = usages.Sum(u => u.CompletionTokens);
            var reasoning = usages.Sum(u => u.ReasoningTokens ?? 0);
            if (usages.Any(u => u.ReasoningTokens is not null))
            {
                meta["reasoningTokens"] = reasoning;
            }
            meta["perStep"] = usages.Select(u => new Dictionary<string, object?>
            {
                ["step"] = u.Step,
                ["promptTokens"] = u.PromptTokens,
                ["completionTokens"] = u.CompletionTokens,
                ["reasoningTokens"] = u.ReasoningTokens,
            }).ToList();

            if (await ModelCostAsync(run.Model, run.UserId,
                    usages.Sum(u => u.PromptTokens),
                    usages.Sum(u => u.CompletionTokens), ct) is { } cost)
            {
                meta["costUsd"] = Math.Round(cost, 6);
            }
        }

        var full = ReadMetaDict(run.MetaJson);
        full["usage"] = meta;
        run.MetaJson = JsonSerializer.Serialize(full, JsonOptions);
    }

    /// <summary>
    /// Meta da retomada (SPEC-20261016-run-recovery RF-006):
    /// <c>resumedCount</c> acumula restarts da run e <c>lastSeq</c> marca o
    /// último evento aplicado — ambos expostos no response via MetaJson.
    /// </summary>
    private async Task BumpResumeMetaAsync(
        ChatRun run, long lastSeq, CancellationToken ct)
    {
        var meta = ReadMetaDict(run.MetaJson);
        var resumed = meta.TryGetValue("resumedCount", out var prev)
            && prev is JsonElement el && el.TryGetInt32(out var n)
                ? n : 0;
        meta["resumedCount"] = resumed + 1;
        meta["lastSeq"] = lastSeq;
        run.MetaJson = JsonSerializer.Serialize(meta, JsonOptions);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>MetaJson → dicionário (vazio quando ausente/corrompido).</summary>
    private static Dictionary<string, object?> ReadMetaDict(string? metaJson)
    {
        if (metaJson is null)
        {
            return new();
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                metaJson, JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>
    /// Custo estimado da run (RF-005): <c>ModelEntry.MetaJson</c>
    /// <c>{"cost":{"input":x,"output":y"}}</c> em USD/1M tokens. Só com os
    /// dois campos — custo parcial é omitido (edge case do SPEC).
    /// </summary>
    private async Task<double?> ModelCostAsync(
        string model, string userId,
        int promptTokens, int completionTokens, CancellationToken ct)
    {
        var metaJson = await db.ModelEntries.AsNoTracking()
            .Where(m => (m.Id == model || m.Name == model || m.BaseModelId == model)
                && (m.UserId == userId || m.UserId == "public"))
            .Select(m => m.MetaJson)
            .FirstOrDefaultAsync(ct);
        if (metaJson is null)
        {
            return null;
        }
        try
        {
            var meta = JsonSerializer.Deserialize<JsonElement>(metaJson);
            if (meta.TryGetProperty("cost", out var cost)
                && cost.TryGetProperty("input", out var input)
                && cost.TryGetProperty("output", out var output)
                && input.TryGetDouble(out var inRate)
                && output.TryGetDouble(out var outRate))
            {
                return promptTokens * inRate / 1_000_000d
                    + completionTokens * outRate / 1_000_000d;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private async Task FinishAsync(
        ChatRun run, string content, CancellationToken ct, string? status = null,
        IReadOnlyList<ChatCompletionMessage>? toolMessages = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        run.Status = status ?? ChatRunStatus.Completed;
        run.CompletedAt = now;
        run.PartialContent = content;

        // RF-006 run-recovery: lastSeq final no meta — posição do stream
        // de eventos consultável pelo response da run.
        var lastSeq = await db.ChatRunEvents.AsNoTracking()
            .Where(e => e.RunId == run.Id)
            .Select(e => (long?)e.Seq)
            .MaxAsync(CancellationToken.None);
        if (lastSeq is { } seq)
        {
            var meta = ReadMetaDict(run.MetaJson);
            meta["lastSeq"] = seq;
            run.MetaJson = JsonSerializer.Serialize(meta, JsonOptions);
        }

        if (content.Length > 0 || toolMessages is { Count: > 0 })
        {
            var chat = await db.Chats.Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == run.ChatId, CancellationToken.None);
            if (chat is not null)
            {
                // As mensagens da run penduram em cadeia no leaf ativo:
                // cada nova é filha da anterior, e a última vira o leaf
                // (RF-001 — o caminho ativo avança com a resposta).
                var nextPosition = chat.Messages.Count == 0
                    ? 0
                    : chat.Messages.Max(m => m.Position) + 1;
                var parentId = BranchService.ResolveLeaf(chat)?.Id;
                ChatMessage? lastAdded = null;
                foreach (var m in toolMessages ?? [])
                {
                    var message = new ChatMessage
                    {
                        ChatId = chat.Id,
                        Role = m.Role,
                        Content = m.Content,
                        Model = m.Role == "assistant" ? run.Model : null,
                        ToolCallsJson = m.ToolCallsJson,
                        ToolCallId = m.ToolCallId,
                        Position = nextPosition++,
                        Timestamp = now,
                        ParentId = parentId,
                    };
                    chat.Messages.Add(message);
                    parentId = (lastAdded = message).Id;
                }
                if (content.Length > 0)
                {
                    var message = new ChatMessage
                    {
                        ChatId = chat.Id,
                        Role = "assistant",
                        Content = content,
                        Model = run.Model,
                        Position = nextPosition,
                        Timestamp = now,
                        ParentId = parentId,
                    };
                    chat.Messages.Add(message);
                    lastAdded = message;
                }
                if (lastAdded is not null)
                {
                    chat.LeafMessageId = lastAdded.Id;
                }
                chat.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);
        broadcaster.Publish(
            run.Id,
            $"event: status\ndata: {JsonSerializer.Serialize(new { status = run.Status })}");
    }

    /// <summary>Marca a run como failed antes de iniciar.</summary>
    private async Task FailAsync(ChatRun run, string error, CancellationToken ct)
    {
        logger.LogWarning("Run {RunId} falhou antes de executar: {Error}", run.Id, error);
        run.Status = ChatRunStatus.Failed;
        run.Error = error;
        run.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(CancellationToken.None);
        broadcaster.Publish(run.Id, $"data: {JsonSerializer.Serialize(new { error })}");
        broadcaster.Publish(run.Id, "data: [DONE]");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
