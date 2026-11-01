using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// Tools de agent teams (SPEC-20261021-harness-seams RF-E2):
/// <c>team_task</c> (task list compartilhada), <c>team_message</c> (inbox
/// inter-membros via steer/queue ou mensagem durável) e <c>team_status</c>
/// (roster + tasks + runs vivas). Todas operam apenas dentro do
/// <see cref="Chat.TeamId"/> do chamador — fora de team falham fechado.
/// </summary>
public static class TeamToolHelpers
{
    /// <summary>
    /// Resolve o TeamId do chamador a partir do chat do contexto; null
    /// quando o chat não pertence a um team (fail-closed).
    /// </summary>
    public static async Task<string?> CallerTeamIdAsync(
        AppDbContext db, BuiltinToolContext context, CancellationToken ct) =>
        context.ChatId is null
            ? null
            : await TeamService.ResolveTeamIdAsync(db, context.ChatId, ct);

    /// <summary>Resposta padrão de "fora de team".</summary>
    public static BuiltinToolResult NotInTeam() =>
        new("Este chat não pertence a um agent team — chame delegate_task "
            + "com team:true para criar um.");
}

/// <summary>
/// <c>builtin:team_task</c> — task list durável do team
/// (SPEC-20261021-harness-seams RF-E2): <c>list</c>, <c>create</c>,
/// <c>claim</c>, <c>update</c>, <c>complete</c>. Escopo = TeamId do
/// chamador.
/// </summary>
public sealed class TeamTaskBuiltinTool(IServiceScopeFactory scopeFactory)
    : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "team_task";

    /// <inheritdoc />
    public string Description =>
        "Shared task list of the agent team — op: list | create | claim | update | complete. "
            + "Tasks are durable and visible to every member of the team.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "op": {
              "type": "string",
              "enum": ["list", "create", "claim", "update", "complete"],
              "description": "Operation on the shared task list."
            },
            "task_id": {
              "type": "string",
              "description": "Task id (required for claim/update/complete)."
            },
            "title": {
              "type": "string",
              "description": "Task title (required for create)."
            },
            "status": {
              "type": "string",
              "enum": ["pending", "in_progress", "done"],
              "description": "New status for op=update."
            },
            "result": {
              "type": "string",
              "description": "Outcome summary recorded by op=complete."
            }
          },
          "required": ["op"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Write;

    /// <inheritdoc />
    public bool SafeInParallel => false;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teamId = await TeamToolHelpers.CallerTeamIdAsync(db, context, ct);
        if (teamId is null)
        {
            return TeamToolHelpers.NotInTeam();
        }

        var op = args.TryGetProperty("op", out var opEl)
            ? opEl.GetString()?.ToLowerInvariant() : null;
        var taskId = args.TryGetProperty("task_id", out var tidEl)
            ? tidEl.GetString() : null;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return op switch
        {
            "list" => await ListTasksAsync(db, teamId, ct),
            "create" => await CreateTaskAsync(db, args, context, teamId, now, ct),
            "claim" or "update" or "complete" => await MutateTaskAsync(
                db, args, context, teamId, taskId, op, now, ct),
            _ => new BuiltinToolResult(
                "op deve ser list|create|claim|update|complete."),
        };
    }

    private static async Task<BuiltinToolResult> ListTasksAsync(
        AppDbContext db, string teamId, CancellationToken ct)
    {
        var tasks = await db.TeamTasks.AsNoTracking()
            .Where(t => t.TeamId == teamId)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);
        if (tasks.Count == 0)
        {
            return new BuiltinToolResult("Task list do team vazia.");
        }
        var lines = tasks.Select(t =>
            $"- [{t.Status}] {t.Title} (id {t.Id}"
            + $"{(t.AssigneeChatId is not null ? $", assignee {t.AssigneeChatId}" : "")}"
            + $"{(t.Result is not null ? $" — {t.Result}" : "")})");
        return new BuiltinToolResult(
            string.Join("\n", lines),
            new { tasks });
    }

    private static async Task<BuiltinToolResult> CreateTaskAsync(
        AppDbContext db, JsonElement args, BuiltinToolContext context,
        string teamId, long now, CancellationToken ct)
    {
        var title = args.TryGetProperty("title", out var tEl)
            ? tEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
        {
            return new BuiltinToolResult("'title' é obrigatório (máx. 200).");
        }
        var task = new TeamTask
        {
            TeamId = teamId,
            Title = title.Trim(),
            Status = "pending",
            CreatedByChatId = context.ChatId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.TeamTasks.Add(task);
        await db.SaveChangesAsync(ct);
        return new BuiltinToolResult(
            $"Tarefa criada: {task.Title} (id {task.Id}).",
            new { taskId = task.Id, status = task.Status });
    }

    private static async Task<BuiltinToolResult> MutateTaskAsync(
        AppDbContext db, JsonElement args, BuiltinToolContext context,
        string teamId, string? taskId, string op, long now,
        CancellationToken ct)
    {
        if (taskId is null)
        {
            return new BuiltinToolResult("'task_id' é obrigatório.");
        }
        var task = await db.TeamTasks.FirstOrDefaultAsync(
            t => t.Id == taskId && t.TeamId == teamId, ct);
        if (task is null)
        {
            return new BuiltinToolResult(
                $"Tarefa '{taskId}' não existe neste team.");
        }

        if (op == "claim")
        {
            if (task.AssigneeChatId is not null && task.Status != "pending")
            {
                return new BuiltinToolResult(
                    $"Tarefa já claimada por {task.AssigneeChatId}.");
            }
            task.AssigneeChatId = context.ChatId;
            task.Status = "in_progress";
        }
        else if (op == "complete")
        {
            task.Status = "done";
            task.AssigneeChatId ??= context.ChatId;
            task.Result = args.TryGetProperty("result", out var rEl)
                && rEl.GetString() is { } r
                    ? r.Length > 500 ? r[..500] : r
                    : null;
        }
        else // update
        {
            var status = args.TryGetProperty("status", out var sEl)
                ? sEl.GetString() : null;
            if (status is not ("pending" or "in_progress" or "done"))
            {
                return new BuiltinToolResult(
                    "'status' deve ser pending|in_progress|done.");
            }
            task.Status = status;
        }

        task.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new BuiltinToolResult(
            $"Tarefa {task.Id}: {task.Status}.",
            new { taskId = task.Id, status = task.Status });
    }
}

