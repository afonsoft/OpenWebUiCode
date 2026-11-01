using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Juiz LLM do preset <c>smart</c> (SPEC-20261031-smart-permission-llm):
/// classifica a tool call pendente como <c>allow|deny|ask</c> — só é
/// chamado para o que a regra determinística marcou Medium/High e que
/// não está em <see cref="IsAlwaysAsk"/>. Prompt com seções nonce
/// (anti-injeção), dedup por fingerprint na sessão, fail-safe
/// <c>ask</c> e auditoria por chamada.
/// </summary>
public sealed class SmartPermissionClassifier(
    ModelRoleResolver roles,
    ConfigService config,
    Func<ChatCompletionRequest, string?, CancellationToken, Task<ProviderCompletion>> complete,
    ILogger<SmartPermissionClassifier>? log = null)
{
    /// <summary>Decisões memorizadas por sessão (fingerprint → decisão).</summary>
    private static readonly ConcurrentDictionary<string,
        ConcurrentDictionary<string, (string Decision, string Reason)>> Permitted = new();

    private static readonly Lazy<string> s_systemPrompt = new(LoadPrompt);

    /// <summary>Decisão do juiz.</summary>
    public sealed record Verdict(
        string Decision, string Reason, string Fingerprint,
        string? Model, long LatencyMs, bool FromCache);

    /// <summary>
    /// Categorias always-ask (RF-004): padrões que nunca passam pelo
    /// juiz — nem dedup libera.
    /// </summary>
    public static readonly Regex[] AlwaysAskPatterns =
    [
        new(@"\b(npm|yarn|pnpm|pip|pip3|apt|apt-get|brew|choco)\s+(add|install)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bgit\s+(commit|push|reset|rebase|checkout\s+--|clean\s+-f|restore)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(rm|rmdir|del)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(sudo|doas)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bterraform\s+(apply|destroy)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bkubectl\s+delete\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdocker\s+(rm|rmi)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\.env\b|\.pem\b|id_rsa|\.ssh[/\\]|\.aws[/\\]|credentials",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>A ação cai numa categoria sempre-ask?</summary>
    public static bool IsAlwaysAsk(string tool, string? argumentsJson)
    {
        var haystack = tool + " " + (argumentsJson ?? "");
        return AlwaysAskPatterns.Any(p => p.IsMatch(haystack));
    }

    /// <summary>
    /// Fingerprint normalizado da ação (RF-005): tool + args com
    /// whitespace colapsado — preserva paths e valores.
    /// </summary>
    public static string FingerprintOf(string tool, string? argumentsJson)
    {
        var raw = tool + "|" + (argumentsJson ?? "");
        var normalized = Regex.Replace(raw, @"\s+", " ").Trim();
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    /// <summary>Decisão memorizada da sessão, se houver.</summary>
    public static Verdict? Cached(string sessionKey, string fingerprint) =>
        Permitted.TryGetValue(sessionKey, out var bag)
        && bag.TryGetValue(fingerprint, out var v)
            ? new Verdict(v.Decision, v.Reason, fingerprint, null, 0, true)
            : null;

    /// <summary>
    /// Classifica a ação: dedup → always-ask → juiz LLM. Nunca lança —
    /// falha vira <c>ask</c>.
    /// </summary>
    public async Task<Verdict> ClassifyAsync(
        string sessionKey, ChatRun run, string tool, string? argumentsJson,
        string? projectRules, string? conversationContext, CancellationToken ct)
    {
        var fp = FingerprintOf(tool, argumentsJson);
        if (Cached(sessionKey, fp) is { } cached)
        {
            return cached;
        }
        if (IsAlwaysAsk(tool, argumentsJson))
        {
            return new Verdict("ask", "always-ask category", fp, null, 0, false);
        }

        var sw = Stopwatch.StartNew();
        string? model = null;
        try
        {
            var timeoutMs = int.TryParse(
                await config.GetAsync<string?>("smartPermission.timeoutMs", null, ct),
                out var ms) && ms > 0 ? ms : 15000;
            model = await config.GetAsync<string?>("smartPermission.model", null, ct)
                ?? await roles.ResolveAsync("advisor", run.UserId, run.Model, ct);
            var maxCtx = int.TryParse(
                await config.GetAsync<string?>("smartPermission.maxContextTokens", null, ct),
                out var mx) && mx > 0 ? mx * 4 : 8000;

            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8))
                .ToLowerInvariant();
            var permittedList = Permitted.TryGetValue(sessionKey, out var bag2)
                ? string.Join('\n', bag2.Select(kv =>
                    $"- {kv.Key}: {kv.Value.Decision} ({kv.Value.Reason})"))
                : "(none)";
            var user = new StringBuilder()
                .Append($"<project_rules-{nonce}>\n")
                .Append(Trunc(projectRules, maxCtx / 4))
                .Append($"\n</project_rules-{nonce}>\n")
                .Append($"<conversation_context-{nonce}>\n")
                .Append(Trunc(conversationContext, maxCtx / 4))
                .Append($"\n</conversation_context-{nonce}>\n")
                .Append($"<already_permitted-{nonce}>\n")
                .Append(permittedList)
                .Append($"\n</already_permitted-{nonce}>\n")
                .Append($"<action_to_classify-{nonce}>\n")
                .Append(Trunc(tool + " " + (argumentsJson ?? ""), maxCtx / 2))
                .Append($"\n</action_to_classify-{nonce}>")
                .ToString();

            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(timeoutMs));
            using var linked = CancellationTokenSource
                .CreateLinkedTokenSource(ct, timeout.Token);
            var step = await complete(
                new ChatCompletionRequest(model,
                    [new("system", s_systemPrompt.Value), new("user", user)],
                    Stream: false),
                run.UserId, linked.Token);
            var verdict = Parse(step.Content, fp, model, sw.ElapsedMilliseconds);
            Record(sessionKey, fp, verdict);
            return verdict;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            || !ct.IsCancellationRequested)
        {
            log?.LogWarning(ex, "Juiz smart falhou — fail-safe ask (tool {Tool}).", tool);
            return new Verdict("ask", "judge unavailable", fp, model,
                sw.ElapsedMilliseconds, false);
        }
    }

    /// <summary>Registra decisão allow/deny na sessão (ask não memoriza).</summary>
    private static void Record(string sessionKey, string fp, Verdict v)
    {
        if (v.Decision is "allow" or "deny")
        {
            Permitted.GetOrAdd(sessionKey,
                _ => new ConcurrentDictionary<string, (string, string)>())
                [fp] = (v.Decision, v.Reason);
        }
    }

    /// <summary>Parse estrito do JSON do juiz; qualquer desvio = ask.</summary>
    internal static Verdict Parse(
        string? content, string fp, string? model, long latencyMs)
    {
        try
        {
            var text = (content ?? "").Trim();
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return new Verdict("ask", "unparseable verdict", fp, model, latencyMs, false);
            }
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var decision = doc.RootElement.TryGetProperty("decision", out var d)
                && d.ValueKind == JsonValueKind.String
                    ? d.GetString()!.ToLowerInvariant()
                    : "ask";
            if (decision is not ("allow" or "deny" or "ask"))
            {
                decision = "ask";
            }
            var reason = doc.RootElement.TryGetProperty("reason", out var r)
                && r.ValueKind == JsonValueKind.String
                    ? r.GetString() ?? "" : "";
            return new Verdict(decision, reason, fp, model, latencyMs, false);
        }
        catch (JsonException)
        {
            return new Verdict("ask", "unparseable verdict", fp, model, latencyMs, false);
        }
    }

    /// <summary>Limpa a sessão no fim da run.</summary>
    public static void Clear(string sessionKey) =>
        Permitted.TryRemove(sessionKey, out _);

    private static string Trunc(string? s, int max) =>
        s is null ? "(none)" : s.Length <= max ? s : s[..max] + "…";

    private static string LoadPrompt()
    {
        var asm = typeof(SmartPermissionClassifier).Assembly;
        using var stream = asm.GetManifestResourceStream(
            "OpenWebUI.Infrastructure.prompts.smart-permission-judge.md");
        if (stream is null)
        {
            return "Classify the pending action: allow, deny or ask.";
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
