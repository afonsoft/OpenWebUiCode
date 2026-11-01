using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Webhooks de automação (SPEC-20261007-chat-agent-parity RF-020/RF-021):
/// <c>POST /api/v1/hooks/{token}</c> — rota anônima e rate-limited —
/// transforma o corpo da chamada numa mensagem de usuário no chat
/// vinculado e enfileira uma run desacoplada (o resultado chega ao chat
/// e às notificações existentes). n8n usa o alias
/// <c>POST /api/v1/hooks/n8n/{token}</c>. CRUD dos hooks é autenticado;
/// o token é o próprio Id (GUID) do hook.
/// </summary>
public static class AutomationHookEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const int MaxNameChars = 120;
    private const int MaxPromptChars = 4000;

    /// <summary>Teto do payload do webhook (SPEC-20261021-harness-seams RF-D2).</summary>
    private const int MaxPayloadBytes = 256 * 1024;

    /// <summary>Mapeia CRUD autenticado e rotas públicas de disparo.</summary>
    public static IEndpointRouteBuilder MapAutomationHookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hooks").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapGet("/{id}/deliveries", ListDeliveriesAsync);

        // Disparo — anônimo (o token é o segredo), com rate limit.
        app.MapPost("/api/v1/hooks/{token}", FireAsync).AllowAnonymous();
        app.MapPost("/api/v1/hooks/n8n/{token}", FireAsync).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var hooks = await db.AutomationHooks.AsNoTracking()
            .Where(h => h.UserId == user.Id)
            .OrderByDescending(h => h.CreatedAt)
            .Select(h => new AutomationHookResponse(
                h.Id, h.Name, h.ChatId, h.Enabled, h.CreatedAt, h.LastFiredAt,
                $"/api/v1/hooks/{h.Id}"))
            .ToListAsync(ct);
        return Results.Ok(hooks);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var request = await JsonSerializer.DeserializeAsync<AutomationHookCreateRequest>(
            http.Request.Body, JsonOptions, ct);
        var name = (request?.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxNameChars)
        {
            return Results.BadRequest(new { detail = "name é obrigatório (máx. 120)." });
        }

        Chat? chat = null;
        if (request?.ChatId is { } chatId)
        {
            chat = await db.Chats.FirstOrDefaultAsync(
                c => c.Id == chatId && c.UserId == user.Id, ct);
            if (chat is null)
            {
                return Results.NotFound(new { detail = "chatId não encontrado." });
            }
        }
        else
        {
            var now0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            chat = new Chat
            {
                UserId = user.Id,
                Title = $"webhook: {name}",
                CreatedAt = now0,
                UpdatedAt = now0,
            };
            db.Chats.Add(chat);
        }

        var provider = (request?.Provider ?? "generic").Trim().ToLowerInvariant();
        if (provider is not ("generic" or "github"))
        {
            return Results.BadRequest(
                new { detail = "provider deve ser 'generic' ou 'github'." });
        }
        if (request?.AgentMode is { Length: > 0 } am
            && PermissionRuleset.Normalize(am) is null)
        {
            return Results.BadRequest(
                new { detail = "agentMode deve ser chat|plan|build." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hook = new AutomationHook
        {
            UserId = user.Id,
            ChatId = chat.Id,
            Name = name,
            CreatedAt = now,
            Secret = string.IsNullOrWhiteSpace(request?.Secret)
                ? null : request.Secret.Trim(),
            Provider = provider,
            EventsJson = request?.Events is { Length: > 0 } events
                ? JsonSerializer.Serialize(events, JsonOptions)
                : null,
            PromptTemplate = string.IsNullOrWhiteSpace(request?.PromptTemplate)
                ? null : request.PromptTemplate,
            RepoSlug = string.IsNullOrWhiteSpace(request?.RepoSlug)
                ? null : request.RepoSlug.Trim(),
            PersonaId = string.IsNullOrWhiteSpace(request?.PersonaId)
                ? null : request.PersonaId.Trim(),
            AgentMode = string.IsNullOrWhiteSpace(request?.AgentMode)
                ? null : request.AgentMode.Trim().ToLowerInvariant(),
        };
        db.AutomationHooks.Add(hook);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new AutomationHookResponse(
            hook.Id, hook.Name, hook.ChatId, hook.Enabled, hook.CreatedAt,
            hook.LastFiredAt, $"/api/v1/hooks/{hook.Id}"));
    }

    private static async Task<IResult> DeleteAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var deleted = await db.AutomationHooks
            .Where(h => h.Id == id && h.UserId == user.Id)
            .ExecuteDeleteAsync(ct);
        return deleted > 0 ? Results.Ok(new { deleted = true }) : Results.NotFound();
    }

    /// <summary>
    /// Lista entregas/auditoria do hook (RF-D2 harness-seams) — dono only.
    /// </summary>
    private static async Task<IResult> ListDeliveriesAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var owns = await db.AutomationHooks.AsNoTracking()
            .AnyAsync(h => h.Id == id && h.UserId == user.Id, ct);
        if (!owns)
        {
            return Results.NotFound(new { detail = "webhook não encontrado." });
        }

        var deliveries = await db.AutomationHookDeliveries.AsNoTracking()
            .Where(d => d.HookId == id)
            .OrderByDescending(d => d.ReceivedAt)
            .Take(100)
            .Select(d => new AutomationHookDeliveryResponse(
                d.Id, d.DeliveryKey, d.EventType, d.ReceivedAt,
                d.Status, d.RunId, d.Error, d.SecurityWarning))
            .ToListAsync(ct);
        return Results.Ok(deliveries);
    }

    /// <summary>
    /// Disparo de webhook v2 (SPEC-20261021-harness-seams RF-D1/D2):
    /// verificação HMAC-SHA256 quando <c>Secret</c> configurado, filtro de
    /// eventos por provider (github/generic), dedup por delivery id,
    /// template <c>{{path}}</c> no payload, binding/persona/modo aplicados
    /// à run e auditoria em <c>AutomationHookDeliveries</c>. Hooks sem
    /// secret seguem aceitos (legado) com warning na delivery.
    /// </summary>
    private static async Task<IResult> FireAsync(
        HttpContext http, [AsParameters] HookFireDeps deps,
        string token, CancellationToken ct)
    {
        var db = deps.Db;
        var dispatcher = deps.Dispatcher;
        var config = deps.Config;
        var limits = deps.Limits;
        var repos = deps.Repos;
        var rateConfig = await config.GetAsync("ratelimit", RateLimitConfig.Default, ct);
        if (rateConfig.Enabled)
        {
            var key = $"hook:{http.Connection.RemoteIpAddress?.ToString() ?? "anon"}";
            var retryAfter = limits.TryAcquire(
                key, rateConfig, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (retryAfter is { } retry)
            {
                http.Response.Headers.RetryAfter = retry.ToString();
                return Results.Json(
                    new { detail = "Limite de disparos excedido." }, statusCode: 429);
            }
        }

        var hook = await db.AutomationHooks
            .Include(h => h.Chat)
            .FirstOrDefaultAsync(h => h.Id == token && h.Enabled, ct);
        if (hook?.Chat is null)
        {
            return Results.NotFound(new { detail = "webhook não encontrado." });
        }

        // Corpo cru uma vez — a assinatura HMAC é sobre os bytes exatos.
        string raw;
        using (var reader = new StreamReader(http.Request.Body))
        {
            raw = await reader.ReadToEndAsync(ct);
        }
        if (raw.Length > MaxPayloadBytes)
        {
            return Results.Json(
                new { detail = "payload excede 256KB." }, statusCode: 413);
        }

        var isGithub = string.Equals(
            hook.Provider, "github", StringComparison.OrdinalIgnoreCase);
        var eventType = OrNull(isGithub
            ? http.Request.Headers["X-GitHub-Event"].ToString()
            : http.Request.Headers["X-Event-Type"].ToString());
        var deliveryKey = OrNull(isGithub
            ? http.Request.Headers["X-GitHub-Delivery"].ToString()
            : http.Request.Headers["X-Delivery-Id"].ToString())
            ?? OrNull(http.Request.Headers["X-GitHub-Delivery"].ToString());

        // Idempotência (RF-D2): delivery já aceita com a mesma chave →
        // devolve a run original sem criar mensagem/run novas.
        if (deliveryKey is not null)
        {
            var prevRunId = await db.AutomationHookDeliveries.AsNoTracking()
                .Where(d => d.HookId == hook.Id && d.DeliveryKey == deliveryKey
                    && d.Status == "accepted")
                .Select(d => d.RunId)
                .FirstOrDefaultAsync(ct);
            if (prevRunId is not null)
            {
                return Results.Accepted(value: new AutomationHookFireResponse(
                    prevRunId, hook.ChatId, "duplicate"));
            }
        }

        // Assinatura: hook com secret exige X-Hub-Signature-256 válida;
        // sem secret aceita (legado) com warning de auditoria.
        string? securityWarning = null;
        if (hook.Secret is { Length: > 0 } secret)
        {
            var sig = http.Request.Headers["X-Hub-Signature-256"].ToString();
            if (!WebhookSignature.Verify(secret, raw, sig))
            {
                await RecordDeliveryAsync(db, new DeliveryInfo(hook, deliveryKey, eventType, "rejected", null, "assinatura ausente/inválida", null), ct);
                return Results.Json(
                    new { detail = "assinatura inválida." }, statusCode: 401);
            }
        }
        else
        {
            securityWarning = "unsigned — hook legado sem secret";
        }

        // Filtro de eventos (RF-D1): lista vazia/ausente aceita tudo;
        // evento conhecido fora da lista é registrado e filtrado.
        var filter = ParseFilter(hook.EventsJson);
        if (filter.Count > 0 && eventType is not null
            && !filter.Contains(eventType, StringComparer.OrdinalIgnoreCase))
        {
            await RecordDeliveryAsync(db, new DeliveryInfo(hook, deliveryKey, eventType, "filtered", null, null, securityWarning), ct);
            return Results.Ok(new { status = "filtered", eventType });
        }

        // Prompt: template {{path}} sobre o payload, ou o parse legado.
        string? content;
        string? modelOverride = null;
        if (!string.IsNullOrWhiteSpace(hook.PromptTemplate))
        {
            try
            {
                using var doc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
                content = WebhookPromptTemplate.Render(
                    hook.PromptTemplate, doc.RootElement,
                    source: $"hook:{hook.Id}");
            }
            catch (JsonException)
            {
                return Results.BadRequest(new
                {
                    detail = "corpo não é JSON válido — exigido pelo promptTemplate do hook.",
                });
            }
            catch (WebhookTemplateException tex)
            {
                await RecordDeliveryAsync(db, new DeliveryInfo(hook, deliveryKey, eventType, "rejected", null, tex.Message, securityWarning), ct);
                return Results.UnprocessableEntity(
                    new { detail = $"template: {tex.Message}" });
            }
        }
        else
        {
            (content, modelOverride) = ReadPrompt(raw);
            // Ramo legado (sem template): o conteúdo cru do payload também
            // ganha proveniência explícita — doutrina <untrusted_input> do
            // SPEC-20261023 cap. D vale para QUALQUER conteúdo externo.
            if (!string.IsNullOrWhiteSpace(content))
            {
                content = "<untrusted_input source=\"hook:" + hook.Id
                    + "\" field=\"payload\">"
                    + content.Replace("</untrusted_input>",
                        "<\\/untrusted_input>",
                        StringComparison.OrdinalIgnoreCase)
                    + "</untrusted_input>";
            }
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Results.BadRequest(
                new { detail = "corpo vazio — envie prompt ou payload JSON." });
        }

        var model = modelOverride ?? FirstModel(hook.Chat.ModelsJson);
        if (model is null)
        {
            return Results.UnprocessableEntity(new
            {
                detail = "sem modelo: configure um modelo no chat vinculado "
                         + "ou envie 'model' no corpo do webhook.",
            });
        }

        // Binding de repo (RF-D2): vincula na primeira entrega do hook —
        // worktree exclusivo do chat; falha vira delivery "failed".
        if (hook.RepoSlug is { Length: > 0 } slug
            && await repos.GetChatBindingAsync(hook.ChatId, ct) is null)
        {
            var (_, bindError) = await repos.OpenChatAsync(
                hook.UserId, hook.ChatId, slug, "main",
                $"https://github.com/{slug}.git", null, ct);
            if (bindError is not null)
            {
                await RecordDeliveryAsync(db, new DeliveryInfo(hook, deliveryKey, eventType, "failed", null, $"repo: {bindError}", securityWarning), ct);
                return Results.UnprocessableEntity(
                    new { detail = $"repo: {bindError}" });
            }
        }
        if (hook.AgentMode is { Length: > 0 } mode)
        {
            hook.Chat.Mode = mode;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var leaf = await db.ChatMessages
            .Where(m => m.ChatId == hook.ChatId)
            .OrderByDescending(m => m.Position)
            .Select(m => new { m.Id, m.Position })
            .FirstOrDefaultAsync(ct);
        // Leaf explícito só vale se ainda existe (podem ter sido deletados);
        // senão cai no topo legado por Position.
        string? leafId = hook.Chat.LeafMessageId;
        if (leafId is not null && leaf?.Id != leafId
            && !await db.ChatMessages.AnyAsync(
                m => m.ChatId == hook.ChatId && m.Id == leafId, ct))
        {
            leafId = null;
        }
        leafId ??= leaf?.Id;

        // Persona do hook (RF-D2): Skill ativa vira system message da run.
        string? personaPrompt = null;
        if (hook.PersonaId is { Length: > 0 } personaSlug)
        {
            personaPrompt = await db.Skills.AsNoTracking()
                .Where(s => s.UserId == hook.UserId && s.IsActive
                    && (s.Name == personaSlug || s.Id == personaSlug))
                .Select(s => s.Content)
                .FirstOrDefaultAsync(ct);
        }

        var message = new ChatMessage
        {
            ChatId = hook.ChatId,
            Role = "user",
            Content = content,
            Position = (leaf?.Position ?? -1) + 1,
            Timestamp = now,
            ParentId = leafId,
        };
        db.ChatMessages.Add(message);
        hook.Chat.LeafMessageId = message.Id;

        var run = new ChatRun
        {
            ChatId = hook.ChatId,
            UserId = hook.UserId,
            Model = model,
            RequestJson = JsonSerializer.Serialize(
                new ChatCompletionRequest(
                    model,
                    [
                        .. personaPrompt is null
                            ? Array.Empty<ChatCompletionMessage>()
                            : new[] { new ChatCompletionMessage("system", personaPrompt) },
                        new ChatCompletionMessage("user", content),
                    ],
                    Stream: true),
                JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(run);
        hook.Chat.UpdatedAt = now;
        hook.LastFiredAt = now;
        await db.SaveChangesAsync(ct);
        await RecordDeliveryAsync(db, new DeliveryInfo(hook, deliveryKey, eventType, "accepted", run.Id, null, securityWarning), ct);

        dispatcher.Enqueue(run.Id);
        return Results.Accepted(value: new AutomationHookFireResponse(
            run.Id, hook.ChatId, ChatRunStatus.Queued));
    }

    /// <summary>Persiste a linha de auditoria da entrega (RF-D2).</summary>
    private sealed record DeliveryInfo(
        AutomationHook Hook, string? DeliveryKey, string? EventType,
        string Status, string? RunId, string? Error, string? SecurityWarning);

    private static async Task RecordDeliveryAsync(
        AppDbContext db, DeliveryInfo d, CancellationToken ct)
    {
        db.AutomationHookDeliveries.Add(new AutomationHookDelivery
        {
            HookId = d.Hook.Id,
            DeliveryKey = d.DeliveryKey,
            EventType = d.EventType,
            ReceivedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = d.Status,
            RunId = d.RunId,
            Error = d.Error is { Length: > 500 } ? d.Error[..500] : d.Error,
            SecurityWarning = d.SecurityWarning,
        });
        await db.SaveChangesAsync(ct);
    }

    private static HashSet<string> ParseFilter(string? eventsJson)
    {
        if (string.IsNullOrWhiteSpace(eventsJson))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(
                eventsJson, JsonOptions);
            return list is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? OrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Interpreta o corpo do webhook (legado v1): JSON com
    /// <c>prompt</c>/<c>payload</c>/<c>model</c> opcionais, JSON genérico
    /// (vira bloco de código) ou texto puro.
    /// </summary>
    private static (string? Content, string? Model) ReadPrompt(string raw)
    {
        if (raw.Length > MaxPromptChars * 4)
        {
            raw = raw[..(MaxPromptChars * 4)];
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (Truncate(raw), null);
            }

            var model = root.TryGetProperty("model", out var m)
                        && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            var prompt = root.TryGetProperty("prompt", out var p)
                         && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
            root.TryGetProperty("payload", out var payload);

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                var content = payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? prompt
                    : $"{prompt}\n\n```json\n{Truncate(payload.GetRawText(), MaxPromptChars)}\n```";
                return (Truncate(content), model);
            }

            return (Truncate($"```json\n{Truncate(raw, MaxPromptChars)}\n```"), model);
        }
        catch (JsonException)
        {
            return (Truncate(raw), null);
        }
    }

    private static string? FirstModel(string modelsJson)
    {
        try
        {
            var models = JsonSerializer.Deserialize<List<string>>(modelsJson, JsonOptions);
            return models?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max = MaxPromptChars) =>
        value.Length <= max ? value : value[..max] + "…";

    private static Task<User?> CurrentUserAsync(
        HttpContext http, AppDbContext db, CancellationToken ct) =>
        AuthEndpoints.FindUserAsync(http, db, ct);

    /// <summary>Bundle DI do disparo de hook (S107).</summary>
    internal sealed record HookFireDeps(
        AppDbContext Db, IChatRunDispatcher Dispatcher,
        ConfigService Config, RateLimitService Limits,
        WorkspaceRepoService Repos);
}
