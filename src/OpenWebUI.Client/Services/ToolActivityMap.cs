namespace OpenWebUI.Client.Services;

/// <summary>
/// Mapeia nomes de tools (builtin_*/mcp/custom) para linhas de atividade
/// amigáveis (SPEC-20261010-chat-activity-feed RF-001): a linha mostra o
/// que o assistente está fazendo — "Pesquisando na web", "Editando arquivo"
/// — sem expor nome de tool nem JSON de args por padrão.
/// </summary>
public static class ToolActivityMap
{
    private const int TargetMax = 40;

    /// <summary>Campos de args candidatos a virar o alvo curto da linha,
    /// em ordem de preferência.</summary>
    private static readonly string[] TargetFields =
        ["query", "url", "path", "command", "prompt", "pattern"];

    /// <summary>
    /// Resolve (key i18n, alvo curto) para uma tool_call.
    /// O sufixo é o segmento após ':' ou '/' (ex.: builtin_shell_exec →
    /// shell_exec); a classificação é por palavra-chave, case-insensitive.
    /// </summary>
    public static (string Key, string? Target) Map(string toolName, string? argsPreview)
    {
        var suffix = toolName.Split(':', '/').Last();
        if (suffix.StartsWith("builtin_", StringComparison.OrdinalIgnoreCase))
        {
            suffix = suffix["builtin_".Length..];
        }
        var key = KeyFor(suffix.AsSpan());
        var target = Target(argsPreview);
        return (key, target);
    }

    /// <summary>Somente a chave i18n da atividade (para o chip de status).</summary>
    public static string KeyFor(string toolName) => Map(toolName, null).Key;

    private static string KeyFor(ReadOnlySpan<char> suffix)
    {
        var name = suffix.ToString().ToLowerInvariant();
        return name switch
        {
            _ when Contains(name, "web_search", "search_web", "brave", "google_search")
                => "chat.activity.web_search",
            _ when Contains(name, "fetch", "open_url", "visit")
                => "chat.activity.fetch",
            _ when Contains(name, "browser_screenshot", "screenshot")
                => "chat.activity.screenshot",
            _ when Contains(name, "browser")
                => "chat.activity.fetch",
            _ when Contains(name, "read_file", "file_read", "view")
                => "chat.activity.read",
            _ when Contains(name, "write_file", "file_write", "file_edit", "edit",
                "apply_patch", "create_file", "worktree_merge")
                => "chat.activity.edit",
            _ when Contains(name, "shell_exec", "bash", "terminal", "exec",
                "code_interpreter", "job_", "run")
                => "chat.activity.exec",
            _ when Contains(name, "delegate", "subagent", "spawn", "agent")
                => "chat.activity.delegate",
            _ when Contains(name, "memory")
                => "chat.activity.memory",
            _ when Contains(name, "list_files", "file_list", "file_grep", "file_glob",
                "grep", "search_files", "find", "lsp_", "worktree_diff")
                => "chat.activity.search_code",
            _ when Contains(name, "video")
                => "chat.activity.video",
            _ when Contains(name, "image")
                => "chat.activity.image",
            _ when Contains(name, "decision_auto")
                => "chat.activity.decision_auto",
            _ when Contains(name, "advisor")
                => "chat.activity.advisor",
            _ => "chat.activity.tool",
        };
    }

    private static bool Contains(string name, params string[] keywords) =>
        keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>Alvo curto da linha: campo principal do args preview
    /// (ToolArgsPreview.Summary) ou o primeiro campo conhecido, ≤40 chars.</summary>
    private static string? Target(string? argsPreview)
    {
        if (string.IsNullOrWhiteSpace(argsPreview))
        {
            return null;
        }
        foreach (var field in TargetFields)
        {
            if (ToolArgsPreview.TryGetField(argsPreview, field) is { Length: > 0 } value)
            {
                var trimmed = value.Trim();
                return trimmed.Length <= TargetMax
                    ? trimmed
                    : string.Concat(trimmed.AsSpan(0, TargetMax), "…");
            }
        }
        return null;
    }
}
