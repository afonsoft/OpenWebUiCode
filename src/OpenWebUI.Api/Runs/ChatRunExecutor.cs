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

        var content = new StringBuilder();
        var sinceCheckpoint = 0;
        IReadOnlyList<ChatCompletionMessage>? toolMessages = null;

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

            // Compactação de contexto (SPEC-20261010-context-compaction): aplica
            // o resumo persistido, compacta o head se o histórico estourou o
            // limiar e re-aplica — a janela ao modelo sai [resumo] + tail.
            await compaction.MaybeCompactAsync(
                run.ChatId, request.Messages, request.Model, ct, run.UserId);
            var compacted = await compaction.ApplyAsync(
                run.ChatId, request.Messages, ct);
            if (compacted.Count != request.Messages.Count)
            {
                request = request with { Messages = compacted };
            }

            // Modo do agente do chat (SPEC-20261009 RF-001): plan/build
            // guia o prompt e o spec de tools anunciado. SPEC-15: build
            // sem repositório vinculado cai para chat.
            var chatMode = await EffectiveModeAsync(run.ChatId, ct);

            var effective = await ChatPipeline.EnrichRequestAsync(
                request, user, db, config, rag, webSearch, repos, ct, chatMode,
                run.ChatId);

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

            var outletRules = ModelFilterService.OutletRules(
                ModelFilterService.Parse(await db.ModelEntries.AsNoTracking()
                    .Where(m => m.IsActive && (m.Id == request.Model || m.Name == request.Model)
                        && (m.UserId == user.Id || m.UserId == "public"))
                    .Select(m => m.MetaJson).FirstOrDefaultAsync(ct)));

            var tools = request.ToolIds is { Count: > 0 }
                ? await toolExecutor.LoadEnabledAsync(user.Id, request.ToolIds, ct)
                : null;
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
                var callbacks = new ChatPipeline.ToolLoopCallbacks(
                    // Checkpoint de pausa em cada iteração do loop (RF-013):
                    // fase nova só começa depois do resume.
                    OnPhaseAsync: async (phase, label, t) =>
                    {
                        await pauses.WaitIfPausedAsync(run.Id, t);
                        await PublishPhaseAsync(run, phase, label);
                    },
                    OnCallAsync: (call, t) => PublishToolCallAsync(run, call),
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
                            sinceCheckpoint = 0;
                        }
                    });
                var outcome = await ChatPipeline.RunToolLoopAsync(
                    effective, tools, toolExecutor, providers, ct, callbacks, builtinContext);
                toolMessages = outcome?.ToolMessages;
            }
            else
            {
                // Sem tool loop o conselho só sai como evento — não há
                // fronteira de rodada para a nota de contexto.
                await KickAdvisorAsync(run, "user_message", null, ct);
                await foreach (var line in providers.StreamCompletionAsync(effective, ct))
                {
                    await pauses.WaitIfPausedAsync(run.Id, ct);
                    var processed = ModelFilterService.ProcessSseLine(line, outletRules);
                    broadcaster.Publish(run.Id, processed);
                    AccumulateDelta(processed, content);
                    sinceCheckpoint += processed.Length;
                    if (sinceCheckpoint >= CheckpointBytes)
                    {
                        run.PartialContent = content.ToString();
                        await db.SaveChangesAsync(CancellationToken.None);
                        sinceCheckpoint = 0;
                    }
                }
            }

            // [DONE] depois do FinishAsync: o cliente recarrega o chat ao ver o
            // marcador — a mensagem precisa já estar persistida.
            await FinishAsync(run, content.ToString(), ct, toolMessages: toolMessages);
            broadcaster.Publish(run.Id, "data: [DONE]");
        }
        catch (OperationCanceledException)
        {
            approvals.Cancel(run.Id);
            await FinishAsync(run, content.ToString(), ct, ChatRunStatus.Stopped, toolMessages);
            broadcaster.Publish(run.Id, "data: [DONE]");
        }
        catch (Exception ex)
        {
            // Qualquer falha fecha a run como failed — uma exceção que escapasse
            // deixaria a run eternamente "running" (o dispatcher só loga).
            if (ex is not (InvalidOperationException or HttpRequestException or TaskCanceledException))
            {
                logger.LogError(ex, "Run {RunId} falhou com exceção inesperada.", run.Id);
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
    private Task PublishPhaseAsync(ChatRun run, string phase, string? label)
    {
        broadcaster.Publish(run.Id,
            $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent(phase, label), JsonOptions)}");
        return Task.CompletedTask;
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
                chat.Messages.Add(new ChatMessage
                {
                    ChatId = chat.Id, Role = "user", Content = steer.Content,
                    Position = pos, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
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
        });
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
                var persisted = await db.ChatMessages.AsNoTracking()
                    .Where(m => m.ChatId == run.ChatId)
                    .OrderBy(m => m.Position)
                    .ToListAsync(ct);
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
        var question = "?";
        string[] options = [];
        var multiple = false;
        try
        {
            var args = JsonDocument.Parse(call.ArgumentsJson).RootElement;
            question = args.TryGetProperty("question", out var q) && q.GetString() is { Length: > 0 } qt
                ? qt
                : question;
            if (args.TryGetProperty("options", out var opts)
                && opts.ValueKind == JsonValueKind.Array)
            {
                options = [.. opts.EnumerateArray()
                    .Select(o => o.GetString())
                    .Where(o => !string.IsNullOrWhiteSpace(o))
                    .Select(o => o!)
                    .Take(6)];
            }
            multiple = args.TryGetProperty("multiple", out var m)
                && m.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return new ToolGateDecision(true, Output: "Pergunta malformada (args inválidos).");
        }

        // SPEC-15 RF-003: preset autônomo + opções → responde sozinho com a
        // recomendada (marcada) ou a primeira, sem pausar a run. Pergunta
        // de resposta livre (sem opções) segue aguardando o dono.
        if (IsAutoPickPreset(preset) && options.Length > 0)
        {
            var pick = PickRecommended(options);
            PublishDecisionAuto(run, call.Id, "ask_user", pick);
            return new ToolGateDecision(true, Output: pick);
        }

        broadcaster.Publish(run.Id,
            $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent("awaiting_approval", call.Name), JsonOptions)}");
        broadcaster.Publish(run.Id,
            $"event: question_asked\ndata: {JsonSerializer.Serialize(new RunQuestionAskedEvent(call.Id, Scrub(Truncate(question, PreviewChars)), options, multiple), JsonOptions)}");

        var result = await approvals.WaitAsync(run.Id, run.ChatId, call.Id, call.Name, ct);
        var answer = result.Approved && !string.IsNullOrWhiteSpace(result.Message)
            ? Scrub(Truncate(result.Message!, PreviewChars))
            : "O usuário não respondeu.";
        return new ToolGateDecision(true, Output: answer);
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
        System.Text.RegularExpressions.RegexOptions.Compiled);

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
    /// Grava as mensagens do loop de tools (assistant com ToolCallsJson +
    /// respostas role=tool) e a mensagem final do assistant no chat, e
    /// finaliza a run.
    /// </summary>
    private async Task FinishAsync(
        ChatRun run, string content, CancellationToken ct, string? status = null,
        IReadOnlyList<ChatCompletionMessage>? toolMessages = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        run.Status = status ?? ChatRunStatus.Completed;
        run.CompletedAt = now;
        run.PartialContent = content;

        if (content.Length > 0 || toolMessages is { Count: > 0 })
        {
            var chat = await db.Chats.Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == run.ChatId, CancellationToken.None);
            if (chat is not null)
            {
                var nextPosition = chat.Messages.Count == 0
                    ? 0
                    : chat.Messages.Max(m => m.Position) + 1;
                foreach (var m in toolMessages ?? [])
                {
                    chat.Messages.Add(new ChatMessage
                    {
                        ChatId = chat.Id,
                        Role = m.Role,
                        Content = m.Content,
                        Model = m.Role == "assistant" ? run.Model : null,
                        ToolCallsJson = m.ToolCallsJson,
                        ToolCallId = m.ToolCallId,
                        Position = nextPosition++,
                        Timestamp = now,
                    });
                }
                if (content.Length > 0)
                {
                    chat.Messages.Add(new ChatMessage
                    {
                        ChatId = chat.Id,
                        Role = "assistant",
                        Content = content,
                        Model = run.Model,
                        Position = nextPosition,
                        Timestamp = now,
                    });
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
