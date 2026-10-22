using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Infrastructure.Runs;

/// <summary>
/// Observer inicial de telemetria
/// (SPEC-20261021-harness-seams RF-A3): contadores por run (steps,
/// tool calls, resultados) e latência tool_call→tool_result em log
/// estruturado. Estado em memória por instância do escopo da run —
/// best-effort, nunca lança.
/// </summary>
public sealed class TelemetryObserver(ILogger<TelemetryObserver> logger)
    : IAgentRunObserver
{
    private int _steps;
    private int _toolCalls;
    private readonly Dictionary<string, long> _toolStartedAt = new();

    /// <inheritdoc />
    public Task OnRunEventAsync(AgentRunEvent e, CancellationToken ct)
    {
        try
        {
            switch (e.Type)
            {
                case "run.started":
                    logger.LogInformation(
                        "run.telemetry started run={RunId} chat={ChatId} user={UserId}",
                        e.RunId, e.ChatId, e.UserId);
                    break;
                case "step_started":
                    _steps++;
                    break;
                case "tool_call":
                    _toolCalls++;
                    if (TryGet(e.PayloadJson, "id", out var callId))
                    {
                        _toolStartedAt[callId!] = DateTimeOffset.UtcNow
                            .ToUnixTimeMilliseconds();
                    }
                    break;
                case "tool_result":
                    if (TryGet(e.PayloadJson, "callId", out var doneId)
                        && _toolStartedAt.Remove(doneId!, out var startedAt))
                    {
                        logger.LogInformation(
                            "run.telemetry tool_latency run={RunId} call={CallId} ms={Ms}",
                            e.RunId, doneId,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt);
                    }
                    break;
                case "run.completed" or "run.failed" or "run.stopped":
                    logger.LogInformation(
                        "run.telemetry finished run={RunId} type={Type} steps={Steps} tools={Tools}",
                        e.RunId, e.Type, _steps, _toolCalls);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "TelemetryObserver ignorou evento {Type}.", e.Type);
        }
        return Task.CompletedTask;
    }

    private static bool TryGet(string payloadJson, string property, out string? value)
    {
        value = null;
        if (string.IsNullOrEmpty(payloadJson))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty(property, out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                value = el.GetString();
            }
        }
        catch (JsonException)
        {
            // payload não-JSON — ignorado.
        }
        return value is not null;
    }
}
