using System.Text.Json;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>Decisão do ruleset de permissão por modo do agente.</summary>
public enum PermissionDecision
{
    /// <summary>O modo permite — executa sem passar pelo fluxo de aprovação.</summary>
    Allow = 0,

    /// <summary>O modo não decide — segue o gate normal (preset de aprovação).</summary>
    Ask = 1,

    /// <summary>O modo bloqueia — nem anunciada ao provider; call devolve erro.</summary>
    Deny = 2,
}

/// <summary>
/// Ruleset de permissão por modo do agente
/// (SPEC-20261009-agent-modes-plan-build RF-002, padrão do
/// <c>permission/evaluate.ts</c> do opencode): no modo <c>plan</c> as
/// tools de escrita/execução/rede são negadas (não anunciadas ao provider;
/// uma call que escape devolve erro estruturado) e só o conjunto de
/// leitura + <c>plan_exit</c> roda livre. Em <c>build</c> (default) o
/// ruleset não decide — vale o gate do preset como antes.
/// </summary>
public static class PermissionRuleset
{
    /// <summary>Modo completo do agente (default) — tools completas.</summary>
    public const string BuildMode = "build";

    /// <summary>Modo somente-leitura — produz plano e pede aprovação.</summary>
    public const string PlanMode = "plan";

    /// <summary>
    /// Conversa comum sem workspace (SPEC-15): tools de arquivo/shell/jobs/
    /// worktree não são anunciadas e uma call que escape devolve erro —
    /// é o modo disponível em chats sem repositório vinculado.
    /// </summary>
    public const string ChatMode = "chat";

    /// <summary>Normaliza o modo persistido; desconhecido → <see cref="BuildMode"/>.</summary>
    public static string Normalize(string? mode)
    {
        if (string.Equals(mode, PlanMode, StringComparison.OrdinalIgnoreCase))
        {
            return PlanMode;
        }
        return string.Equals(mode, ChatMode, StringComparison.OrdinalIgnoreCase)
            ? ChatMode
            : BuildMode;
    }

