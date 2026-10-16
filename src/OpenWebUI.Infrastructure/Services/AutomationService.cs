using System.Net;
using System.Text.Json;
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
public class AutomationService(
    AppDbContext db,
    ProviderService providers,
    NotificationService notifications,
    SkillDiscoveryService? skills = null,
    McpClientService? mcp = null,
    IHttpClientFactory? httpFactory = null)
{
    /// <summary>Cap do resumo de output gravado no <see cref="AutomationRun"/>.</summary>
    private const int MaxOutputChars = 4096;

    /// <summary>Tipos de ação suportados (RF-001).</summary>
    public static readonly HashSet<string> ActionKinds =
        ["prompt", "skill", "mcp", "reminder", "webhook"];

    /// <summary>Valida o payload <see cref="Automation.ActionJson"/> por kind (RF-008).</summary>
    public static string? ValidateAction(string actionKind, string? actionJson)
    {
        if (!ActionKinds.Contains(actionKind))
        {
            return $"ActionKind deve ser um de: {string.Join(", ", ActionKinds)}.";
        }

        if (actionKind is "prompt")
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(actionJson))
        {
            return $"ActionJson obrigatório para ActionKind '{actionKind}'.";
        }

        try
        {
            using var doc = JsonDocument.Parse(actionJson);
            var e = doc.RootElement;
            return actionKind switch
            {
                "skill" => e.TryGetProperty("skill", out var s) && s.ValueKind == JsonValueKind.String
                    ? null : "ActionJson.skill (string) obrigatório.",
                "mcp" => e.TryGetProperty("server", out var sv) && sv.ValueKind == JsonValueKind.String
                    && e.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String
                    ? null : "ActionJson.server e ActionJson.tool (strings) obrigatórios.",
                "reminder" => e.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String
                    ? null : "ActionJson.title (string) obrigatório.",
                "webhook" => e.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var uri)
                    && (uri.Scheme is "http" or "https")
                    ? null : "ActionJson.url (http/https absoluta) obrigatório.",
                _ => null,
            };
        }
        catch (JsonException)
        {
            return "ActionJson não é JSON válido.";
        }
    }

    /// <summary>Executa a automação agora e registra o run; erros viram run failed.</summary>
    public async Task<AutomationRun> RunNowAsync(
        Automation automation, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var run = new AutomationRun
        {
            AutomationId = automation.Id,
            Status = "ok",
            ActionKind = automation.ActionKind,
            StartedAt = now,
        };

        try
        {
            var outcome = automation.ActionKind switch
            {
                "skill" => await RunSkillAsync(automation, ct),
                "mcp" => await RunMcpAsync(automation, ct),
                "reminder" => await RunReminderAsync(automation, ct),
                "webhook" => await RunWebhookAsync(automation, ct),
                _ => await RunPromptAsync(automation, ct),
            };
            run.ChatId = outcome.ChatId;
            run.Output = outcome.Output is { Length: > MaxOutputChars }
                ? outcome.Output[..MaxOutputChars]
                : outcome.Output;
            if (outcome.Error is not null)
            {
                run.Status = "failed";
                run.Error = outcome.Error;
            }
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

    /// <summary>Resultado interno de um caller de ação.</summary>
    private sealed record ActionOutcome(
        string? ChatId = null, string? Output = null, string? Error = null);

    /// <summary>Caller <c>prompt</c>: completa o prompt e grava no chat (cria ou reusa).</summary>
    private async Task<ActionOutcome> RunPromptAsync(Automation automation, CancellationToken ct) =>
        await CompleteIntoChatAsync(automation, automation.Prompt, automation.ModelId, ct);

    /// <summary>
    /// Caller <c>skill</c>: resolve a skill no escopo global+usuário e usa o
    /// corpo como prompt (RF-002). Skills de repo exigem workspace — a
    /// automação não tem vínculo, então o lookup é global.
    /// </summary>
    private async Task<ActionOutcome> RunSkillAsync(Automation automation, CancellationToken ct)
    {
        if (skills is null)
        {
            return new(Error: "SkillDiscoveryService indisponível.");
        }

        using var doc = JsonDocument.Parse(automation.ActionJson ?? "{}");
        var name = doc.RootElement.GetProperty("skill").GetString()!;
        var args = doc.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() : null;
        var model = doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() : null;

        var entry = skills.Find(skills.Scan(workdir: null), name);
        if (entry is null)
        {
            return new(Error: $"Skill '{name}' não encontrada no escopo global.");
        }

        var prompt = entry.Body
            + (string.IsNullOrWhiteSpace(args) ? "" : $"\n\nArgumentos: {args}");
        return await CompleteIntoChatAsync(
            automation, prompt, model ?? automation.ModelId, ct);
    }

    /// <summary>Caller <c>mcp</c>: <c>tools/call</c> num servidor env:* ou cadastrado (RF-003).</summary>
    private async Task<ActionOutcome> RunMcpAsync(Automation automation, CancellationToken ct)
    {
        if (mcp is null)
        {
            return new(Error: "McpClientService indisponível.");
        }

        using var doc = JsonDocument.Parse(automation.ActionJson ?? "{}");
        var server = doc.RootElement.GetProperty("server").GetString()!;
        var tool = doc.RootElement.GetProperty("tool").GetString()!;
        var argsJson = doc.RootElement.TryGetProperty("args", out var a)
            ? a.GetRawText() : "{}";

        var srv = await mcp.GetServerAsync(server, ct)
            ?? await mcp.GetServerAsync(McpClientService.EnvServerIdPrefix + server, ct);
        return srv is null
            ? new(Error: $"Servidor MCP '{server}' não encontrado.")
            : new(Output: await mcp.CallToolAsync(srv, tool, argsJson, ct));
    }

    /// <summary>Caller <c>reminder</c>: notificação sem LLM (RF-004).</summary>
    private async Task<ActionOutcome> RunReminderAsync(Automation automation, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(automation.ActionJson ?? "{}");
        var title = doc.RootElement.GetProperty("title").GetString()!;
        var body = doc.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString() : null;
        await notifications.DispatchAsync("automation.reminder",
            new { automation.Id, automation.Name, title, body }, automation.UserId, ct);
        return new(Output: $"Notificação '{title}' enviada.");
    }

    /// <summary>Caller <c>webhook</c>: POST com guard SSRF e timeout 15s (RF-005).</summary>
    private async Task<ActionOutcome> RunWebhookAsync(Automation automation, CancellationToken ct)
    {
        if (httpFactory is null)
        {
            return new(Error: "HttpClientFactory indisponível.");
        }

        using var doc = JsonDocument.Parse(automation.ActionJson ?? "{}");
        var url = doc.RootElement.GetProperty("url").GetString()!;
        var uri = new Uri(url, UriKind.Absolute);
        if (!IsSafeWebhookHost(uri))
        {
            return new(Error: $"URL bloqueada (host privado/loopback): {uri.Host}");
        }

        var method = doc.RootElement.TryGetProperty("method", out var me) && me.ValueKind == JsonValueKind.String
            ? me.GetString()!.ToUpperInvariant() : "POST";
        using var req = new HttpRequestMessage(new HttpMethod(method), uri);
        if (method is "POST" or "PUT" or "PATCH")
        {
            req.Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    automationId = automation.Id,
                    automation.Name,
                    at = DateTimeOffset.UtcNow,
                }), System.Text.Encoding.UTF8, "application/json");
        }

        if (doc.RootElement.TryGetProperty("headers", out var hs) && hs.ValueKind == JsonValueKind.Object)
        {
            foreach (var h in hs.EnumerateObject())
            {
                if (h.Value.ValueKind == JsonValueKind.String
                    && !h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    req.Headers.TryAddWithoutValidation(h.Name, h.Value.GetString());
                }
            }
        }

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        try
        {
            using var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return resp.IsSuccessStatusCode
                ? new(Output: $"HTTP {(int)resp.StatusCode}: {body}")
                : new(Error: $"Webhook respondeu {(int)resp.StatusCode}: {body[..Math.Min(500, body.Length)]}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(Error: "Webhook timeout (15s).");
        }
    }

    /// <summary>Host permitido para webhook: http(s) público; bloqueia loopback/privados/metadata.</summary>
    internal static bool IsSafeWebhookHost(Uri uri)
    {
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            return true;
        }

        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => bytes[0] switch
            {
                0 or 10 or 127 => false,
                169 => bytes[1] != 254,
                172 => bytes[1] < 16 || bytes[1] > 31,
                192 => bytes[1] != 168,
                _ => true,
            },
            System.Net.Sockets.AddressFamily.InterNetworkV6 =>
                !ip.Equals(IPAddress.IPv6Loopback) && !ip.IsIPv6LinkLocal
                && !ip.IsIPv6SiteLocal && !(bytes[0] is 0xfc or 0xfd),
            _ => false,
        };
    }

    /// <summary>Completa um prompt e grava user+assistant no chat (novo ou reusado).</summary>
    private async Task<ActionOutcome> CompleteIntoChatAsync(
        Automation automation, string prompt, string modelId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var chat = automation.ChatId is { } cid
            ? await db.Chats.Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == cid && c.UserId == automation.UserId, ct)
            : null;
        var isNew = chat is null;
        chat ??= new Chat
        {
            UserId = automation.UserId,
            Title = automation.Name,
            ModelsJson = $"[\"{modelId}\"]",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var reply = await providers.CompleteAsync(
            new ChatCompletionRequest(
                modelId,
                [new ChatCompletionMessage("user", prompt)],
                Stream: false),
            automation.UserId, ct);

        var position = chat.Messages.Count == 0 ? 0 : chat.Messages.Max(m => m.Position) + 1;
        var userMsg = new ChatMessage
        {
            ChatId = chat.Id,
            Role = "user",
            Content = prompt,
            Position = position,
            Timestamp = now,
        };
        chat.Messages.Add(userMsg);
        var assistantMsg = new ChatMessage
        {
            ChatId = chat.Id,
            Role = "assistant",
            Content = reply,
            Model = modelId,
            Position = position + 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ParentId = userMsg.Id,
        };
        chat.Messages.Add(assistantMsg);
        chat.LeafMessageId = assistantMsg.Id;
        if (isNew)
        {
            db.Chats.Add(chat);
        }

        return new(ChatId: chat.Id);
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
