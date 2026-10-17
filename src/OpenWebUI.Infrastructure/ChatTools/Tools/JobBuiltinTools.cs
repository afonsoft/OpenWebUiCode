using System.Text.Json;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:job_list</c> — lista os jobs de background do usuário/chat
/// (SPEC-20261007-chat-agent-tools RF-004).
/// </summary>
public sealed class JobListBuiltinTool(ChatJobService jobs) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "job_list";

    /// <inheritdoc />
    public string Description =>
        "List background jobs — call to check running procs.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "all_chats": {
              "type": "boolean",
              "description": "true = jobs from all of the user's conversations; false = this conversation only.",
              "default": false
            },
            "include_finished": {
              "type": "boolean",
              "description": "true = include finished/killed jobs.",
              "default": true
            }
          }
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;


    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var allChats = args.TryGetProperty("all_chats", out var a)
            && a.ValueKind is JsonValueKind.True;
        var includeFinished = !args.TryGetProperty("include_finished", out var f)
            || f.ValueKind is not JsonValueKind.False;

        var list = await jobs.ListAsync(
            context.UserId, allChats ? null : context.ChatId, includeFinished, ct);
        if (list.Count == 0)
        {
            return new BuiltinToolResult("Nenhum job encontrado.", new { jobs = Array.Empty<object>() });
        }

        var items = list.Select(j => new
        {
            jobId = j.Id,
            j.Command,
            j.Status,
            j.Pid,
            j.ExitCode,
            j.StartedAt,
            j.FinishedAt,
        }).ToList();

        var text = string.Join("\n", list.Select(j =>
            $"{j.Id} [{j.Status}] pid={j.Pid?.ToString() ?? "-"} exit={j.ExitCode?.ToString() ?? "-"} :: {j.Command}"));
        return new BuiltinToolResult(text, new { jobs = items });
    }
}

/// <summary>
/// <c>builtin:job_output</c> — devolve stdout+stderr de um job de
/// background (SPEC-20261007-chat-agent-tools RF-004).
/// </summary>
public sealed class JobOutputBuiltinTool(ChatJobService jobs) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "job_output";

    /// <inheritdoc />
    public string Description =>
        "Read a job's output — call to poll a bg command.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "job_id": { "type": "string", "description": "Job id (returned by shell_exec/job_list)." },
            "tail_chars": { "type": "integer", "description": "Max characters from the end of the log.", "default": 4000 }
          },
          "required": ["job_id"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;


    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var jobId = args.TryGetProperty("job_id", out var j) ? j.GetString() : null;
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return new BuiltinToolResult("Parâmetro 'job_id' é obrigatório.");
        }

        var tailChars = args.TryGetProperty("tail_chars", out var t) && t.TryGetInt32(out var tc)
            ? Math.Clamp(tc, 100, 16_000)
            : 4_000;

        var result = await jobs.GetOutputAsync(context.UserId, jobId, tailChars, ct);
        if (result is null)
        {
            return new BuiltinToolResult($"Job '{jobId}' não encontrado.");
        }

        var (job, output) = result.Value;
        var text = $"[job {job.Id} — {job.Status}"
            + (job.ExitCode is not null ? $", exit {job.ExitCode}" : string.Empty)
            + $"]\n{(output.Length > 0 ? output : "(sem saída ainda)")}";
        return new BuiltinToolResult(text, new
        {
            jobId = job.Id,
            status = job.Status,
            exitCode = job.ExitCode,
            truncated = output.StartsWith('…'),
        });
    }
}

/// <summary>
/// <c>builtin:job_kill</c> — mata um job de background do usuário
/// (SPEC-20261007-chat-agent-tools RF-004). Mutável → gate de aprovação.
/// </summary>
public sealed class JobKillBuiltinTool(ChatJobService jobs) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "job_kill";

    /// <inheritdoc />
    public string Description =>
        "Kill a background job — call to stop a bg process.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "job_id": { "type": "string", "description": "Id of the job to kill." }
          },
          "required": ["job_id"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Exec;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var jobId = args.TryGetProperty("job_id", out var j) ? j.GetString() : null;
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return new BuiltinToolResult("Parâmetro 'job_id' é obrigatório.");
        }

        var killed = await jobs.KillAsync(context.UserId, jobId, ct);
        return killed
            ? new BuiltinToolResult($"Job {jobId} morto.", new { jobId, status = ChatJobStatus.Killed })
            : new BuiltinToolResult($"Job '{jobId}' não encontrado ou já finalizado.");
    }
}
