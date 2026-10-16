using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Runs;

/// <summary>
/// Dispatch de mensagens de canal para personas
/// (SPEC-20261016-agent-personas-channels RF-002/RF-003/RF-005): resolve a
/// sessão lógica (persona × chatKey → chat), injeta o system prompt da
/// persona (+ linha de confirmação no nível <c>strict</c>), filtra os
/// ToolIds pelo guardrail e enfileira uma run real — o audit do turno é
/// fechado por <see cref="PersonaTurnNotifier"/> ao término da run.
/// </summary>
public sealed class PersonaDispatchService(
    AppDbContext db,
    ChatRunDispatcher dispatcher,
    ContextBoundaryService boundaries,
    BuiltinToolRegistry builtins)
{
    /// <summary>Tempo máximo de espera síncrona por turno (RF-002: 2min).</summary>
    public static readonly TimeSpan SyncWait = TimeSpan.FromSeconds(110);

    /// <summary>Limite de mensagens por persona por minuto (429 acima).</summary>
    private const int RateLimitPerMinute = 60;

    private const int AuditFieldCap = 4096;

    /// <summary>Janela deslizante de rate limit por persona (processo).</summary>
    private static readonly ConcurrentDictionary<string, Queue<long>> RateWindow = new();

    /// <summary>Tools de execução bloqueadas no guardrail <c>basic</c>.</summary>
    private static readonly HashSet<string> ExecuteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell_exec", "code_interpreter", "job_kill",
    };

    /// <summary>Tools de leitura permitidas no guardrail <c>strict</c>.</summary>
    private static readonly HashSet<string> ReadTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_list", "file_read", "file_grep", "file_glob",
        "web_search", "fetch_url", "ask_user", "todo_write",
        "job_list", "job_output", "memory_search", "run_result",
    };

    /// <summary>Linha extra de system prompt no nível strict (RF-005).</summary>
    internal const string StrictPromptLine =
        "Responda sem executar ações destrutivas; peça confirmação antes de qualquer alteração.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Resultado do dispatch para o endpoint montar 200/202/429.</summary>
    public sealed record DispatchOutcome(
        bool RateLimited, string? SessionId, string? RunId,
        string? Reply, string Status);

    /// <summary>
    /// Envia uma mensagem de canal para a persona. Com <paramref name="wait"/>
    /// retorna quando a run termina (ou após <see cref="SyncWait"/>); sem
    /// wait retorna imediatamente com a run enfileirada.
    /// </summary>
    public async Task<DispatchOutcome> SendAsync(
        AgentPersona persona, string message, string? chatKey, bool wait,
        CancellationToken ct)
    {
        if (!AllowRequest(persona.Id))
        {
            return new DispatchOutcome(true, null, null, null, "rate_limited");
        }

        var key = string.IsNullOrWhiteSpace(chatKey) ? "default" : chatKey.Trim();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var session = await db.AgentPersonaSessions
            .FirstOrDefaultAsync(
                s => s.PersonaId == persona.Id && s.ChatKey == key, ct);
        var chat = session is null
            ? null
            : await db.Chats.Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == session.ChatId, ct);

        if (chat is null)
        {
            chat = new Chat
            {
                UserId = persona.UserId,
                Title = $"agente:{persona.Name}",
                ModelsJson = JsonSerializer.Serialize(new[] { persona.ModelId }, JsonOptions),
                ApprovalPreset = "approve-mutations",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Chats.Add(chat);
            if (session is null)
            {
                session = new AgentPersonaSession
                {
                    PersonaId = persona.Id,
                    ChatKey = key,
                    ChatId = chat.Id,
                    UpdatedAt = now,
                };
                db.AgentPersonaSessions.Add(session);
            }
            else
            {
                // Sessão órfã (chat removido): reponta para o chat novo
                // em vez de duplicar a chave (personaId, chatKey).
                session.ChatId = chat.Id;
            }
        }
        // session != null aqui: chat só foi encontrado via sessão, e o
        // ramo de criação atribui uma nova. NRE impossível por construção.
        session!.UpdatedAt = now;

        var position = chat.Messages.Count == 0 ? 0 : chat.Messages.Max(m => m.Position) + 1;
        var userMessage = new ChatMessage
        {
            ChatId = chat.Id,
            Role = "user",
            Content = message,
            Position = position,
            Timestamp = now,
            ParentId = BranchService.ResolveLeaf(chat)?.Id,
        };
        chat.Messages.Add(userMessage);
        chat.LeafMessageId = userMessage.Id;
        chat.UpdatedAt = now;

        var path = BranchService.WalkPath(chat);
        var visible = await boundaries.FilterAsync(chat.Id, path, ct);
        var history = visible
            .Select(m => new ChatCompletionMessage(
                m.Role, m.Content, m.ToolCallId, m.ToolCallsJson))
            .ToList();

        var system = persona.GuardrailLevel == "strict"
            ? $"{persona.SystemPrompt}\n\n{StrictPromptLine}"
            : persona.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(system))
        {
            history.Insert(0, new ChatCompletionMessage("system", system));
        }

        var toolIds = ResolveToolIds(persona);
        var completionRequest = new ChatCompletionRequest(
            persona.ModelId, history, Stream: true, ToolIds: toolIds);

        var run = new ChatRun
        {
            ChatId = chat.Id,
            UserId = persona.UserId,
            Model = persona.ModelId,
            RequestJson = JsonSerializer.Serialize(completionRequest, JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(run);

        var turn = new AgentTurn
        {
            PersonaId = persona.Id,
            RunId = run.Id,
            ChatKey = key,
            Channel = "webhook",
            InText = Truncate(message),
            Timestamp = now,
        };
        db.AgentTurns.Add(turn);
        await db.SaveChangesAsync(ct);

        dispatcher.Enqueue(run.Id);
        if (!wait)
        {
            return new DispatchOutcome(false, chat.Id, run.Id, null, "running");
        }

        var reply = await WaitTurnAsync(turn.Id, ct);
        return reply is null
            ? new DispatchOutcome(false, chat.Id, run.Id, null, "running")
            : new DispatchOutcome(false, chat.Id, run.Id, reply.OutText, reply.Status);
    }

    /// <summary>
    /// ToolIds efetivos da persona (RF-005): <c>none</c> → allowlist da
    /// persona ou todas as builtins; <c>basic</c> → menos tools de
    /// execução; <c>strict</c> → somente leitura. Tools da persona
    /// (allowlist extra) sempre intersectam.
    /// </summary>
    internal IReadOnlyList<string>? ResolveToolIds(AgentPersona persona)
    {
        var personaTools = ParseTools(persona.ToolsJson);
        if (persona.GuardrailLevel == "none" && personaTools is null)
        {
            return null; // conjunto default do chat — sem restrição
        }

        var names = builtins.All.Select(t => t.Name);
        var level = persona.GuardrailLevel;
        var allowed = level == "strict"
            ? names.Where(ReadTools.Contains)
            : level == "basic"
                ? names.Where(n => !ExecuteTools.Contains(n))
                : names;
        if (personaTools is not null)
        {
            var set = new HashSet<string>(personaTools, StringComparer.OrdinalIgnoreCase);
            allowed = allowed.Where(set.Contains);
        }

        var ids = allowed
            .Select(n => $"{BuiltinToolRegistry.IdPrefix}{n}")
            .ToList();
        return ids.Count == 0 ? null : ids;
    }

    /// <summary>Espera o turno terminar (notifier preenche) até SyncWait.</summary>
    private async Task<AgentTurn?> WaitTurnAsync(string turnId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + SyncWait;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var turn = await db.AgentTurns.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == turnId, ct);
            if (turn is null || turn.Status != "running")
            {
                return turn;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        }
        return null;
    }

    /// <summary>Rate limit 60/min por persona — janela deslizante.</summary>
    private static bool AllowRequest(string personaId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var window = RateWindow.GetOrAdd(personaId, _ => new Queue<long>());
        lock (window)
        {
            while (window.Count > 0 && now - window.Peek() >= 60)
            {
                window.Dequeue();
            }
            if (window.Count >= RateLimitPerMinute)
            {
                return false;
            }
            window.Enqueue(now);
            return true;
        }
    }

    internal static string Truncate(string text) =>
        text.Length <= AuditFieldCap ? text : text[..AuditFieldCap];

    private static List<string>? ParseTools(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
