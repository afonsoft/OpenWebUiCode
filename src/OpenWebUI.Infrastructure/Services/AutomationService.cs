using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        var tz = ResolveTimeZone(automation.TimeZoneId);
        return automation.ScheduleKind switch
        {
            // interval ignora fuso (RF-001): é delta relativo a agora.
            "interval" => utc.AddMinutes(Math.Max(1, automation.IntervalMinutes)).ToUnixTimeSeconds(),
            "daily" => NextDaily(utc, automation.TimeOfDay, tz),
            "weekly" => NextWeekly(utc, automation.Weekday ?? 0, automation.TimeOfDay, tz),
            "cron" => NextCron(utc, automation.CronExpression, tz),
            // Execução única (reminder): mantém o horário já armazenado enquanto
            // futuro; depois de disparar, devolve null e nunca reagenda.
            "once" => automation.NextRunAt is long at && at > utc.ToUnixTimeSeconds()
                ? at
                : null,
            _ => null,
        };
    }

    /// <summary>Fuso IANA da automação; valor ausente/inválido resolve UTC (RF-001).</summary>
    internal static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static long? NextDaily(DateTimeOffset nowUtc, string? timeOfDay, TimeZoneInfo tz)
    {
        if (!TryParseTime(timeOfDay, out var time))
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime;
        var candidateLocal = local.Date.Add(time.ToTimeSpan());
        if (candidateLocal <= local)
        {
            candidateLocal = candidateLocal.AddDays(1);
        }
        return ToUtcEpoch(candidateLocal, tz);
    }

    private static long? NextWeekly(DateTimeOffset nowUtc, int weekday, string? timeOfDay, TimeZoneInfo tz)
    {
        if (!TryParseTime(timeOfDay, out var time))
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime;
        var day = ((DayOfWeek)Math.Clamp(weekday, 0, 6));
        var daysUntil = ((int)day - (int)local.DayOfWeek + 7) % 7;
        var candidateLocal = local.Date.AddDays(daysUntil).Add(time.ToTimeSpan());
        if (candidateLocal <= local)
        {
            candidateLocal = candidateLocal.AddDays(7);
        }
        return ToUtcEpoch(candidateLocal, tz);
    }

    /// <summary>Próxima ocorrência cron no fuso da automação (RF-002).</summary>
    private static long? NextCron(DateTimeOffset nowUtc, string? expression, TimeZoneInfo tz)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return null;
        }
        try
        {
            var next = Cronos.CronExpression.Parse(expression)
                .GetNextOccurrence(nowUtc.UtcDateTime, tz);
            return next is { } n
                ? new DateTimeOffset(DateTime.SpecifyKind(n, DateTimeKind.Utc)).ToUnixTimeSeconds()
                : null;
        }
        catch (Cronos.CronFormatException)
        {
            return null;
        }
    }

    private static long ToUtcEpoch(DateTime localUnspecified, TimeZoneInfo tz) =>
        new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(localUnspecified, tz),
            TimeSpan.Zero).ToUnixTimeSeconds();

    private static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out time);
}

