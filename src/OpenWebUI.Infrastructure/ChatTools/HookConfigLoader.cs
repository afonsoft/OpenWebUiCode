using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Loader de hooks multi-fonte (SPEC-20261031-lifecycle-hooks RF-007):
/// repo vinculado — <c>.openwebui/hooks.json</c> (nativo),
/// <c>.devin/hooks.v1.json</c>, chave <c>hooks</c> de
/// <c>.devin/config.json</c> e <c>.claude/settings*.json</c> — e
/// <c>~/.config/openwebui/hooks.json</c> do usuário. Ordem repo &gt;
/// usuário (hooks do repo rodam primeiro). Chaves desconhecidas =
/// warning não fatal.
/// </summary>
public static class HookConfigLoader
{
    /// <summary>Eventos reconhecidos no MVP.</summary>
    public static readonly string[] KnownEvents =
    [
        "PreToolUse", "PostToolUse", "UserPromptSubmit",
        "Stop", "SessionStart", "SessionEnd",
        // futuros — aceitos no parse, ignorados sem ponto de integração
        "PermissionRequest", "PostCompaction",
    ];

    /// <summary>
    /// Carrega todos os hooks aplicáveis ao workdir; <paramref name="home"/>
    /// injetável para testes (default = home do usuário).
    /// </summary>
    public static List<HookDefinition> Load(
        string? workdir, ILogger? log = null, string? home = null)
    {
        var hooks = new List<HookDefinition>();
        if (!string.IsNullOrWhiteSpace(workdir))
        {
            LoadFile(hooks, Path.Combine(workdir, ".openwebui", "hooks.json"), log);
            LoadFile(hooks, Path.Combine(workdir, ".devin", "hooks.v1.json"), log);
            LoadKey(hooks, Path.Combine(workdir, ".devin", "config.json"), "hooks", log);
            var claude = Path.Combine(workdir, ".claude");
            LoadKey(hooks, Path.Combine(claude, "settings.json"), "hooks", log);
            LoadKey(hooks, Path.Combine(claude, "settings.local.json"), "hooks", log);
        }
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            LoadFile(hooks, Path.Combine(home, ".config", "openwebui", "hooks.json"), log);
        }
        return hooks;
    }

    /// <summary>Arquivo inteiro = definição (nativo + hooks.v1).</summary>
    private static void LoadFile(
        List<HookDefinition> hooks, string path, ILogger? log)
    {
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            ParseHooks(doc.RootElement, path, hooks, log);
        }
        catch (JsonException ex)
        {
            log?.LogWarning("Hooks: JSON inválido em {Path}: {Msg}", path, ex.Message);
        }
    }

    /// <summary>Chave aninhada de um settings JSON (compat claude/devin).</summary>
    private static void LoadKey(
        List<HookDefinition> hooks, string path, string key, ILogger? log)
    {
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty(key, out var el))
            {
                ParseHooks(el, path, hooks, log);
            }
        }
        catch (JsonException ex)
        {
            log?.LogWarning("Hooks: JSON inválido em {Path}: {Msg}", path, ex.Message);
        }
    }

    /// <summary>
    /// Parse do schema <c>{"&lt;Event&gt;": [{matcher?, hooks:[]}]}</c>
    /// — aceita também hook direto na lista
    /// (<c>{type, command, matcher?}</c>).
    /// </summary>
    private static void ParseHooks(
        JsonElement root, string source,
        List<HookDefinition> hooks, ILogger? log)
    {
        // Compat: {"hooks": {...}} como objeto raiz.
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("hooks", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            root = nested;
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (var ev in root.EnumerateObject())
        {
            if (!KnownEvents.Contains(ev.Name, StringComparer.OrdinalIgnoreCase))
            {
                log?.LogWarning(
                    "Hooks: evento desconhecido '{Event}' em {Path} — ignorado.",
                    ev.Name, source);
                continue;
            }
            if (ev.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var group in ev.Value.EnumerateArray())
            {
                var matcher = Str(group, "matcher");
                if (group.TryGetProperty("hooks", out var inner)
                    && inner.ValueKind == JsonValueKind.Array)
                {
                    foreach (var h in inner.EnumerateArray())
                    {
                        AddHook(hooks, ev.Name, matcher, h, source, log);
                    }
                }
                else
                {
                    AddHook(hooks, ev.Name, matcher, group, source, log);
                }
            }
        }
    }

    private static void AddHook(
        List<HookDefinition> hooks, string evName, string? matcher,
        JsonElement h, string source, ILogger? log)
    {
        var type = Str(h, "type") ?? "command";
        var command = Str(h, "command") ?? Str(h, "prompt");
        if (string.IsNullOrWhiteSpace(command))
        {
            log?.LogWarning(
                "Hooks: hook sem command/prompt em {Path} — ignorado.", source);
            return;
        }
        if (matcher is not null && !IsValidRegex(matcher))
        {
            log?.LogWarning(
                "Hooks: matcher regex inválido '{Matcher}' em {Path} — hook desabilitado.",
                matcher, source);
            return;
        }
        var timeout = h.TryGetProperty("timeout", out var t)
            && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n)
                ? Math.Clamp(n, 1, 300)
                : 60;
        hooks.Add(new HookDefinition(
            evName, matcher ?? Str(h, "matcher"), type, command,
            timeout, source));
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
