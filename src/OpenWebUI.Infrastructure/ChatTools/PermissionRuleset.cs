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
/// <c>permission/evaluate.ts</c> do opencode): no modo <c>plan</c> o
/// conjunto de leitura + <c>plan_exit</c> roda livre, a escrita fica
/// restrita a artefatos de planejamento (planos/SPECs em markdown) e a
/// execução (shell/intérprete — builds, testes, git) é anunciada mas
/// cai no gate do preset como mutação — aprovada, roda para a análise
/// detalhada (SPEC-20261030-plan-exec-analysis). O restante é negado
/// (não anunciado ao provider; call que escape devolve erro estruturado).
/// Em <c>build</c> (default) o ruleset não decide — vale o gate do
/// preset como antes.
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
    public static string Normalize(string? mode) =>
        mode switch
        {
            { } m when string.Equals(m, PlanMode, StringComparison.OrdinalIgnoreCase) => PlanMode,
            { } m when string.Equals(m, ChatMode, StringComparison.OrdinalIgnoreCase) => ChatMode,
            _ => BuildMode,
        };

    /// <summary>
    /// Built-ins permitidas no modo plan: read/list/grep/glob, web/fetch,
    /// ask, todo, plan_exit, readers de jobs/screenshot/workflows e a
    /// leitura estrutural do projeto — <c>worktree_diff</c>, <c>specs</c>
    /// (o ciclo SDD é jailed em <c>.specs/</c> — a tool mutável segue o
    /// preset na op transition) e as tools LSP (diagnóstico/símbolos são
    /// read-only por natureza).
    /// </summary>
    private static readonly HashSet<string> PlanAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_list", "file_read", "file_grep", "file_glob",
        "web_search", "fetch_url", "ask_user", "todo_write",
        "plan_exit", "write_plan", "browser_screenshot",
        "browser_navigate", "browser_observe", "browser_close",
        "job_list", "job_output", "n8n_list_workflows",
        "worktree_diff", "specs",
        "lsp_diagnostics", "lsp_symbols", "lsp_workspace_symbols",
        "lsp_definition", "lsp_references", "lsp_hover",
    };

    /// <summary>
    /// Built-ins de escrita que o modo plan só aceita em artefatos de
    /// planejamento (SPEC-20261030-plan-exec-analysis): o alvo precisa ser
    /// um arquivo <c>*.md</c> ou viver sob <c>.openwebui/</c>,
    /// <c>.specs/</c>, <c>docs/specs/</c> ou <c>plans/</c> — decidido por
    /// <see cref="PlanWriteDecision"/> a partir dos args da call.
    /// </summary>
    private static readonly HashSet<string> PlanSpecWriters = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_write", "file_edit", "apply_patch",
    };

    /// <summary>
    /// Built-ins de execução que o modo plan anuncia sem liberar de graça
    /// (SPEC-20261030-plan-exec-analysis): <see cref="Ask"/> devolve o
    /// controle ao gate do preset — builds, testes e comandos git
    /// (clone/status/diff/log/commit para gravar SPECs) rodam mediante
    /// aprovação do dono, exatamente como em build/chat.
    /// </summary>
    private static readonly HashSet<string> PlanExecGated = new(StringComparer.OrdinalIgnoreCase)
    {
        "shell_exec", "code_interpreter",
    };

    /// <summary>
    /// Diretórios do workdir onde a escrita em modo plan é livre de
    /// restrição adicional (além do <c>*.md</c> aceito em qualquer lugar).
    /// </summary>
    private static readonly string[] PlanWritableDirs =
        [".openwebui/", ".specs/", "docs/specs/", "plans/"];

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
    /// Built-ins que tocam o workspace (SPEC-15): sistema de arquivos,
    /// shell/exec, jobs, worktrees e delegação de subtarefas (que herdam
    /// o binding do chat). No modo <c>plan</c> fora da allowlist são
    /// negadas; em <c>chat</c> seguem o preset — a pasta scratch da
    /// conversa garante onde escrever (SPEC-20261027).
    /// </summary>
    private static readonly HashSet<string> WorkspaceTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "file_list", "file_read", "file_grep", "file_glob",
        "file_write", "file_edit", "apply_patch", "shell_exec",
        "code_interpreter", "job_list", "job_output", "job_kill",
        "worktree_diff", "worktree_merge", "plan_exit", "delegate_task",
        "specs",
        "lsp_diagnostics", "lsp_symbols", "lsp_workspace_symbols",
        "lsp_definition", "lsp_references", "lsp_hover",
    };

    /// <summary>
    /// Subconjunto de <see cref="WorkspaceTools"/> que só faz sentido com
    /// repo/worktree real — não é anunciado no modo chat mesmo com scratch
    /// (evita ruído: worktree/lsp/specs exigem projeto, não pasta vazia).
    /// </summary>
    private static readonly HashSet<string> BuildOnlyTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "worktree_diff", "worktree_merge", "specs",
        "lsp_diagnostics", "lsp_symbols", "lsp_workspace_symbols",
        "lsp_definition", "lsp_references", "lsp_hover",
    };

    public static PermissionDecision Evaluate(
        string? mode, string toolName, bool isBuiltin = true, string? argsSummary = null)
    {
        var normalized = Normalize(mode);
        if (normalized == ChatMode)
        {
            // SPEC-20261027-scratch-workspace: toda conversa tem pasta
            // scratch — workspace tools não são mais negadas por falta de
            // repo; seguem o preset (Ask) como qualquer outra tool.
            return PermissionDecision.Ask;
        }
        if (normalized != PlanMode)
        {
            // build: o ruleset não restringe — o preset decide por call.
            return PermissionDecision.Ask;
        }

        var name = BuiltinName(toolName);
        if (!isBuiltin)
        {
            // Tools externas (HTTP/MCP/python) abrem rede/código arbitrário
            // — seguem negadas no plan.
            return PermissionDecision.Deny;
        }
        if (PlanAllowed.Contains(name))
        {
            return PermissionDecision.Allow;
        }
        if (PlanExecGated.Contains(name))
        {
            // Execução para análise (build/testes/git) — o preset decide.
            return PermissionDecision.Ask;
        }
        return PlanSpecWriters.Contains(name)
            ? PlanWriteDecision(name, argsSummary)
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

    /// <summary>
    /// Decisão da escrita em modo plan (SPEC-20261030): todos os paths
    /// tocados precisam ser artefatos de planejamento — <c>*.md</c> ou sob
    /// <see cref="PlanWritableDirs"/>. Path fora do escopo → Deny; args
    /// sem path legível → Ask (a prévia da aprovação decide).
    /// </summary>
    private static PermissionDecision PlanWriteDecision(string name, string? argumentsJson)
    {
        var paths = PlanWritePaths(name, argumentsJson);
        if (paths.Count == 0)
        {
            return PermissionDecision.Ask;
        }
        return paths.All(IsPlanWritablePath)
            ? PermissionDecision.Allow
            : PermissionDecision.Deny;
    }

    /// <summary>Paths-alvo da call: <c>path</c> direto ou os cabeçalhos
    /// <c>*** … File:</c>/<c>*** Move to:</c> do patch OpenAI.</summary>
    private static List<string> PlanWritePaths(string name, string? argumentsJson)
    {
        if (string.Equals(name, "apply_patch", StringComparison.OrdinalIgnoreCase))
        {
            var patch = TryGetString(argumentsJson, "patch");
            if (string.IsNullOrWhiteSpace(patch))
            {
                return [];
            }
            var ops = ApplyPatch.Parse(patch, out _);
            return ops is null
                ? []
                : ops.SelectMany(op => op.MoveTo is null
                        ? new[] { op.Path }
                        : new[] { op.Path, op.MoveTo })
                    .ToList();
        }
        var path = TryGetString(argumentsJson, "path");
        return string.IsNullOrWhiteSpace(path) ? [] : [path];
    }

    /// <summary>Path dentro do escopo de planejamento (normalizado: só
    /// caminho relativo ao workdir, sem <c>..</c>, sem absoluto/drive).</summary>
    internal static bool IsPlanWritablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        var normalized = path.Replace('\\', '/');
        // Absoluto posix ou drive windows — fora do workdir relativo.
        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            return false;
        }
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        if (normalized.Split('/').Contains(".."))
        {
            return false;
        }
        return PlanWritableDirs.Any(d =>
                normalized.StartsWith(d, StringComparison.OrdinalIgnoreCase))
            || normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
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
            // SPEC-20261027: com scratch garantido, file/shell/delegate
            // entram no spec anunciado em modo chat — exceto as que só
            // servem com repo real (worktree, lsp, specs).
            return !(isBuiltin && BuildOnlyTools.Contains(name!));
        }
        return Normalize(mode) != PlanMode
            || Evaluate(mode, tool) != PermissionDecision.Deny;
    }

    /// <summary>Extrai o nome puro de uma call (<c>builtin:x</c>/<c>builtin_x</c>/x).</summary>
    public static string BuiltinName(string toolName)
    {
        if (toolName.StartsWith(BuiltinToolRegistry.IdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return toolName[BuiltinToolRegistry.IdPrefix.Length..];
        }
        if (toolName.StartsWith(BuiltinToolRegistry.SpecPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return toolName[BuiltinToolRegistry.SpecPrefix.Length..];
        }
        return toolName;
    }

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
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(field, out var el))
            {
                return el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : el.GetRawText();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
