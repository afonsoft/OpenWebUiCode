using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Orquestrador dos lifecycle hooks (SPEC-20261031-lifecycle-hooks
/// RF-003..RF-008): resolve a config do workdir (cache por diretório),
/// filtra por matcher regex sobre <c>tool_name</c>, executa
/// <c>command</c>/<c>prompt</c> em ordem (primeiro <c>block</c> vence) e
/// persiste cada decisão no audit trail da run
/// (<c>hook.decision</c>).
/// </summary>
public sealed class LifecycleHookService(
    CommandHookRunner commands,
    PromptHookEvaluator prompts,
    RunEventWriter? events,
    ILogger<LifecycleHookService>? log = null)
{
    /// <summary>Cache por workdir — reload só quando o arquivo muda.</summary>
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    private sealed record CacheEntry(
        DateTime LoadedAt, List<HookDefinition> Hooks, long Stamp);

    /// <summary>Stamp = soma dos ticks dos arquivos fonte existentes.</summary>
    private static long StampOf(string? workdir, string home)
    {
        long stamp = 0;
        foreach (var p in SourcesOf(workdir, home))
        {
            if (File.Exists(p))
            {
                stamp += new FileInfo(p).LastWriteTimeUtc.Ticks;
            }
        }
        return stamp;
    }

    /// <summary>Caminhos-fonte consultados (mesma ordem do loader).</summary>
    private static IEnumerable<string> SourcesOf(string? workdir, string home)
    {
        if (!string.IsNullOrWhiteSpace(workdir))
        {
            yield return Path.Combine(workdir, ".openwebui", "hooks.json");
            yield return Path.Combine(workdir, ".devin", "hooks.v1.json");
            yield return Path.Combine(workdir, ".devin", "config.json");
            yield return Path.Combine(workdir, ".claude", "settings.json");
            yield return Path.Combine(workdir, ".claude", "settings.local.json");
        }
        yield return Path.Combine(home, ".config", "openwebui", "hooks.json");
    }

    /// <summary>Hooks resolvidos para o workdir (cache com invalidação por mtime).</summary>
    public IReadOnlyList<HookDefinition> Resolve(string? workdir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var key = workdir ?? "(none)";
        var stamp = StampOf(workdir, home);
        if (Cache.TryGetValue(key, out var hit) && hit.Stamp == stamp)
        {
            return hit.Hooks;
        }
        var hooks = HookConfigLoader.Load(workdir, log, home);
        Cache[key] = new CacheEntry(DateTime.UtcNow, hooks, stamp);
        return hooks;
    }

    /// <summary>Matcher do hook bate o tool_name?</summary>
    public static bool Matches(HookDefinition hook, string? toolName) =>
        hook.Matcher is null
        || toolName is null
        || Regex.IsMatch(toolName, hook.Matcher, RegexOptions.IgnoreCase);

    /// <summary>
    /// Executa os hooks de um evento em ordem; primeiro <c>block</c>
    /// vence (demais seguem rodando? não — interrompe: "primeiro block
    /// vence"). <paramref name="toolName"/> filtra o matcher; o payload
    /// recebe <c>session_id/prompt_id/cwd/hook_event_name</c>.
    /// </summary>
    public async Task<HookOutcome> RunAsync(
        string eventName, string? workdir, ChatRun run,
        string? toolName, object? payloadExtra, CancellationToken ct)
    {
        var hooks = Resolve(workdir)
            .Where(h => string.Equals(h.Event, eventName,
                StringComparison.OrdinalIgnoreCase))
            .Where(h => Matches(h, toolName))
            .ToList();
        if (hooks.Count == 0)
        {
            return HookOutcome.ApproveNone;
        }

        var payload = BuildPayload(eventName, workdir, run, toolName, payloadExtra);
        var contexts = new List<string>();
        string? updated = null;
        foreach (var hook in hooks)
        {
            var sw = Stopwatch.StartNew();
            var outcome = hook.Type.Equals("prompt", StringComparison.OrdinalIgnoreCase)
                ? await prompts.EvaluateAsync(hook, payload, run, ct)
                : await commands.RunAsync(hook, payload,
                    workdir ?? Directory.GetCurrentDirectory(),
                    run.Id, run.ChatId, ct);
            sw.Stop();
            contexts.AddRange(outcome.AdditionalContexts);
            updated ??= outcome.UpdatedInput;
            await AuditAsync(run, eventName, hook, outcome, sw.Elapsed, ct);
            if (outcome.Decision is HookDecision.Block or HookDecision.Ask)
            {
                return outcome with { AdditionalContexts = contexts,
                    UpdatedInput = updated };
            }
        }
        return new HookOutcome(HookDecision.Approve, null, null,
            updated, contexts);
    }

    /// <summary>Payload JSON do evento nos campos do protocolo (RF-001).</summary>
    internal static JsonElement BuildPayload(
        string eventName, string? workdir, ChatRun run,
        string? toolName, object? extra)
    {
        var dict = new Dictionary<string, object?>
        {
            ["session_id"] = run.Id,
            ["prompt_id"] = run.ChatId,
            ["cwd"] = workdir,
            ["hook_event_name"] = eventName,
            ["tool_name"] = toolName,
        };
        if (extra is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in je.EnumerateObject())
            {
                dict[p.Name] = p.Value;
            }
        }
        else if (extra is not null)
        {
            foreach (var p in extra.GetType().GetProperties())
            {
                var name = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
                dict[name] = p.GetValue(extra);
            }
        }
        return JsonSerializer.SerializeToElement(dict);
    }

    /// <summary>Auditoria por decisão (RF-008): evento durável na run.</summary>
    private async Task AuditAsync(
        ChatRun run, string eventName, HookDefinition hook,
        HookOutcome outcome, TimeSpan elapsed, CancellationToken ct)
    {
        if (events is null)
        {
            return;
        }
        try
        {
            await events.AppendAsync(run.Id, "hook.decision", new
            {
                @event = eventName,
                hook = hook.Source,
                type = hook.Type,
                decision = outcome.Decision.ToString().ToLowerInvariant(),
                latencyMs = (int)elapsed.TotalMilliseconds,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogWarning(ex, "Hooks: auditoria falhou (run {Run}).", run.Id);
        }
    }
}
