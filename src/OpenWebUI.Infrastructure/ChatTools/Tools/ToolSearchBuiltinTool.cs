using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin_tool_search</c> (SPEC-20261031-tool-search-deferred RF-003):
/// busca substring nas tools deferidas da run, promove as matches a
/// "loaded" (voltam ao spec anunciado no round seguinte) e devolve os
/// schemas completos. Sempre core — nunca pode ser deferida. Não expõe
/// tools fora do conjunto permitido da run.
/// </summary>
public sealed class ToolSearchBuiltinTool(
    DeferredToolState state,
    ToolIndexService index,
    RunEventWriter events) : IBuiltinChatTool
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public string Name => "tool_search";

    public string Description =>
        "Search deferred tools by name/description substring and load their "
        + "full schemas into this run. Use before calling a deferred tool.";

    public string ParametersJson => """
        {
            "type": "object",
            "properties": {
                "query": {"type": "string", "description": "Substring to match against deferred tool names/descriptions"},
                "limit": {"type": "integer", "default": 5, "description": "Max schemas to load (1-20)"}
            },
            "required": ["query"]
        }
        """;

    public ApprovalTier Tier => ApprovalTier.Read;

    public bool SafeInParallel => true;

    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var query = args.TryGetProperty("query", out var qEl)
            ? qEl.GetString() ?? string.Empty
            : string.Empty;
        var limit = args.TryGetProperty("limit", out var lEl)
            && lEl.TryGetInt32(out var n)
                ? n
                : 5;

        if (!state.IsActive(context.RunId))
        {
            return new BuiltinToolResult(
                "No deferred tools in this run — every permitted tool is "
                + "already announced.");
        }

        var hits = index.Search(state.Deferred(context.RunId), query, limit);
        var loaded = new List<object>();
        foreach (var tool in hits)
        {
            var name = ToolExecutor.FunctionName(tool) ?? tool.Name;
            if (state.Load(context.RunId, name) is { } loadedTool)
            {
                loaded.Add(new
                {
                    name,
                    schema = JsonSerializer.Deserialize<JsonElement>(
                        loadedTool.SpecJson),
                });
            }
        }

        if (loaded.Count == 0)
        {
            return new BuiltinToolResult(
                $"No deferred tools match '{query}'. Broaden the query or "
                + "check the '## Available on demand' index in the system "
                + "prompt.");
        }

        if (context.RunId is { } runId)
        {
            await events.AppendAsync(runId, "tool_search.loaded",
                new
                {
                    loaded = hits
                        .Select(t => ToolExecutor.FunctionName(t) ?? t.Name)
                        .ToArray(),
                    remainingDeferred = state.Deferred(runId).Count,
                }, ct);
        }

        return new BuiltinToolResult(JsonSerializer.Serialize(
            new { loaded }, JsonOptions));
    }
}
