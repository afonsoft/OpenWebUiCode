using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Auto-sync de memória do agente (SPEC-20261010-memory-autosync-retention):
/// a cada tick, destila os chats atualizados desde o último sync em fatos
/// duráveis via LLM e grava como <see cref="AgentMemory"/> scope global com
/// título <c>auto:{chat}</c> — mesmo upsert por título do memory_save.
/// Opt-out por usuário: kv <c>u:{uid}:memsync.enabled</c> = false.
/// </summary>
public class MemorySyncService(
    AppDbContext db, ConfigService config, ProviderService providers,
    UserConfigOverlayService overlay, ModelRoleResolver roles,
    ILogger<MemorySyncService> logger)
{
    /// <summary>Chats processados por usuário a cada sync.</summary>
    private const int MaxChatsPerUser = 3;

    /// <summary>Mensagens mais recentes por chat enviadas à destilação.</summary>
    private const int MaxMessagesPerChat = 20;

    /// <summary>Cap por mensagem no transcript.</summary>
    private const int MaxMessageChars = 500;

    /// <summary>Cap total do transcript enviado ao modelo.</summary>
    private const int MaxTranscriptChars = 6000;

    /// <summary>Máximo de memórias <c>auto:*</c> mantidas por usuário.</summary>
    private const int MaxAutoMemories = 25;

    /// <summary>Cap de caracteres do título derivado do chat.</summary>
    private const int MaxTitleChars = 45;

    /// <summary>Intervalo default entre syncs por usuário (horas).</summary>
    public const int DefaultIntervalHours = 6;

    /// <summary>Janela default de chats elegíveis (dias).</summary>
    public const int DefaultMaxAgeDays = 30;

    /// <summary>Executa um ciclo de sync para todos os usuários com chats.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var userIds = await db.Chats
            .Select(c => c.UserId)
            .Distinct()
            .ToListAsync(ct);
        foreach (var userId in userIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SyncUserAsync(userId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "memsync falhou para o usuário {UserId}", userId);
            }
        }
    }

    private async Task SyncUserAsync(string userId, CancellationToken ct)
    {
        // SPEC-20261014-memory-pipeline RF-004: config em camadas — o
        // global do admin `memsync.enabled=false` é negativa absoluta
        // (custo de provider); senão o usuário decide (default on, opt-out).
        if (await config.GetAsync<bool?>("memsync.enabled", null, ct) == false)
        {
            logger.LogDebug("memsync: global admin desligou — skip {UserId}", userId);
            return;
        }
        var enabled = await overlay.GetAsync(
            userId, "memsync.enabled", true, ct);
        logger.LogDebug(
            "memsync: {UserId} enabled={V} src={S}",
            userId, enabled.Value, enabled.Source);
        if (!enabled.Value)
        {
            logger.LogDebug("memsync: usuário {UserId} optou por desligar", userId);
            return;
        }

        var intervalHours = Math.Clamp(
            (await overlay.GetAsync(userId, "memsync.intervalHours",
                DefaultIntervalHours, ct)).Value, 1, 168);
        var maxAgeDays = Math.Clamp(
            (await overlay.GetAsync(userId, "memsync.maxAgeDays",
                DefaultMaxAgeDays, ct)).Value, 1, 365);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var last = await config.GetAsync($"u:{userId}:memsync.last", 0L, ct);
        if (last > 0 && now - last < intervalHours * 3600L)
        {
            logger.LogDebug(
                "memsync: {UserId} dentro do intervalo ({H}h) — skip",
                userId, intervalHours);
            return;
        }
        var cutoff = now - maxAgeDays * 86400L;
        var chats = await db.Chats
            .Include(c => c.Messages)
            .Where(c => c.UserId == userId
                && c.UpdatedAt > last
                && c.UpdatedAt >= cutoff)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(MaxChatsPerUser)
            .ToListAsync(ct);
        logger.LogDebug("memsync: {UserId} com {Count} chats elegíveis", userId, chats.Count);

        var complete = true;
        foreach (var chat in chats)
        {
            // RF-001/AC: papel `memory` (SPEC-20261013-model-roles) primeiro;
            // sem papel configurado cai no primeiro modelo do chat.
            var model = await roles.ResolveAsync(
                "memory", userId, FirstModel(chat.ModelsJson) ?? string.Empty, ct);
            var transcript = BuildTranscript(chat);
            if (string.IsNullOrWhiteSpace(model) || transcript is null)
            {
                continue;
            }

            string reply;
            try
            {
                reply = await providers.CompleteAsync(
                    new ChatCompletionRequest(
                        model,
                        [new ChatCompletionMessage("user", DistillPrompt + transcript)],
                        Stream: false),
                    userId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Provider fora: não avança o watermark — retenta no próximo tick.
                complete = false;
                logger.LogWarning(ex, "memsync: provider falhou no chat {ChatId}", chat.Id);
                break;
            }
            if (string.IsNullOrWhiteSpace(reply))
            {
                continue;
            }
            await UpsertAutoMemoryAsync(userId, chat, reply.Trim(), ct);
        }

        if (complete)
        {
            await config.SetAsync($"u:{userId}:memsync.last", now, ct);
        }

        // Poda: só as MaxAutoMemories mais recentes sobrevivem.
        var stale = await db.AgentMemories
            .Where(m => m.UserId == userId && m.Title.StartsWith("auto:"))
            .OrderByDescending(m => m.UpdatedAt)
            .Skip(MaxAutoMemories)
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            db.AgentMemories.RemoveRange(stale);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task UpsertAutoMemoryAsync(
        string userId, Chat chat, string content, CancellationToken ct)
    {
        var chatTitle = string.IsNullOrWhiteSpace(chat.Title) ? chat.Id : chat.Title.Trim();
        var title = $"auto:{chatTitle}";
        if (title.Length > MaxTitleChars)
        {
            title = title[..MaxTitleChars];
        }
        if (content.Length > 4000)
        {
            content = content[..4000];
        }

        var existing = await db.AgentMemories.FirstOrDefaultAsync(
            m => m.UserId == userId && m.Title == title, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (existing is null)
        {
            db.AgentMemories.Add(new AgentMemory
            {
                UserId = userId,
                Scope = "global",
                Title = title,
                Content = content,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Content = content;
            existing.UpdatedAt = now;
        }
    }

    private const string DistillPrompt =
        "Abaixo está o transcript de uma conversa entre usuário e assistente. "
            + "Extraia somente fatos duráveis que valem lembrar em sessões futuras "
            + "(preferências do usuário, decisões do projeto, convenções, comandos "
            + "de build/test, nomes de repos/ambientes). Uma linha por fato, no "
            + "máximo 8 linhas, sem preâmbulo nem marcadores extras. Se não houver "
            + "nada durável, responda apenas 'nada'.\n\n";

    private static string? BuildTranscript(Chat chat)
    {
        // Caminho ativo (SPEC-20261013-chat-branching): a destilação lê o
        // ramo corrente, não todos os leafs misturados por Position.
        var messages = BranchService.WalkPath(chat)
            .Where(m => m.Role is "user" or "assistant")
            .TakeLast(MaxMessagesPerChat)
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
}

/// <summary>
/// BackgroundService que acorda a cada hora; a cadência efetiva por usuário
/// é o kv em camadas <c>memsync.intervalHours</c> (SPEC-20261014
/// memory-pipeline RF-004 — default 6h, range 1–168).
/// </summary>
public class MemorySyncScheduler(
    IServiceScopeFactory scopeFactory, ILogger<MemorySyncScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromHours(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);

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
                var service = scope.ServiceProvider.GetRequiredService<MemorySyncService>();
                await service.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Tick do memsync falhou.");
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