/// <summary>Serviço de automações: criação, validação e execução imediata.</summary>
public class AutomationService(
    AppDbContext db,
    ProviderService providers,
    NotificationService notifications,
    SkillDiscoveryService? skills = null,
    McpClientService? mcp = null,
    IHttpClientFactory? httpFactory = null,
    ComboService? combos = null,
    IConfiguration? config = null,
    OpenWebUI.Application.Interfaces.IChatRunDispatcher? dispatcher = null)
{
    private const string SkillKey = "skill";

    /// <summary>Máximo de tentativas para falhas transitórias (RF-003).</summary>
    internal const int MaxTransientAttempts = 3;

    /// <summary>Delay entre tentativas de retry — injetável em testes.</summary>
    internal Func<int, CancellationToken, Task> RetryDelayAsync { get; set; }
        = (ms, ct) => Task.Delay(TimeSpan.FromMilliseconds(ms), ct);

    private int CircuitBreakerThreshold =>
        Math.Max(1, config?.GetValue("Automation:CircuitBreakerThreshold", 5) ?? 5);

    /// <summary>Cap do resumo de output gravado no <see cref="AutomationRun"/>.</summary>
    private const int MaxOutputChars = 4096;

    /// <summary>Tipos de ação suportados (RF-001).</summary>
    public static readonly HashSet<string> ActionKinds =
        ["prompt", SkillKey, "mcp", "reminder", "webhook", "run"];

    /// <summary>Valida o payload <see cref="Automation.ActionJson"/> por kind (RF-008).</summary>
    public static string? ValidateAction(string actionKind, string? actionJson)
    {
        if (!ActionKinds.Contains(actionKind))
        {
            return $"ActionKind deve ser um de: {string.Join(", ", ActionKinds)}.";
        }

        // prompt/run usam o Prompt da automação como payload — sem ActionJson.
        if (actionKind is "prompt" or "run")
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
            return ValidateActionPayload(actionKind, doc.RootElement);
        }
        catch (JsonException)
        {
            return "ActionJson não é JSON válido.";
        }
    }

    /// <summary>Shape exigido do payload por kind (RF-008) —
    /// <c>null</c> quando válido.</summary>
    private static string? ValidateActionPayload(string actionKind, JsonElement e) =>
        actionKind switch
        {
            SkillKey => e.TryGetProperty(SkillKey, out var s) && s.ValueKind == JsonValueKind.String
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
                SkillKey => await RunSkillAsync(automation, ct),
                "mcp" => await RunMcpAsync(automation, ct),
                "reminder" => await RunReminderAsync(automation, ct),
                "webhook" => await RunWebhookAsync(automation, ct),
                "run" => await RunChatRunAsync(automation, ct),
                _ => await RunPromptAsync(automation, ct),
            };
            run.ChatId = outcome.ChatId;
            run.RunId = outcome.RunId;
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

        // Circuit breaker (SPEC-20261115 RF-003): sucesso zera o contador;
        // N falhas consecutivas desabilitam a automação e avisam o dono.
        var disabled = false;
        if (run.Status == "ok")
        {
            automation.ConsecutiveFailures = 0;
        }
        else
        {
            automation.ConsecutiveFailures++;
            if (automation.ConsecutiveFailures >= CircuitBreakerThreshold
                && automation.Enabled)
            {
                automation.Enabled = false;
                automation.NextRunAt = null;
                disabled = true;
            }
        }
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
        if (disabled)
        {
            await notifications.DispatchAsync("automation.disabled",
                new { automation.Id, automation.Name,
                    error = $"Desabilitada após {automation.ConsecutiveFailures} falhas consecutivas." },
                automation.UserId, ct);
        }
        return run;
    }

    /// <summary>Resultado interno de um caller de ação.</summary>
    private sealed record ActionOutcome(
        string? ChatId = null, string? Output = null, string? Error = null,
        string? RunId = null);

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
        var name = doc.RootElement.GetProperty(SkillKey).GetString()!;
        var args = doc.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() : null;
        var model = doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString() : null;

        var entry = SkillDiscoveryService.Find(skills.Scan(workdir: null), name);
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
        if (srv is null)
        {
            return new(Error: $"Servidor MCP '{server}' não encontrado.");
        }

        // Retry de falhas transitórias (RF-003): exceções de transporte
        // retentam com backoff exponencial 1s→4s + jitter.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new(Output: await mcp.CallToolAsync(srv, tool, argsJson, ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                && attempt < MaxTransientAttempts)
            {
                await RetryDelayAsync(BackoffMs(attempt), ct);
            }
        }
    }

    /// <summary>Backoff exponencial 1s→2s→4s com jitter ±25% (RF-003).</summary>
    internal static int BackoffMs(int attempt) =>
        (int)((1000 << (attempt - 1))
            * (1.0 + (Random.Shared.NextDouble() * 0.5 - 0.25)));

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

        // Retry de falhas transitórias (RF-003): 5xx/408/429, timeout e
        // erro de transporte retentam até MaxTransientAttempts — cada
        // tentativa recria o HttpRequestMessage (não é re-enviável).
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = BuildWebhookRequest(doc, automation, uri, method);
                using var resp = await http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    return new(Output: $"HTTP {(int)resp.StatusCode}: {body}");
                }
                var transient = (int)resp.StatusCode is >= 500 or 408 or 429;
                if (!transient || attempt == MaxTransientAttempts)
                {
                    return new(Error:
                        $"Webhook respondeu {(int)resp.StatusCode}: {body[..Math.Min(500, body.Length)]}");
                }
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt == MaxTransientAttempts)
                {
                    return new(Error: "Webhook timeout (15s).");
                }
            }
            catch (HttpRequestException ex)
            {
                if (attempt == MaxTransientAttempts)
                {
                    return new(Error: $"Webhook falhou: {ex.Message}");
                }
            }
            await RetryDelayAsync(BackoffMs(attempt), ct);
        }
    }

    /// <summary>Monta o request do webhook — body JSON nas verbs com
    /// payload e headers customizados (Host ignorado, é reservado).</summary>
    private static HttpRequestMessage BuildWebhookRequest(
        JsonDocument doc, Automation automation, Uri uri, string method)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), uri);
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

        if (doc.RootElement.TryGetProperty("headers", out var hs)
            && hs.ValueKind == JsonValueKind.Object)
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
        return req;
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
            System.Net.Sockets.AddressFamily.InterNetwork => IsPublicIPv4(bytes),
            System.Net.Sockets.AddressFamily.InterNetworkV6 =>
                !ip.Equals(IPAddress.IPv6Loopback) && !ip.IsIPv6LinkLocal
                && !ip.IsIPv6SiteLocal && !(bytes[0] is 0xfc or 0xfd),
            _ => false,
        };
    }

    /// <summary>IPv4 público — bloqueia 0/8, 10/8, 127/8, link-local e privados.</summary>
    private static bool IsPublicIPv4(byte[] bytes) => bytes[0] switch
    {
        0 or 10 or 127 => false,
        169 => bytes[1] != 254,
        172 => bytes[1] < 16 || bytes[1] > 31,
        192 => bytes[1] != 168,
        _ => true,
    };

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

        var reply = modelId.StartsWith(ComboService.Prefix, StringComparison.Ordinal)
            ? await CompleteWithComboAsync(automation, prompt, modelId, ct)
            : await providers.CompleteAsync(
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

    /// <summary>
    /// Completa via <c>combo:{id}</c> (RF-003 SPEC-20261016-model-combos):
    /// resolve o efetivo, reporta falha transitória e tenta o próximo
    /// candidato. Sem streaming, retry nunca duplica saída.
    /// </summary>
    private async Task<string> CompleteWithComboAsync(
        Automation automation, string prompt, string comboRef, CancellationToken ct)
    {
        if (combos is null)
        {
            throw new InvalidOperationException("ComboService indisponível.");
        }

        var attempt = 0;
        while (true)
        {
            var pick = await combos.ResolveAsync(comboRef, automation.UserId, ct);
            try
            {
                var reply = await providers.CompleteAsync(
                    new ChatCompletionRequest(
                        pick.ModelId,
                        [new ChatCompletionMessage("user", prompt)],
                        Stream: false),
                    automation.UserId, ct);
                await combos.ReportAsync(pick.ComboId, pick.ModelId, ok: true,
                    ct: CancellationToken.None);
                return reply;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                && attempt + 1 < pick.Candidates)
            {
                await combos.ReportAsync(pick.ComboId, pick.ModelId, ok: false,
                    ct: CancellationToken.None);
            }
            attempt++;
        }
    }

    /// <summary>
    /// Caller <c>run</c> (SPEC-20261115-scheduler-advanced RF-003): cria uma
    /// <see cref="ChatRun"/> queued real no chat da automação — herda tools,
    /// worktree, budget gate, recovery e advisor. O prompt vira mensagem
    /// user persistida para compor o histórico da run.
    /// </summary>
    private async Task<ActionOutcome> RunChatRunAsync(Automation automation, CancellationToken ct)
    {
        if (dispatcher is null)
        {
            return new(Error: "ChatRunDispatcher indisponível.");
        }

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
            ModelsJson = $"[\"{automation.ModelId}\"]",
            CreatedAt = now,
            UpdatedAt = now,
        };
        // Preset configurável: allow-readonly (autônomo) ou approve-mutations.
        chat.ApprovalPreset = automation.ApprovalPreset;
        if (isNew)
        {
            db.Chats.Add(chat);
        }

        var position = chat.Messages.Count == 0 ? 0 : chat.Messages.Max(m => m.Position) + 1;
        var userMsg = new ChatMessage
        {
            ChatId = chat.Id,
            Role = "user",
            Content = automation.Prompt,
            Position = position,
            Timestamp = now,
        };
        db.ChatMessages.Add(userMsg);
        chat.LeafMessageId = userMsg.Id;

        var history = chat.Messages
            .Where(m => m.Id != userMsg.Id)
            .OrderBy(m => m.Position)
            .Select(m => new ChatCompletionMessage(
                m.Role, m.Content, m.ToolCallId, m.ToolCallsJson))
            .Append(new ChatCompletionMessage("user", automation.Prompt))
            .ToList();
        var request = new ChatCompletionRequest(
            automation.ModelId, history, Stream: true);
        var chatRun = new ChatRun
        {
            ChatId = chat.Id,
            UserId = automation.UserId,
            Model = automation.ModelId,
            RequestJson = JsonSerializer.Serialize(request, JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(chatRun);
        await db.SaveChangesAsync(ct);

        if (!dispatcher.Enqueue(chatRun.Id))
        {
            return new(ChatId: chat.Id, RunId: chatRun.Id,
                Error: "Fila de runs cheia — run não despachada.");
        }
        return new(ChatId: chat.Id, RunId: chatRun.Id,
            Output: $"ChatRun {chatRun.Id} enfileirada.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Jitter ±5% aplicado sobre o instante calculado (SPEC-20261115
    /// RF-005): automações criadas juntas não disparam todas no mesmo
    /// tick. Não se aplica a <c>once</c>.
    /// </summary>
    public static long? ApplyJitter(long? next, DateTimeOffset now,
        string scheduleKind)
    {
        if (next is not { } at || scheduleKind == "once")
        {
            return next;
        }
        var delta = at - now.ToUniversalTime().ToUnixTimeSeconds();
        if (delta <= 0)
        {
            return next;
        }
        var jitter = 1.0 + ((Random.Shared.NextDouble() * 2 - 1) * 0.05);
        return now.ToUniversalTime().ToUnixTimeSeconds()
            + (long)Math.Max(1, delta * jitter);
    }

    /// <summary>Reagenda a próxima execução de uma automação (com jitter).</summary>
    public static void Reschedule(Automation automation)
    {
        var now = DateTimeOffset.UtcNow;
        automation.NextRunAt = automation.Enabled
            ? ApplyJitter(
                AutomationSchedule.ComputeNextRun(automation, now),
                now, automation.ScheduleKind)
            : null;
    }
}

/// <summary>
/// Scheduler de automações (SPEC-20261115-scheduler-robustness): tick de
/// 15s, dispatch PARALELO das due (cap <c>Automation:MaxConcurrent</c>,
/// default 4) com overlap guard por automação em voo, reschedule com
/// jitter e flag <see cref="AutomationRun.Missed"/> em atraso grande.
/// </summary>
public class AutomationScheduler(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AutomationScheduler> logger)
    : PeriodicBackgroundService(logger)
{
    /// <summary>Automações com run em andamento nesta instância (overlap guard RF-001).</summary>
    private readonly ConcurrentDictionary<string, byte> _inFlight = new();

    /// <inheritdoc />
    protected override TimeSpan Tick => TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override async Task ExecuteTickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        List<string> dueIds;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dueIds = await db.Automations
                .Where(a => a.Enabled && a.NextRunAt != null && a.NextRunAt <= now)
                .Select(a => a.Id)
                .ToListAsync(ct);
        }
        if (dueIds.Count == 0)
        {
            return;
        }

        var maxParallel = Math.Max(1,
            configuration.GetValue("Automation:MaxConcurrent", 4));
        await Parallel.ForEachAsync(
            dueIds,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallel,
                CancellationToken = ct,
            },
            async (automationId, itemCt) =>
            {
                // Overlap guard: run anterior ainda em voo → pula este
                // tick (NextRunAt já passou; o próximo tick re-avalia).
                if (!_inFlight.TryAdd(automationId, 0))
                {
                    logger.LogDebug(
                        "Automação {Id} já tem run em andamento — tick pulado.",
                        automationId);
                    return;
                }
                try
                {
                    await RunOneAsync(automationId, itemCt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Execução agendada da automação {Id} falhou.", automationId);
                }
                finally
                {
                    _inFlight.TryRemove(automationId, out _);
                }
            });
    }

    /// <summary>Executa uma automação em escopo próprio e reagenda sempre
    /// (falha num run não bloqueia as próximas execuções).</summary>
    private async Task RunOneAsync(string automationId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<AutomationService>();
        var automation = await db.Automations
            .FirstOrDefaultAsync(a => a.Id == automationId, ct);
        if (automation is null || !automation.Enabled)
        {
            return;
        }

        var overdue = automation.NextRunAt is { } due
            ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - due
            : 0;
        try
        {
            var run = await service.RunNowAsync(automation, ct);
            run.Missed = overdue > MissedThresholdSeconds(automation);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            AutomationService.Reschedule(automation);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Atraso a partir do qual o run conta como "missed":
    /// 2× o intervalo (kind interval) ou 1 dia (daily/weekly/once).</summary>
    internal static long MissedThresholdSeconds(Automation a) =>
        a.ScheduleKind == "interval"
            ? Math.Max(1, a.IntervalMinutes) * 60L * 2
            : 24L * 3600;
}