/// <summary>
/// <c>builtin:team_message</c> — inbox entre membros do team
/// (SPEC-20261021-harness-seams RF-E2): membro vivo (run queued/running/
/// paused) recebe via steer/queue; membro ocioso recebe mensagem durável
/// role=user com prefixo <c>[team:{from}]</c> — vira input no próximo
/// resume do chat alvo.
/// </summary>
public sealed class TeamMessageBuiltinTool(IServiceScopeFactory scopeFactory)
    : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "team_message";

    /// <inheritdoc />
    public string Description =>
        "Send a message to another member of this agent team (to_chat_id). "
            + "Live members get it as steer input in their active run; "
            + "idle members get a durable message consumed on the next resume.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "to_chat_id": {
              "type": "string",
              "description": "Target member chat id (see builtin_team_status)."
            },
            "message": {
              "type": "string",
              "description": "Message content for the teammate."
            }
          },
          "required": ["to_chat_id", "message"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Write;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var toChatId = args.TryGetProperty("to_chat_id", out var toEl)
            ? toEl.GetString() : null;
        var message = args.TryGetProperty("message", out var mEl)
            ? mEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(toChatId) || string.IsNullOrWhiteSpace(message))
        {
            return new BuiltinToolResult("'to_chat_id' e 'message' são obrigatórios.");
        }
        if (message.Length > 4000)
        {
            message = message[..4000];
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teamId = await TeamToolHelpers.CallerTeamIdAsync(db, context, ct);
        if (teamId is null)
        {
            return TeamToolHelpers.NotInTeam();
        }
        if (toChatId == context.ChatId)
        {
            return new BuiltinToolResult("team_message exige outro membro como alvo.");
        }

        // ACL: alvo tem que ser membro do MESMO team — fora disso falha
        // fechado (sem vazamento entre times).
        if (!await IsTeamMemberAsync(db, context, teamId, toChatId, ct))
        {
            return new BuiltinToolResult(
                $"Chat '{toChatId}' não é membro deste team.");
        }

        var activeRun = await db.ChatRuns
            .Where(r => r.ChatId == toChatId
                && (r.Status == ChatRunStatus.Queued
                    || r.Status == ChatRunStatus.Running
                    || r.Status == ChatRunStatus.Paused))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var content = $"[team:{context.ChatId}] {message}";

        if (activeRun is not null)
        {
            // Membro vivo: steer na próxima fronteira de rodada (running)
            // ou queue quando ocioso-queued — mesmo mecanismo do endpoint.
            db.ChatRunSteers.Add(new ChatRunSteer
            {
                RunId = activeRun.Id,
                ChatId = toChatId,
                Content = content,
                Mode = activeRun.Status == ChatRunStatus.Running
                    ? "steer" : "queue",
                Status = "pending",
                Timestamp = now,
            });
            await db.SaveChangesAsync(ct);
            return new BuiltinToolResult(
                $"Mensagem entregue via steer à run ativa {activeRun.Id} "
                    + $"do membro {toChatId}.",
                new { delivered = "steer", runId = activeRun.Id });
        }

        // Membro ocioso: mensagem durável — vira input na próxima
        // continuação do chat alvo.
        return await DeliverDurableAsync(
            db, context, toChatId, message, content, now, ct);
    }

    /// <summary>Alvo existe, é do usuário e pertence ao mesmo team.</summary>
    private static async Task<bool> IsTeamMemberAsync(
        AppDbContext db, BuiltinToolContext context, string teamId,
        string toChatId, CancellationToken ct)
    {
        var target = await db.Chats.AsNoTracking()
            .Where(c => c.Id == toChatId && c.UserId == context.UserId)
            .Select(c => new { c.Id, c.TeamId })
            .FirstOrDefaultAsync(ct);
        var targetTeam = target is null
            ? null
            : target.TeamId ?? await TeamService.ResolveTeamIdAsync(
                db, toChatId, ct);
        return target is not null && targetTeam == teamId;
    }

    /// <summary>Entrega durável: mensagem user no topo do alvo + notificação.</summary>
    private static async Task<BuiltinToolResult> DeliverDurableAsync(
        AppDbContext db, BuiltinToolContext context, string toChatId,
        string message, string content, long now, CancellationToken ct)
    {
        var leaf = await db.ChatMessages
            .Where(m => m.ChatId == toChatId)
            .OrderByDescending(m => m.Position)
            .Select(m => new { m.Id, m.Position })
            .FirstOrDefaultAsync(ct);
        var targetChat = await db.Chats.FirstOrDefaultAsync(
            c => c.Id == toChatId, ct);
        if (targetChat is null)
        {
            return new BuiltinToolResult("Chat alvo não encontrado.");
        }
        var durable = new ChatMessage
        {
            ChatId = toChatId,
            Role = "user",
            Content = content,
            Position = (leaf?.Position ?? -1) + 1,
            Timestamp = now,
            ParentId = targetChat.LeafMessageId ?? leaf?.Id,
        };
        db.ChatMessages.Add(durable);
        targetChat.LeafMessageId = durable.Id;
        targetChat.UpdatedAt = now;
        db.Notifications.Add(new Notification
        {
            UserId = context.UserId,
            Kind = "automation.reminder",
            Title = $"Mensagem de team em {targetChat.Title}",
            Body = $"De {context.ChatId}: {message}",
            Link = $"/c/{toChatId}",
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return new BuiltinToolResult(
            $"Membro ocioso — mensagem durável gravada no chat {toChatId} "
                + "(consumida no próximo resume).",
            new { delivered = "durable", messageId = durable.Id });
    }
}

/// <summary>
/// <c>builtin:team_status</c> — roster + tasks + runs vivas do team
/// (read-only; análogo a <c>ctx.agentTeams.status</c> do deepseek-harness).
/// </summary>
public sealed class TeamStatusBuiltinTool(IServiceScopeFactory scopeFactory)
    : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "team_status";

    /// <inheritdoc />
    public string Description =>
        "Agent team status — roster (member chats, roles, active runs) "
            + "plus the shared task list.";

    /// <inheritdoc />
    public string ParametersJson => """{"type":"object","properties":{}}""";

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public bool SafeInParallel => true;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teamId = await TeamToolHelpers.CallerTeamIdAsync(db, context, ct);
        if (teamId is null)
        {
            return TeamToolHelpers.NotInTeam();
        }

        var status = await TeamService.BuildStatusAsync(db, teamId, ct);
        if (status is null)
        {
            return TeamToolHelpers.NotInTeam();
        }

        var members = string.Join("\n", status.Members.Select(m =>
            $"- {m.ChatId} [{m.Role}] \"{m.Title}\""
            + $"{(m.ActiveRunId is not null ? $" — run {m.ActiveRunId}" : " — ocioso")}"));
        var tasks = status.Tasks.Count == 0
            ? "(sem tarefas)"
            : string.Join("\n", status.Tasks.Select(t =>
                $"  - [{t.Status}] {t.Title} (id {t.Id})"));
        return new BuiltinToolResult(
            $"Team {teamId}\nMembros:\n{members}\nTasks:\n{tasks}",
            new { status.TeamId, status.Members, status.Tasks });
    }
}
