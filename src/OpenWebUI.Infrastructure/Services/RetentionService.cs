using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Retenção de dados transientes (SPEC-20261010-memory-autosync-retention):
/// expurgo diário de registros operacionais velhos (AutomationRuns,
/// Notifications, steers de runs terminadas), poda de worktrees órfãs via
/// <see cref="WorktreeService.PruneOrphansAsync"/>, checkpoint do WAL a cada
/// tick e VACUUM semanal no SQLite. Nunca toca em dados primários do usuário
/// (chats, mensagens, arquivos, memórias).
/// </summary>
public class RetentionService(
    AppDbContext db, ConfigService config, WorktreeService worktrees,
    MemorySyncService memorySync,
    ILogger<RetentionService> logger,
    ToolOutputService? toolOutputs = null)
{
    /// <summary>Dias de retenção das execuções de automações.</summary>
    public const int AutomationRunDays = 90;

    /// <summary>Dias de retenção das notificações.</summary>
    public const int NotificationDays = 60;

    /// <summary>Dias de retenção de steers de runs terminadas.</summary>
    public const int SteerDays = 30;

    /// <summary>Dias mínimos entre VACUUMs.</summary>
    public const int VacuumDays = 7;

    /// <summary>Chats podados por ciclo (RF-003).</summary>
    public const int MaxChatsPerCycle = 20;

    /// <summary>Config global de dias de retenção de chats (0 = off, opt-in).</summary>
    public const string ChatDaysKey = "retention.chatDays";

    /// <summary>Kv com o resultado do último ciclo (JSON).</summary>
    private const string LastCycleKey = "sys:maintenance.last";

    private const int Day = 24 * 3600;

    // 409 no endpoint quando um ciclo já está em andamento (RF-005).
    private static int _cycleRunning;

    /// <summary>Tenta marcar um ciclo como em andamento; false se já há um.</summary>
    public static bool TryEnterCycle() =>
        Interlocked.CompareExchange(ref _cycleRunning, 1, 0) == 0;

    /// <summary>Libera a marca de ciclo em andamento.</summary>
    public static void ExitCycle() => Interlocked.Exchange(ref _cycleRunning, 0);

    /// <summary>Executa um ciclo completo de retenção.</summary>
    public async Task RunOnceAsync(CancellationToken ct) =>
        await RunCycleAsync(dryRun: false, ct);

    /// <summary>
    /// Ciclo completo: expurgo operacional + poda de chats por idade
    /// (SPEC-20261016-session-maintenance) + checkpoint/VACUUM. Persiste o
    /// resultado em <c>sys:maintenance.last</c>. Em <paramref name="dryRun"/>
    /// nada é deletado nem destilado — só conta os elegíveis.
    /// </summary>
    public async Task<MaintenanceCycleResult> RunCycleAsync(
        bool dryRun, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var now = startedAt.ToUnixTimeSeconds();
        var opsPurged = 0;
        var chatsPurged = 0;
        var memoriesSynthesized = 0;

        if (!dryRun)
        {
            var runs = await db.AutomationRuns
                .Where(r => r.FinishedAt < now - AutomationRunDays * Day)
                .ExecuteDeleteAsync(ct);
            var notifications = await db.Notifications
                .Where(n => n.CreatedAt < now - NotificationDays * Day)
                .ExecuteDeleteAsync(ct);
            var terminalRunIds = db.ChatRuns
                .Where(r => r.Status == ChatRunStatus.Completed
                    || r.Status == ChatRunStatus.Failed
                    || r.Status == ChatRunStatus.Stopped
                    || r.Status == ChatRunStatus.Interrupted)
                .Select(r => r.Id);
            var steers = await db.ChatRunSteers
                .Where(s => s.Timestamp < now - SteerDays * Day
                    && terminalRunIds.Contains(s.RunId))
                .ExecuteDeleteAsync(ct);
            opsPurged = runs + notifications + steers;
            if (opsPurged > 0)
            {
                logger.LogInformation(
                    "Retenção: {Runs} automation-runs, {Notifications} notificações, "
                        + "{Steers} steers expurgados.",
                    runs, notifications, steers);
            }
        }

        // Poda de chats por idade — opt-in via retention.chatDays (RF-001).
        var chatDays = Math.Clamp(
            await config.GetAsync(ChatDaysKey, 0, ct), 0, 3650);
        var eligible = chatDays > 0
            ? await EligibleChatsAsync(now - (long)chatDays * Day, ct)
            : [];
        foreach (var chat in eligible)
        {
            ct.ThrowIfCancellationRequested();
            if (dryRun)
            {
                chatsPurged++;
                continue;
            }

            // RF-002: destila antes de apagar; falha no provider → skip
            // (o chat retenta no próximo ciclo).
            if (!await memorySync.DistillChatAsync(chat.UserId, chat, ct))
            {
                continue;
            }

            memoriesSynthesized++;
            db.Chats.Remove(chat);
            chatsPurged++;
        }
        if (!dryRun && chatsPurged > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        var vacuumed = false;
        if (!dryRun)
        {
            await worktrees.PruneOrphansAsync(ct);
            // Arquivos gerenciados de tool-output >7d (RF-004 do SPEC
            // tool-output-bounding) — limpeza barata junto do ciclo.
            if (toolOutputs is not null)
            {
                await toolOutputs.CleanupAsync(ct: ct);
            }

            await CheckpointAsync(ct);
            vacuumed = await MaybeVacuumAsync(now, ct);
        }

        var result = new MaintenanceCycleResult(
            startedAt.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            chatsPurged, memoriesSynthesized, opsPurged, vacuumed, chatDays);
        if (!dryRun)
        {
            await config.SetAsync(LastCycleKey, result, ct);
        }
        return result;
    }

    /// <summary>
    /// Candidatos à poda (RF-003/RF-006): <c>UpdatedAt</c> antes do cutoff,
    /// não pinados, sem run running/paused e sem binding de workspace
    /// (<c>chat:{id}:workspace.repo</c>). Cap de <see cref="MaxChatsPerCycle"/>.
    /// </summary>
    public async Task<List<Chat>> EligibleChatsAsync(long cutoff, CancellationToken ct)
    {
        var activeChatIds = db.ChatRuns
            .Where(r => r.Status == ChatRunStatus.Running
                || r.Status == ChatRunStatus.Paused
                || r.Status == ChatRunStatus.Queued)
            .Select(r => r.ChatId);
        var boundChatIds = await db.ConfigEntries
            .Where(e => e.Key.StartsWith("chat:")
                && e.Key.EndsWith(":workspace.repo"))
            .Select(e => e.Key)
            .ToListAsync(ct);
        var bound = boundChatIds
            .Select(k => k["chat:".Length..^":workspace.repo".Length])
            .ToHashSet(StringComparer.Ordinal);

        var candidates = await db.Chats
            .Include(c => c.Messages)
            .Where(c => c.UpdatedAt < cutoff && !c.Pinned
                && !activeChatIds.Contains(c.Id))
            .OrderBy(c => c.UpdatedAt)
            .Take(MaxChatsPerCycle * 2)
            .ToListAsync(ct);
        return [.. candidates.Where(c => !bound.Contains(c.Id)).Take(MaxChatsPerCycle)];
    }

    /// <summary>Status do último ciclo + config atual (RF-004).</summary>
    public async Task<MaintenanceStatus> GetStatusAsync(CancellationToken ct)
    {
        var last = await config.GetAsync<MaintenanceCycleResult?>(LastCycleKey, null, ct);
        var chatDays = await config.GetAsync(ChatDaysKey, 0, ct);
        var nextRunAt = last is null
            ? (long?)null
            : last.FinishedAt + 24 * Day; // RetentionScheduler tick = 24h
        var dbFile = db.Database.GetDbConnection().DataSource;
        long dbSize = 0;
        try
        {
            if (File.Exists(dbFile))
            {
                dbSize = new FileInfo(dbFile).Length;
            }
        }
        catch (Exception)
        {
            // Tamanho é informativo — nunca quebra o status.
        }
        return new MaintenanceStatus(last, chatDays, nextRunAt, dbSize);
    }

    /// <summary>Checkpoint do WAL a cada tick — mantém o -wal pequeno.</summary>
    private async Task CheckpointAsync(CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "PRAGMA wal_checkpoint(TRUNCATE)", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "wal_checkpoint falhou.");
        }
    }

    /// <summary>VACUUM semanal — kv <c>sys:vacuum.last</c> guarda o último.</summary>
    private async Task<bool> MaybeVacuumAsync(long now, CancellationToken ct)
    {
        var last = await config.GetAsync("sys:vacuum.last", 0L, ct);
        if (now - last < VacuumDays * Day)
        {
            return false;
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync("VACUUM", ct);
            await config.SetAsync("sys:vacuum.last", now, ct);
            logger.LogInformation("VACUUM executado pela retenção.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Base ocupada — tenta no próximo tick, sem derrubar o serviço.
            logger.LogWarning(ex, "VACUUM falhou.");
            return false;
        }
    }
}

/// <summary>Resultado de um ciclo de manutenção (persistido em kv).</summary>
public sealed record MaintenanceCycleResult(
    long StartedAt,
    long FinishedAt,
    int ChatsPurged,
    int MemoriesSynthesized,
    int OpsPurged,
    bool Vacuumed,
    int ChatDays);

/// <summary>Status exposto por <c>GET /api/v1/maintenance/status</c>.</summary>
public sealed record MaintenanceStatus(
    MaintenanceCycleResult? LastRun,
    int ChatDays,
    long? NextRunAt,
    long DbSizeBytes);

/// <summary>BackgroundService que roda a retenção uma vez por dia.</summary>
public class RetentionScheduler(
    IServiceScopeFactory scopeFactory, ILogger<RetentionScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromHours(24);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<RetentionService>();
                await service.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Tick da retenção falhou.");
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
