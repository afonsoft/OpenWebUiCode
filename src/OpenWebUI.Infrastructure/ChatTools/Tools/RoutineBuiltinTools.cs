using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:routine</c> — cria/gerencia automações agendadas do usuário
/// (SPEC-20261010-agent-routines): reusa a entidade <see cref="Automation"/>
/// e o <see cref="AutomationScheduler"/> já existentes — o agente só precisa
/// de CRUD; a execução (prompt → modelo → chat novo) já roda server-side.
/// Sem aprovação: só mexe em recursos do próprio usuário.
/// </summary>
public sealed class RoutineBuiltinTool(AppDbContext db, AutomationService automations)
    : IBuiltinChatTool
{
    /// <summary>Cap de caracteres do prompt agendado.</summary>
    private const int MaxPromptChars = 4000;

    /// <inheritdoc />
    public string Name => "routine";

    /// <inheritdoc />
    public string Description =>
        "Manage scheduled prompts (routines): list/create/update/delete/"
            + "enable/disable/run_now. A routine runs the prompt against a "
            + "model on a schedule and records the reply as a new chat.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "create", "update", "delete", "enable", "disable", "run_now"],
              "description": "list shows all routines; update/delete/enable/disable/run_now need 'id'."
            },
            "id": { "type": "string", "description": "Routine id (from list/create output)." },
            "name": { "type": "string", "description": "Display name (required on create)." },
            "prompt": {
              "type": "string",
              "description": "Prompt sent to the model on each execution (required on create)."
            },
            "model": {
              "type": "string",
              "description": "Model id; defaults to the current run's model when omitted."
            },
            "schedule_kind": {
              "type": "string",
              "enum": ["once", "interval", "daily", "weekly"],
              "description": "once fires a single time; interval every N minutes; daily/weekly at time_of_day (UTC)."
            },
            "in_minutes": {
              "type": "integer",
              "description": "once: fire N minutes from now (alternative to run_at)."
            },
            "run_at": {
              "type": "integer",
              "description": "once: fire at this epoch-seconds timestamp (UTC)."
            },
            "interval_minutes": {
              "type": "integer",
              "description": "interval: minutes between runs (min 1, default 60)."
            },
            "time_of_day": {
              "type": "string",
              "description": "daily/weekly: 'HH:mm' UTC."
            },
            "weekday": {
              "type": "integer",
              "description": "weekly: 0-6 (Sunday..Saturday, UTC)."
            }
          },
          "required": ["action"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var action = args.TryGetProperty("action", out var aEl)
            && aEl.ValueKind == JsonValueKind.String
                ? aEl.GetString()
                : null;
        return action switch
        {
            "list" => await ListAsync(context, ct),
            "create" => await CreateAsync(args, context, ct),
            "update" => await UpdateAsync(args, context, ct),
            "delete" => await DeleteAsync(args, context, ct),
            "enable" => await SetEnabledAsync(args, context, true, ct),
            "disable" => await SetEnabledAsync(args, context, false, ct),
            "run_now" => await RunNowAsync(args, context, ct),
            _ => new BuiltinToolResult(
                "Ação inválida — use list|create|update|delete|enable|disable|run_now."),
        };
    }

    private async Task<BuiltinToolResult> ListAsync(BuiltinToolContext context, CancellationToken ct)
    {
        var items = await db.Automations.AsNoTracking()
            .Where(x => x.UserId == context.UserId)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(50)
            .ToListAsync(ct);
        if (items.Count == 0)
        {
            return new BuiltinToolResult("Nenhuma rotina cadastrada.");
        }

        var lines = items.Select(x =>
            $"- `{x.Id}` **{x.Name}** [{x.ScheduleKind}"
            + $"{(x.ScheduleKind == "interval" ? $" {x.IntervalMinutes}min" : string.Empty)}"
            + $"{(x.ScheduleKind is "daily" or "weekly" ? $" {x.TimeOfDay}" : string.Empty)}"
            + $"{(x.ScheduleKind == "weekly" ? $" dow={x.Weekday}" : string.Empty)}"
            + $"] model={x.ModelId}"
            + $"{(x.Enabled ? "" : " (desabilitada)")}"
            + $"{(x.NextRunAt is long next ? $" — próx. {DateTimeOffset.FromUnixTimeSeconds(next):u}" : string.Empty)}");
        return new BuiltinToolResult(
            string.Join('\n', lines),
            new
            {
                routines = items.Select(x => new
                {
                    x.Id, x.Name, x.ScheduleKind, x.IntervalMinutes,
                    x.TimeOfDay, x.Weekday, x.ModelId, x.Enabled, x.NextRunAt,
                }),
            });
    }

    private async Task<BuiltinToolResult> CreateAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var name = ReadString(args, "name");
        var prompt = ReadString(args, "prompt");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(prompt))
        {
            return new BuiltinToolResult("create exige 'name' e 'prompt'.");
        }
        if (prompt.Length > MaxPromptChars)
        {
            return new BuiltinToolResult($"Prompt excede {MaxPromptChars} caracteres.");
        }

        var model = ReadString(args, "model") ?? await CurrentRunModelAsync(context, ct);
        if (string.IsNullOrWhiteSpace(model))
        {
            return new BuiltinToolResult("create exige 'model' (não foi possível herdar da run).");
        }

        var automation = new Automation
        {
            UserId = context.UserId,
            Name = name.Trim(),
            Prompt = prompt,
            ModelId = model,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        var error = ApplySchedule(automation, args);
        if (error is not null)
        {
            return new BuiltinToolResult(error);
        }

        automations.Reschedule(automation);
        db.Automations.Add(automation);
        await db.SaveChangesAsync(ct);
        return new BuiltinToolResult(
            $"Rotina `{automation.Id}` criada: **{automation.Name}** "
                + $"[{automation.ScheduleKind}] model={automation.ModelId}"
                + $"{(automation.NextRunAt is long next ? $" — próx. {DateTimeOffset.FromUnixTimeSeconds(next):u}" : " — sem próxima execução")}.",
            new { automation.Id, automation.NextRunAt });
    }

    private async Task<BuiltinToolResult> UpdateAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var (automation, notFound) = await FindOwnedAsync(args, context, ct);
        if (automation is null)
        {
            return notFound!;
        }

        if (ReadString(args, "name") is { } name && !string.IsNullOrWhiteSpace(name))
        {
            automation.Name = name.Trim();
        }
        if (ReadString(args, "prompt") is { } prompt)
        {
            if (prompt.Length > MaxPromptChars)
            {
                return new BuiltinToolResult($"Prompt excede {MaxPromptChars} caracteres.");
            }
            automation.Prompt = prompt;
        }
        if (ReadString(args, "model") is { } model && !string.IsNullOrWhiteSpace(model))
        {
            automation.ModelId = model;
        }
        if (args.TryGetProperty("schedule_kind", out _) || args.TryGetProperty("in_minutes", out _)
            || args.TryGetProperty("run_at", out _) || args.TryGetProperty("interval_minutes", out _)
            || args.TryGetProperty("time_of_day", out _) || args.TryGetProperty("weekday", out _))
        {
            var error = ApplySchedule(automation, args);
            if (error is not null)
            {
                return new BuiltinToolResult(error);
            }
        }

        automation.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        automations.Reschedule(automation);
        await db.SaveChangesAsync(ct);
        return new BuiltinToolResult($"Rotina `{automation.Id}` atualizada.");
    }

    private async Task<BuiltinToolResult> DeleteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var (automation, notFound) = await FindOwnedAsync(args, context, ct);
        if (automation is null)
        {
            return notFound!;
        }

        db.Automations.Remove(automation);
        await db.SaveChangesAsync(ct);
        return new BuiltinToolResult($"Rotina `{automation.Id}` removida.");
    }

    private async Task<BuiltinToolResult> SetEnabledAsync(
        JsonElement args, BuiltinToolContext context, bool enabled, CancellationToken ct)
    {
        var (automation, notFound) = await FindOwnedAsync(args, context, ct);
        if (automation is null)
        {
            return notFound!;
        }

        automation.Enabled = enabled;
        automation.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        automations.Reschedule(automation);
        await db.SaveChangesAsync(ct);
        return new BuiltinToolResult(
            $"Rotina `{automation.Id}` {(enabled ? "habilitada" : "desabilitada")}.");
    }

    private async Task<BuiltinToolResult> RunNowAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var (automation, notFound) = await FindOwnedAsync(args, context, ct);
        if (automation is null)
        {
            return notFound!;
        }

        var run = await automations.RunNowAsync(automation, ct);
        return run.Status == "ok"
            ? new BuiltinToolResult(
                $"Rotina `{automation.Id}` executada — resposta no chat `{run.ChatId}`.",
                new { run.ChatId })
            : new BuiltinToolResult($"Execução falhou: {run.Error}");
    }

    private async Task<(Automation? Automation, BuiltinToolResult? NotFound)> FindOwnedAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var id = ReadString(args, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return (null, new BuiltinToolResult("Parâmetro 'id' é obrigatório."));
        }

        var automation = await db.Automations
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == context.UserId, ct);
        return automation is null
            ? (null, new BuiltinToolResult($"Rotina `{id}` não encontrada."))
            : (automation, null);
    }

    /// <summary>Aplica os campos de agenda dos args na automação; null quando válido.</summary>
    private static string? ApplySchedule(Automation automation, JsonElement args)
    {
        var kind = ReadString(args, "schedule_kind") ?? automation.ScheduleKind;
        automation.ScheduleKind = kind;
        return kind switch
        {
            "once" => ApplyOnce(automation, args),
            "interval" => ApplyInterval(automation, args),
            "daily" => ApplyDailyWeekly(automation, args, weekly: false),
            "weekly" => ApplyDailyWeekly(automation, args, weekly: true),
            _ => "'schedule_kind' deve ser once|interval|daily|weekly.",
        };
    }

    private static string? ApplyOnce(Automation automation, JsonElement args)
    {
        var now = DateTimeOffset.UtcNow;
        if (ReadLong(args, "run_at") is long runAt && runAt > 0)
        {
            automation.NextRunAt = runAt;
        }
        else if (ReadLong(args, "in_minutes") is long inMin && inMin > 0)
        {
            automation.NextRunAt = now.AddMinutes(inMin).ToUnixTimeSeconds();
        }
        else if (automation.NextRunAt is null || automation.NextRunAt <= now.ToUnixTimeSeconds())
        {
            return "schedule_kind=once exige 'run_at' futuro ou 'in_minutes' >= 1.";
        }
        return null;
    }

    private static string? ApplyInterval(Automation automation, JsonElement args)
    {
        if (ReadLong(args, "interval_minutes") is long interval && interval > 0)
        {
            automation.IntervalMinutes = (int)interval;
        }
        automation.IntervalMinutes = Math.Max(1, automation.IntervalMinutes);
        return null;
    }

    private static string? ApplyDailyWeekly(
        Automation automation, JsonElement args, bool weekly)
    {
        if (ReadString(args, "time_of_day") is { } tod)
        {
            if (!TimeOnly.TryParseExact(tod, "HH:mm", out _))
            {
                return "'time_of_day' deve estar no formato HH:mm (UTC).";
            }
            automation.TimeOfDay = tod;
        }
        if (string.IsNullOrEmpty(automation.TimeOfDay))
        {
            return $"schedule_kind={automation.ScheduleKind} exige 'time_of_day' HH:mm (UTC).";
        }
        return weekly ? ApplyWeekday(automation, args) : null;
    }

    private static string? ApplyWeekday(Automation automation, JsonElement args)
    {
        if (ReadLong(args, "weekday") is { } dow)
        {
            if (dow is < 0 or > 6)
            {
                return "'weekday' deve ser 0-6.";
            }
            automation.Weekday = (int)dow;
        }
        return automation.Weekday is null
            ? "schedule_kind=weekly exige 'weekday' 0-6."
            : null;
    }

    /// <summary>Modelo da run corrente — default de 'model' no create.</summary>
    private async Task<string?> CurrentRunModelAsync(BuiltinToolContext context, CancellationToken ct)
    {
        if (context.RunId is null)
        {
            return null;
        }
        var run = await db.ChatRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == context.RunId, ct);
        return run?.Model;
    }

    private static string? ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static long? ReadLong(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el))
        {
            return null;
        }
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(el.GetString(), out var n) => n,
            _ => null,
        };
    }
}

