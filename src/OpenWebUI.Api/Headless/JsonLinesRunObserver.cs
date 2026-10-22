using System.Text.Json;
using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Api.Headless;

/// <summary>
/// Observer que imprime cada evento da run como uma linha JSON em stdout
/// (SPEC-20261021-harness-seams RF-C1) — saída do modo <c>--headless</c>.
/// Payload já chega higienizado pelo bus (SecretScrubber).
/// </summary>
public sealed class JsonLinesRunObserver : IAgentRunObserver
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly object Gate = new();

    /// <inheritdoc/>
    public Task OnRunEventAsync(AgentRunEvent e, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(new
        {
            type = "event",
            runId = e.RunId,
            chatId = e.ChatId,
            eventType = e.Type,
            payload = e.PayloadJson is { Length: > 0 } p
                ? (object?)JsonSerializer.Deserialize<JsonElement>(p)
                : null,
        }, JsonOptions);
        lock (Gate)
        {
            Console.Out.WriteLine(line);
        }
        return Task.CompletedTask;
    }
}
