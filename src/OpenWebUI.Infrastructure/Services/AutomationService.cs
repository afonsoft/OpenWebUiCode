using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Cálculo do próximo horário de execução de uma automação (UTC).</summary>
public static class AutomationSchedule
{
    /// <summary>Próxima execução em epoch seconds, ou null se a agenda estiver inválida.</summary>
    public static long? ComputeNextRun(Automation automation, DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        return automation.ScheduleKind switch
        {
            "interval" => utc.AddMinutes(Math.Max(1, automation.IntervalMinutes)).ToUnixTimeSeconds(),
            "daily" => NextDaily(utc, automation.TimeOfDay),
            "weekly" => NextWeekly(utc, automation.Weekday ?? 0, automation.TimeOfDay),
            // Execução única (reminder): mantém o horário já armazenado enquanto
            // futuro; depois de disparar, devolve null e nunca reagenda.
            "once" => automation.NextRunAt is long at && at > utc.ToUnixTimeSeconds()
                ? at
                : null,
            _ => null,
        };
    }

    private static long? NextDaily(DateTimeOffset now, string? timeOfDay)
    {
        if (!TryParseTime(timeOfDay, out var time))
        {
            return null;
        }

        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day,
            time.Hour, time.Minute, 0, TimeSpan.Zero);
        if (candidate <= now)
        {
            candidate = candidate.AddDays(1);
        }

        return candidate.ToUnixTimeSeconds();
    }

    private static long? NextWeekly(DateTimeOffset now, int weekday, string? timeOfDay)
    {
        if (!TryParseTime(timeOfDay, out var time))
        {
            return null;
        }

        var day = ((DayOfWeek)Math.Clamp(weekday, 0, 6));
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day,
            time.Hour, time.Minute, 0, TimeSpan.Zero);
        var daysUntil = ((int)day - (int)now.DayOfWeek + 7) % 7;
        candidate = candidate.AddDays(daysUntil);
        if (candidate <= now)
        {
            candidate = candidate.AddDays(7);
        }

        return candidate.ToUnixTimeSeconds();
    }

    private static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", out time);
}

/// <summary>Serviço de automações: criação, validação e execução imediata.</summary>
public class AutomationService(AppDbContext db, ProviderService providers, NotificationService notifications)
{
    /// <summary>Executa a automação agora e registra o run; erros viram run failed.</summary>
    public async Task<AutomationRun> RunNowAsync(
        Automation automation, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var run = new AutomationRun
        {
            AutomationId = automation.Id,
            Status = "ok",
            StartedAt = now,
        };

        try
        {
            var chat = new Chat
            {
                UserId = automation.UserId,
                Title = automation.Name,
                ModelsJson = $"[\"{automation.ModelId}\"]",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var reply = await providers.CompleteAsync(
                new ChatCompletionRequest(
                    automation.ModelId,
                    [new ChatCompletionMessage("user", automation.Prompt)],
                    Stream: false),
                automation.UserId, ct);

            var userMsg = new ChatMessage
            {
                ChatId = chat.Id,
                Role = "user",
                Content = automation.Prompt,
                Position = 0,
                Timestamp = now,
            };
            chat.Messages.Add(userMsg);
            var assistantMsg = new ChatMessage
            {
                ChatId = chat.Id,
                Role = "assistant",
                Content = reply,
                Model = automation.ModelId,
                Position = 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ParentId = userMsg.Id,
            };
            chat.Messages.Add(assistantMsg);
            chat.LeafMessageId = assistantMsg.Id;
            db.Chats.Add(chat);
            run.ChatId = chat.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = "failed";
            run.Error = ex.Message;
        }

        run.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        db.AutomationRuns.Add(run);
        await db.SaveChangesAsync(ct);
        // Lembrete (once) precisa aparecer pro usuário — o chat criado
        // silenciosamente não basta.
        if (run.Status == "ok" && automation.ScheduleKind == "once")
        {
            await notifications.DispatchAsync("automation.reminder",
                new { automation.Id, automation.Name, chatId = run.ChatId }, automation.UserId, ct);
        }
        if (run.Status == "failed")
        {
            await notifications.DispatchAsync("automation.failed",
                new { automation.Id, automation.Name, error = run.Error }, automation.UserId, ct);
        }
        return run;
    }

    /// <summary>Reagenda a próxima execução de uma automação.</summary>
    public void Reschedule(Automation automation)
    {
        automation.NextRunAt = automation.Enabled
            ? AutomationSchedule.ComputeNextRun(automation, DateTimeOffset.UtcNow)
            : null;
    }
}

/// <summary>BackgroundService que dispara runs de automações no horário.</summary>
public class AutomationScheduler(IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                // Um tick com falha não derruba o scheduler.
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown gracioso — não é falha do scheduler.
                break;
            }
        }
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<AutomationService>();

        var due = await db.Automations
            .Where(a => a.Enabled && a.NextRunAt != null && a.NextRunAt <= now)
            .ToListAsync(ct);

        foreach (var automation in due)
        {
            try
            {
                await service.RunNowAsync(automation, ct);
            }
            finally
            {
                // Reagenda sempre — falha num run não bloqueia as próximas execuções.
                var freshNow = DateTimeOffset.UtcNow;
                automation.NextRunAt =
                    AutomationSchedule.ComputeNextRun(automation, freshNow);
                await db.SaveChangesAsync(ct);
            }
        }
    }
}
