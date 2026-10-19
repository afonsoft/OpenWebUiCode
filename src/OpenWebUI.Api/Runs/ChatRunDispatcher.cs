using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Runs;

/// <summary>
/// Dispatcher das runs de chat desacopladas
/// (SPEC-20261007-chat-detached-runs): fila em memória com FIFO por chat,
/// no máximo <see cref="MaxConcurrent"/> execuções simultâneas, sweep de
/// runs órfãs no boot (queued/running → interrupted) e cancelamento por
/// stop. Runs são enfileiradas pelo endpoint de mensagens e executadas em
/// escopo EF próprio — a conexão do cliente não participa.
/// </summary>
public sealed class ChatRunDispatcher(
    IServiceScopeFactory scopeFactory,
    ChatRunBroadcaster broadcaster,
    ChatJobService jobs,
    ChatRunPauses pauses,
    WorktreeService worktrees,
    IConfiguration configuration,
    ILogger<ChatRunDispatcher> logger) : BackgroundService, IChatRunDispatcher
{
    /// <summary>Máximo padrão de runs executando em paralelo (override via ChatRuns:MaxConcurrent).</summary>
    public const int MaxConcurrent = 4;

    /// <summary>
    /// Identidade do processo (SPEC-20261016-run-recovery RF-005): cada
    /// instância gera um guid no boot — uma run com heartbeat fresco de
    /// outro dono nunca é retomada aqui.
    /// </summary>
    private static readonly string InstanceId = Guid.NewGuid().ToString("N");

    private readonly System.Threading.Channels.Channel<string> _queue =
        System.Threading.Channels.Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _chatLocks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runCancels = new();

    /// <summary>Enfileira uma run recém-criada (idempotente).</summary>
    public void Enqueue(string runId) => _queue.Writer.TryWrite(runId);

    /// <summary>
    /// Pede a interrupção de uma run em execução. Retorna false quando a run
    /// não está ativa (queued ou já finalizada — o endpoint marca queued →
    /// stopped direto no banco).
    /// </summary>
    public bool TryStop(string runId)
    {
        if (_runCancels.TryGetValue(runId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepOrphansAsync(stoppingToken);
        // Jobs running órfãos de restart: o processo não existe mais —
        // marca killed (SPEC-20261007-chat-agent-tools RF-004).
        await jobs.SweepOrphansAsync(stoppingToken);
        // Worktrees de runs que não existem mais ou já expiraram
        // (E16 S9 RF-005 — spec worktree-format-hooks).
        await worktrees.PruneOrphansAsync(stoppingToken);

        var workers = Enumerable.Range(0, configuration.GetValue("ChatRuns:MaxConcurrent", MaxConcurrent))
            .Select(_ => WorkerAsync(stoppingToken))
            .ToArray();
        await Task.WhenAll(workers);
    }

    /// <summary>
    /// Sweep de runs órfãs no boot (SPEC-20261016-run-recovery RF-003/005):
    /// <list type="bullet">
    /// <item>queued → re-enfileiradas (a fila é em memória e morre com o
    /// processo) — nunca começaram, execução normal;</item>
    /// <item>paused → mantidas (pausa é estado do usuário);</item>
    /// <item>running com heartbeat fresco de <em>outra</em> instância →
    /// intocadas (dono vivo);</item>
    /// <item>running stale com snapshot de loop-state → reivindicadas
    /// (OwnerInstance + heartbeat) e re-enfileiradas para retomada;</item>
    /// <item>running stale sem eventos → interrupted (legado).</item>
    /// </list>
    /// </summary>
    public async Task SweepOrphansAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var orphans = await db.ChatRuns
                .Where(r => r.Status == ChatRunStatus.Queued || r.Status == ChatRunStatus.Running
                    || r.Status == ChatRunStatus.Paused)
                .ToListAsync(ct);
            if (orphans.Count == 0)
            {
                return;
            }

            var runningIds = orphans
                .Where(r => r.Status == ChatRunStatus.Running)
                .Select(r => r.Id)
                .ToList();
            var resumable = runningIds.Count == 0
                ? []
                : await db.ChatRunEvents.AsNoTracking()
                    .Where(e => runningIds.Contains(e.RunId)
                        && e.Type == RunEventWriter.Types.Snapshot)
                    .Select(e => e.RunId)
                    .Distinct()
                    .ToListAsync(ct);
            var resumableSet = resumable.ToHashSet();
            var staleAfter = configuration.GetValue(
                "ChatRuns:ResumeStaleSeconds", 60);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var requeue = new List<string>();
            var resumed = 0;
            var interrupted = 0;

            foreach (var run in orphans)
            {
                switch (ClassifyOrphan(run, resumableSet, now, staleAfter, requeue))
                {
                    case OrphanAction.Resumed:
                        resumed++;
                        break;
                    case OrphanAction.Interrupted:
                        interrupted++;
                        break;
                }
            }

            await db.SaveChangesAsync(ct);
            // Enfileirar só depois do claim persistido — o worker valida
            // OwnerInstance == esta instância para runs já running.
            foreach (var runId in requeue)
            {
                _queue.Writer.TryWrite(runId);
            }
            if (resumed > 0 || interrupted > 0)
            {
                logger.LogWarning(
                    "Sweep de runs órfãs: {Resumed} retomadas, {Interrupted} interrupted, {Requeued} re-enfileiradas.",
                    resumed, interrupted, requeue.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha no sweep de runs órfãs.");
        }
    }

    private enum OrphanAction { Skipped, Resumed, Interrupted }

    /// <summary>Decide o destino de uma run do sweep (ver doc de
    /// <see cref="SweepOrphansAsync"/>). Devolve a ação tomada para a
    /// contabilidade do caller.</summary>
    private static OrphanAction ClassifyOrphan(
        ChatRun run, HashSet<string> resumableSet, long now,
        int staleAfter, List<string> requeue)
    {
        if (run.Status == ChatRunStatus.Paused)
        {
            return OrphanAction.Skipped;
        }
        if (run.Status == ChatRunStatus.Queued)
        {
            requeue.Add(run.Id);
            return OrphanAction.Skipped;
        }
        // running: heartbeat fresco de QUALQUER dono = run viva
        // (esta instância ou outra) — nunca retomar algo em
        // execução. Stale/dono morto → avalia retomada abaixo.
        var alive = run.OwnerInstance is { Length: > 0 }
            && run.HeartbeatAt is { } hb
            && now - hb < staleAfter;
        if (alive)
        {
            return OrphanAction.Skipped;
        }
        if (!resumableSet.Contains(run.Id))
        {
            run.Status = ChatRunStatus.Interrupted;
            run.CompletedAt = now;
            run.Error ??= "Servidor reiniciado durante a execução.";
            return OrphanAction.Interrupted;
        }
        run.OwnerInstance = InstanceId;
        run.HeartbeatAt = now;
        requeue.Add(run.Id);
        return OrphanAction.Resumed;
    }

    private async Task WorkerAsync(CancellationToken stoppingToken)
    {
        await foreach (var runId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ExecuteRunAsync(runId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro inesperado ao despachar run {RunId}.", runId);
                await FailRunBestEffortAsync(runId, ex);
            }
        }
    }

    private async Task ExecuteRunAsync(string runId, CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ChatRuns.FirstOrDefaultAsync(r => r.Id == runId, stoppingToken);
        // RF-005 run-recovery: além de queued o worker pega runs running
        // reivindicadas por esta instância no sweep (retomada pós-restart).
        var claimedResume = run?.Status == ChatRunStatus.Running
            && run.OwnerInstance == InstanceId;
        if (run is null || !(run.Status == ChatRunStatus.Queued || claimedResume))
        {
            return;
        }

        // FIFO por chat: runs do mesmo chat executam em sequência.
        var chatLock = _chatLocks.GetOrAdd(run.ChatId, _ => new SemaphoreSlim(1, 1));
        await chatLock.WaitAsync(stoppingToken);
        try
        {
            // Stop pode ter chegado enquanto aguardava o lock.
            if (!(run.Status == ChatRunStatus.Queued
                || (run.Status == ChatRunStatus.Running
                    && run.OwnerInstance == InstanceId)))
            {
                return;
            }

            run.Status = ChatRunStatus.Running;
            run.StartedAt ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            run.OwnerInstance = InstanceId;
            run.HeartbeatAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync(stoppingToken);
            broadcaster.Publish(runId, "event: status\ndata: {\"status\":\"running\"}");

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _runCancels[runId] = runCts;
            using var heartbeatCts = CancellationTokenSource
                .CreateLinkedTokenSource(runCts.Token);
            var heartbeat = HeartbeatLoopAsync(runId, heartbeatCts.Token);
            try
            {
                var executor = scope.ServiceProvider.GetRequiredService<ChatRunExecutor>();
                await executor.ExecuteAsync(run, runCts.Token);
            }
            finally
            {
                _runCancels.TryRemove(runId, out _);
                pauses.Forget(runId);
                broadcaster.Complete(runId);
                await heartbeatCts.CancelAsync();
                try
                {
                    await heartbeat;
                }
                catch (OperationCanceledException ex)
                {
                    logger.LogTrace(ex,
                        "Heartbeat da run {RunId} encerrado no finally.", runId);
                }
                await NotifyRunFinishedAsync(scope.ServiceProvider, run);
            }
        }
        finally
        {
            chatLock.Release();
        }
    }

    /// <summary>
    /// Heartbeat de ownership (SPEC-20261016-run-recovery RF-005): a cada
    /// <c>ChatRuns:HeartbeatSeconds</c> (15s) grava o timestamp na run —
    /// um heartbeat fresco impede que outra instância a retome.
    /// </summary>
    private async Task HeartbeatLoopAsync(string runId, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(
            Math.Max(1, configuration.GetValue("ChatRuns:HeartbeatSeconds", 15)));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.ChatRuns
                    .Where(r => r.Id == runId
                        && (r.Status == ChatRunStatus.Running
                            || r.Status == ChatRunStatus.Paused))
                    .ExecuteUpdateAsync(s => s.SetProperty(
                        r => r.HeartbeatAt,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                        CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Heartbeat da run {RunId} falhou.", runId);
            }
        }
    }

    /// <summary>
    /// Última rede de segurança: uma exceção fora do executor (ex.: escrita
    /// `database is locked` na transição queued→running) não pode deixar a
    /// run eternamente não-terminal — tenta marcar failed em escopo novo,
    /// com uma retentativa.
    /// </summary>
    private async Task FailRunBestEffortAsync(string runId, Exception cause)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var run = await db.ChatRuns
                    .FirstOrDefaultAsync(r => r.Id == runId, CancellationToken.None);
                if (run is null || run.Status is not (ChatRunStatus.Queued or ChatRunStatus.Running))
                {
                    return;
                }

                run.Status = ChatRunStatus.Failed;
                run.Error = $"Falha interna do dispatcher: {cause.Message}";
                run.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await db.SaveChangesAsync(CancellationToken.None);
                broadcaster.Publish(
                    runId,
                    $"data: {JsonSerializer.Serialize(new { error = run.Error })}");
                broadcaster.Publish(runId, "data: [DONE]");
                broadcaster.Complete(runId);
                await NotifyRunFinishedAsync(scope.ServiceProvider, run);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Falha ao marcar run {RunId} como failed (tentativa {Attempt}).",
                    runId, attempt + 1);
            }
        }
    }

    private async Task NotifyRunFinishedAsync(IServiceProvider services, ChatRun run)
    {
        var finished = new ChatRunFinished(
            run.Id, run.ChatId, run.UserId, run.Model,
            run.Status, run.Error, run.PartialContent);
        await NotifyParentChatAsync(services, run);
        foreach (var notifier in services.GetServices<IChatRunNotifier>())
        {
            try
            {
                await notifier.RunCompletedAsync(finished, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notifier {Type} falhou para run {RunId}.",
                    notifier.GetType().Name, run.Id);
            }
        }
    }

    /// <summary>
    /// SPEC-20261010-subrun-parent-notify: run filha (com
    /// <see cref="ChatRun.ParentRunId"/>) ao chegar num status terminal
    /// anexa uma mensagem marcador no chat PAI — "sub-session X concluiu"
    /// fica visível no transcript e entra no contexto das próximas runs do
    /// pai (o resultado completo segue consultável via builtin:run_result).
    /// Best-effort: falha nunca derruba o término da run.
    /// </summary>
    private async Task NotifyParentChatAsync(IServiceProvider services, ChatRun run)
    {
        if (run.ParentRunId is null)
        {
            return;
        }
        try
        {
            var db = services.GetRequiredService<AppDbContext>();
            var parentChatId = await db.ChatRuns.AsNoTracking()
                .Where(r => r.Id == run.ParentRunId)
                .Select(r => r.ChatId)
                .FirstOrDefaultAsync(CancellationToken.None);
            if (parentChatId is null || parentChatId == run.ChatId)
            {
                // Auto-chain (mesmo chat) não é subtarefa delegada — o
                // marcador "Subtarefa concluída" contaminaria o histórico
                // da própria conversa e das próximas continuações.
                return;
            }
            var childTitle = await db.Chats.AsNoTracking()
                .Where(c => c.Id == run.ChatId)
                .Select(c => c.Title)
                .FirstOrDefaultAsync(CancellationToken.None);

            var parentChat = await db.Chats.Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == parentChatId, CancellationToken.None);
            if (parentChat is null)
            {
                return;
            }

            var position = parentChat.Messages.Count == 0
                ? 0
                : parentChat.Messages.Max(m => m.Position) + 1;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var notice = new ChatMessage
            {
                ChatId = parentChat.Id,
                Role = "assistant",
                Content = $"> Subtarefa concluída ({run.Status}): "
                    + $"{childTitle ?? run.ChatId} — run {run.Id}. "
                    + "Resultado completo via builtin:run_result.",
                Position = position,
                Timestamp = now,
                ParentId = BranchService.ResolveLeaf(parentChat)?.Id,
            };
            parentChat.Messages.Add(notice);
            parentChat.LeafMessageId = notice.Id;
            parentChat.UpdatedAt = now;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Falha ao notificar chat pai da run filha {RunId}.", run.Id);
        }
    }
}
