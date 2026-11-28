using System.Text.Json;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin wait</c> — aguarda N segundos (cap <see cref="MaxSeconds"/>)
/// ou, com <c>job_id</c>, até um job de background terminar
/// (SPEC-20261128-wait-tool, estilo Devin: o agente espera um processo
/// em vez de sair do loop). Read-only — o tempo decorrido não muta nada.
/// Ceder o turno ao usuário sem terminar a run já é coberto por
/// <c>ask_user</c>/<c>plan_exit</c>; wait cobre a metade "processo" do
/// wait/yield do plano.
/// </summary>
public sealed class WaitBuiltinTool(ChatJobService jobs) : IBuiltinChatTool
{
    /// <summary>Teto do aguardo em segundos (evita dormir a run inteira).</summary>
    public const int MaxSeconds = 60;

    /// <summary>Intervalo entre polls do status do job.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public string Name => "wait";

    /// <inheritdoc />
    public string Description =>
        "Wait for a duration (seconds, max 60) or until a background job "
            + "finishes (job_id). Use to let a long command complete instead "
            + "of polling it repeatedly or abandoning the run.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "seconds": {
              "type": "integer",
              "description": "Duration to wait (1-60). With job_id it is the deadline."
            },
            "job_id": {
              "type": "string",
              "description": "Optional background job id to wait on until terminal state."
            }
          }
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var seconds = args.TryGetProperty("seconds", out var s)
            && s.ValueKind == JsonValueKind.Number
            ? Math.Clamp(s.GetInt32(), 1, MaxSeconds)
            : MaxSeconds;
        var jobId = args.TryGetProperty("job_id", out var j)
            ? j.GetString()?.Trim()
            : null;

        if (string.IsNullOrEmpty(jobId))
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            return new BuiltinToolResult($"Aguardado {seconds}s.");
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var job = await jobs.GetAsync(context.UserId, jobId, ct);
            if (job is null)
            {
                return new BuiltinToolResult(
                    $"Job '{jobId}' não encontrado ou não pertence ao usuário.");
            }
            if (job.Status != ChatJobStatus.Running)
            {
                return new BuiltinToolResult(
                    $"Job '{jobId}' terminou: {job.Status.ToString().ToLowerInvariant()}.",
                    new { job_id = jobId, status = job.Status.ToString().ToLowerInvariant() });
            }
            await Task.Delay(PollInterval, ct);
        }
        return new BuiltinToolResult(
            $"Job '{jobId}' ainda em execução após {seconds}s.",
            new { job_id = jobId, status = "running" });
    }
}
