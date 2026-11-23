using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:context_usage</c> — relatório de uso da janela de contexto
/// por seção (SPEC-20261123-context-usage-report — portado do comando
/// "Show context window usage" do Devin CLI): system prompt, tools,
/// histórico e tool-results pendentes do request persistido da run
/// (<see cref="Domain.ChatRun.RequestJson"/>), mais o threshold efetivo
/// de compactação. Acima de 80% o relatório embute um aviso — o modelo
/// se auto-regula (resume/compacta) antes do overflow estourar.
/// </summary>
public sealed class ContextUsageBuiltinTool(AppDbContext db, ConfigService config)
    : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "context_usage";

    /// <inheritdoc />
    public string Description =>
        "Show context window usage — call to inspect how many tokens each "
        + "section (system prompt, tools, history, tool results) consumes "
        + "before expensive operations.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {}
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;

    /// <summary>Threshold default quando nenhuma config resolve (mesmo
    /// do <see cref="ContextCompactionService"/>).</summary>
    private const int DefaultThresholdTokens = 224_000;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var run = context.RunId is not null
            ? await db.ChatRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == context.RunId, ct)
            : context.ChatId is not null
                ? await db.ChatRuns.AsNoTracking()
                    .Where(r => r.ChatId == context.ChatId)
                    .OrderByDescending(r => r.CreatedAt)
                    .FirstOrDefaultAsync(ct)
                : null;
        if (run is null || string.IsNullOrWhiteSpace(run.RequestJson))
        {
            return new BuiltinToolResult(
                "{\"estimating\":true,\"sections\":[]} — sem request de run "
                + "persistido ainda; o relatório fica disponível após o "
                + "primeiro step.");
        }

        var sections = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["system_prompt"] = 0,
            ["tools"] = 0,
            ["history"] = 0,
            ["pending_tool_results"] = 0,
        };
        try
        {
            using var doc = JsonDocument.Parse(run.RequestJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("messages", out var messages)
                && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in messages.EnumerateArray())
                {
                    var chars = (long)((m.TryGetProperty("content", out var c)
                        ? c.GetString()?.Length ?? 0 : 0)
                        + (m.TryGetProperty("toolCallsJson", out var tc)
                            ? tc.GetString()?.Length ?? 0 : 0));
                    var role = m.TryGetProperty("role", out var r)
                        ? r.GetString() : null;
                    sections[role switch
                    {
                        "system" => "system_prompt",
                        "tool" => "pending_tool_results",
                        _ => "history",
                    }] += chars;
                }
            }
            if (root.TryGetProperty("tools", out var tools)
                && tools.ValueKind == JsonValueKind.Array)
            {
                sections["tools"] = tools.GetRawText().Length;
            }
        }
        catch (JsonException)
        {
            return new BuiltinToolResult(
                "{\"estimating\":true,\"sections\":[]} — RequestJson da run "
                + "não parseou; relatório indisponível nesta run.");
        }

        var model = context.Model ?? run.Model;
        var maxTokens = Math.Clamp(
            (int)(await config.GetAsync<long?>(
                $"compaction:{model}:threshold_tokens", null, ct)
                ?? await config.GetAsync<long>(
                    "compaction:threshold_tokens", DefaultThresholdTokens, ct)),
            32_000, 256_000);
        var totalChars = sections.Values.Sum();
        var totalTokens = ContextTokens.Estimate(totalChars);
        var percentUsed = maxTokens > 0
            ? (int)Math.Round(100.0 * totalTokens / maxTokens)
            : 0;

        var payload = new
        {
            model,
            totalTokens,
            maxTokens,
            percentUsed,
            compactionThresholdTokens = maxTokens,
            sections = sections.Select(s => new
            {
                name = s.Key,
                tokens = ContextTokens.Estimate(s.Value),
            }).ToList(),
            warning = percentUsed > 80
                ? "context above 80% — summarize progress, finish tool work "
                  + "or request compaction before opening new large outputs."
                : null,
            estimating = true, // heurística chars/4 — mesma do threshold.
        };
        return new BuiltinToolResult(JsonSerializer.Serialize(payload));
    }
}
