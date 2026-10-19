using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Escritor dos eventos duráveis de run (SPEC-20261016-run-recovery RF-001):
/// append-only com <c>Seq</c> monotônico por run. Falha de gravação é logada
/// e nunca derruba a run — a reconstrução tolera buracos (evento ausente =
/// fato não recuperado).
/// </summary>
public sealed class RunEventWriter(AppDbContext db, ILogger<RunEventWriter> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, long> _seq = new();

    /// <summary>Tipos emitidos pelo loop/executor.</summary>
    public static class Types
    {
        /// <summary>Loop-state por fronteira de round (RF-002).</summary>
        public const string Snapshot = "snapshot";
        /// <summary>Início de um step/round (1-based).</summary>
        public const string StepStarted = "step_started";
        /// <summary>Completion do round recebida; <c>calls</c> = ids das tool calls.</summary>
        public const string StepFinished = "step_finished";
        /// <summary>Mensagens apendadas ao histórico dentro do round.</summary>
        public const string MessagesAdded = "messages_added";
        /// <summary>Tool call despachada (antes da execução).</summary>
        public const string ToolCall = "tool_call";
        /// <summary>Resultado de uma tool call.</summary>
        public const string ToolResult = "tool_result";
        /// <summary>Texto agregado do assistant (flush ~2KB).</summary>
        public const string ContentDelta = "content_delta";
        /// <summary>Mudança de fase visível (SSE).</summary>
        public const string Phase = "phase";
        /// <summary>Nota de sistema (resume, advisor, etc.).</summary>
        public const string Note = "note";
    }

    /// <summary>Append de um evento com payload já serializado.</summary>
    public async Task AppendAsync(
        string runId, string type, string payloadJson, CancellationToken ct)
    {
        try
        {
            if (!_seq.TryGetValue(runId, out var seq))
            {
                seq = await db.ChatRunEvents.AsNoTracking()
                    .Where(e => e.RunId == runId)
                    .Select(e => (long?)e.Seq)
                    .MaxAsync(ct) ?? 0;
            }
            seq++;
            _seq[runId] = seq;

            db.ChatRunEvents.Add(new ChatRunEvent
            {
                RunId = runId,
                Seq = seq,
                Type = type,
                PayloadJson = SecretScrubber.Scrub(payloadJson) ?? "",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "RunEvent {Type} da run {RunId} não persistiu.", type, runId);
        }
    }

    /// <summary>Append com payload de objeto anônimo.</summary>
    public Task AppendAsync(
        string runId, string type, object payload, CancellationToken ct) =>
        AppendAsync(runId, type, JsonSerializer.Serialize(payload, JsonOptions), ct);
}