/// <summary>
/// <c>builtin:reminder</c> — atalho para lembrete único
/// (SPEC-20261010-agent-routines): cria uma <see cref="Automation"/> com
/// <c>ScheduleKind=once</c>; o scheduler dispara e o resultado chega ao feed
/// de notificações (<c>automation.reminder</c>) como chat novo.
/// </summary>
public sealed class ReminderBuiltinTool(AppDbContext db) : IBuiltinChatTool
{
    /// <summary>Cap de caracteres do título derivado da mensagem.</summary>
    private const int MaxTitleChars = 80;

    /// <inheritdoc />
    public string Name => "reminder";

    /// <inheritdoc />
    public string Description =>
        "Set a one-shot reminder: fires once after N minutes (or at an epoch "
            + "timestamp) and delivers a notification + a new chat with the message.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "message": {
              "type": "string",
              "description": "Reminder text to deliver."
            },
            "in_minutes": {
              "type": "integer",
              "description": "Fire N minutes from now (default 60)."
            },
            "run_at": {
              "type": "integer",
              "description": "Fire at this epoch-seconds timestamp (UTC) — overrides in_minutes."
            },
            "model": {
              "type": "string",
              "description": "Model id for the reminder chat; defaults to the current run's model."
            }
          },
          "required": ["message"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var message = args.TryGetProperty("message", out var mEl)
            && mEl.ValueKind == JsonValueKind.String
                ? mEl.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(message))
        {
            return new BuiltinToolResult("Parâmetro 'message' é obrigatório.");
        }

        var now = DateTimeOffset.UtcNow;
        long nextRun;
        if (args.TryGetProperty("run_at", out var rEl)
            && rEl.ValueKind == JsonValueKind.Number && rEl.TryGetInt64(out var runAt)
            && runAt > now.ToUnixTimeSeconds())
        {
            nextRun = runAt;
        }
        else
        {
            var inMinutes = args.TryGetProperty("in_minutes", out var iEl)
                && iEl.ValueKind == JsonValueKind.Number && iEl.TryGetInt64(out var n)
                    ? Math.Max(1, n)
                    : 60;
            nextRun = now.AddMinutes(inMinutes).ToUnixTimeSeconds();
        }

        var model = args.TryGetProperty("model", out var moEl)
            && moEl.ValueKind == JsonValueKind.String
                ? moEl.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(model) && context.RunId is not null)
        {
            var run = await db.ChatRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == context.RunId, ct);
            model = run?.Model;
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            return new BuiltinToolResult(
                "Não foi possível determinar o modelo do lembrete — informe 'model'.");
        }

        var title = message.Trim();
        if (title.Length > MaxTitleChars)
        {
            title = title[..MaxTitleChars] + "…";
        }

        var automation = new Automation
        {
            UserId = context.UserId,
            Name = $"Lembrete: {title}",
            Prompt = $"Lembrete agendado pelo usuário — apresente de forma clara e objetiva:\n\n{message.Trim()}",
            ModelId = model,
            ScheduleKind = "once",
            NextRunAt = nextRun,
            CreatedAt = now.ToUnixTimeSeconds(),
            UpdatedAt = now.ToUnixTimeSeconds(),
        };
        db.Automations.Add(automation);
        await db.SaveChangesAsync(ct);
        return new BuiltinToolResult(
            $"Lembrete `{automation.Id}` agendado para {DateTimeOffset.FromUnixTimeSeconds(nextRun):u}.",
            new { automation.Id, automation.NextRunAt });
    }
}