    /// <summary>
    /// Built-ins permitidas no modo plan (somente leitura + a saída do
    /// modo): read/list/grep/glob, web/fetch, ask, todo, plan_exit e os
    /// readers de jobs/screenshot/workflows.
    /// </summary>
    private static readonly HashSet<string> PlanAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_list", "file_read", "file_grep", "file_glob",
        "web_search", "fetch_url", "ask_user", "todo_write",
        "plan_exit", "browser_screenshot",
        "job_list", "job_output", "n8n_list_workflows",
    };

    /// <summary>
    /// Decide uma call pelo modo do chat. <paramref name="toolName"/> é o
    /// nome da função chamada (aceita <c>builtin_*</c>/<c>builtin:*</c> ou
    /// o nome puro); <paramref name="isBuiltin"/> indica se a tool é
    /// built-in — tools externas (HTTP/MCP/python) são negadas no plan por
    /// abrirem rede/código arbitrário. <paramref name="argsSummary"/>
    /// reserva-se para regras por padrão de argumento (ex.: whitelist de
    /// comandos read-only no futuro).
    /// </summary>
    /// <summary>
    /// Built-ins que tocam o workspace (SPEC-15, modo <c>chat</c>): sistema
    /// de arquivos, shell/exec, jobs, worktrees e delegação de subtarefas
    /// (que herdariam o binding do chat) — negadas e nem anunciadas quando
    /// a conversa não tem repositório vinculado.
    /// </summary>
    private static readonly HashSet<string> WorkspaceTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_list", "file_read", "file_grep", "file_glob",
        "file_write", "file_edit", "apply_patch", "shell_exec",
        "code_interpreter", "job_list", "job_output", "job_kill",
        "worktree_diff", "worktree_merge", "plan_exit", "delegate_task",
        "lsp_diagnostics", "lsp_symbols", "lsp_workspace_symbols",
        "lsp_definition", "lsp_references", "lsp_hover", "skill",
    };

    public static PermissionDecision Evaluate(
        string? mode, string toolName, bool isBuiltin = true, string? argsSummary = null)
    {
        var normalized = Normalize(mode);
        if (normalized == ChatMode)
        {
            // chat: nega só as tools de workspace — o resto (web, memória,
            // ask_user, MCP/HTTP) segue o gate normal do preset.
            return isBuiltin && WorkspaceTools.Contains(BuiltinName(toolName))
                ? PermissionDecision.Deny
                : PermissionDecision.Ask;
        }
        if (normalized != PlanMode)
        {
            // build: o ruleset não restringe — o preset decide por call.
            return PermissionDecision.Ask;
        }

        var name = BuiltinName(toolName);
        return isBuiltin && PlanAllowed.Contains(name)
            ? PermissionDecision.Allow
            : PermissionDecision.Deny;
    }

    /// <summary>Sobrecarga com o <see cref="Tool"/> carregado (builtin-ness pela URL).</summary>
    public static PermissionDecision Evaluate(string? mode, Tool tool, string? argsSummary = null)
    {
        var isBuiltin = tool.Url?.StartsWith(
            BuiltinToolRegistry.UrlPrefix, StringComparison.OrdinalIgnoreCase) == true;
        var name = isBuiltin
            ? tool.Url![BuiltinToolRegistry.UrlPrefix.Length..]
            : ToolExecutor.FunctionName(tool) ?? tool.Name;
        return Evaluate(mode, name, isBuiltin, argsSummary);
    }

    /// <summary>Se a tool entra no spec anunciado ao provider neste modo.</summary>
    public static bool ShouldAnnounce(string? mode, Tool tool)
    {
        var isBuiltin = tool.Url?.StartsWith(
            BuiltinToolRegistry.UrlPrefix, StringComparison.OrdinalIgnoreCase) == true;
        var name = isBuiltin ? tool.Url![BuiltinToolRegistry.UrlPrefix.Length..] : null;
        if (string.Equals(name, "plan_exit", StringComparison.OrdinalIgnoreCase))
        {
            // plan_exit só é anunciado no modo plan — fora dele nem existe.
            return Normalize(mode) == PlanMode;
        }
        if (Normalize(mode) == ChatMode)
        {
            return !(isBuiltin && WorkspaceTools.Contains(name!));
        }
        return Normalize(mode) != PlanMode
            || Evaluate(mode, tool) != PermissionDecision.Deny;
    }

    /// <summary>Extrai o nome puro de uma call (<c>builtin:x</c>/<c>builtin_x</c>/x).</summary>
    public static string BuiltinName(string toolName) =>
        toolName.StartsWith(BuiltinToolRegistry.IdPrefix, StringComparison.OrdinalIgnoreCase)
            ? toolName[BuiltinToolRegistry.IdPrefix.Length..]
            : toolName.StartsWith(BuiltinToolRegistry.SpecPrefix, StringComparison.OrdinalIgnoreCase)
                ? toolName[BuiltinToolRegistry.SpecPrefix.Length..]
                : toolName;

    /// <summary>
    /// Assinatura de argumentos da call para a allowlist "sempre nesta
    /// sessão" (RF-005): reduz os args a um padrão estável que reaprova a
    /// mesma classe de ação — <c>exec:{comando}</c> pelo primeiro token,
    /// <c>write:{dir}/**</c> pelo diretório, <c>fetch:{scheme}://{host}/**</c>
    /// pelo host e <c>*</c> (tool inteira) no resto. Matching é glob
    /// (<see cref="WorkspaceFiles.GlobToRegex"/>).
    /// </summary>
    public static string ArgPattern(string toolName, string? argumentsJson)
    {
        var name = BuiltinName(toolName);
        switch (name)
        {
            case "shell_exec":
            {
                var command = TryGetString(argumentsJson, "command") ?? string.Empty;
                var first = command.TrimStart().Split(' ', 2,
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "*";
                return $"exec:{first}";
            }
            case "file_write":
            case "file_edit":
            {
                var path = (TryGetString(argumentsJson, "path") ?? string.Empty)
                    .Replace('\\', '/');
                var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] : ".";
                return $"write:{dir}/**";
            }
            case "fetch_url":
            {
                var url = TryGetString(argumentsJson, "url") ?? string.Empty;
                return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    ? $"fetch:{uri.Scheme}://{uri.Host}/**"
                    : "fetch:*";
            }
            default:
                return "*";
        }
    }

    /// <summary>Lê um campo string do JSON de argumentos (defensivo).</summary>
    private static string? TryGetString(string? json, string field)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(field, out var el)
                    ? el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
