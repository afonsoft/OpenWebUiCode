using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Avaliador de permissões v2 (SPEC-20261018-permissions-v2 RF-001/RF-002):
/// regras ordenadas do usuário (<see cref="UserPermissionRule"/>,
/// last-match-wins) e aprovações duráveis por workdir
/// (<see cref="SavedApproval"/>). A ordem efetiva no gate de tools é:
/// admin policy deny → ruleset do modo → <b>regras do usuário</b> →
/// <b>aprovação salva</b> → overrides/allowlist legacy → preset/risk gate.
/// </summary>
public class PermissionRuleEvaluator(AppDbContext db)
{
    /// <summary>Efeitos válidos de uma regra.</summary>
    public static readonly HashSet<string> ValidEffects =
        new(StringComparer.OrdinalIgnoreCase) { "allow", "ask", "deny" };

    /// <summary>Ações válidas de uma regra.</summary>
    public static readonly HashSet<string> ValidActions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "read", "edit", "glob", "grep", "shell", "subagent",
            "skill", "question", "webfetch", "websearch",
            "external_directory", "mcp",
        };

    /// <summary>
    /// Normaliza tool → ação (RF-001): <c>file_read</c>/<c>file_list</c> →
    /// <c>read</c>, <c>file_write</c>/<c>file_edit</c>/<c>apply_patch</c> →
    /// <c>edit</c>, <c>file_grep</c> → <c>grep</c>, <c>file_glob</c> →
    /// <c>glob</c>, <c>shell_exec</c>/<c>code_interpreter</c> →
    /// <c>shell</c>, <c>delegate_task</c> → <c>subagent</c>,
    /// <c>ask_user</c> → <c>question</c>, <c>fetch_url</c> →
    /// <c>webfetch</c>, <c>web_search</c> → <c>websearch</c>, tools MCP →
    /// <c>mcp:{server}</c> (regra <c>mcp</c> casa qualquer servidor).
    /// </summary>
    public static string ActionOf(string toolName)
    {
        var name = PermissionRuleset.BuiltinName(toolName);
        return name switch
        {
            "file_read" or "file_list" => "read",
            "file_write" or "file_edit" or "apply_patch" => "edit",
            "file_grep" => "grep",
            "file_glob" => "glob",
            "shell_exec" or "code_interpreter" => "shell",
            "delegate_task" => "subagent",
            "ask_user" => "question",
            "fetch_url" => "webfetch",
            "web_search" => "websearch",
            _ => name.StartsWith("mcp_", StringComparison.OrdinalIgnoreCase)
                ? "mcp"
                : name,
        };
    }

    /// <summary>
    /// Efeito da última regra do usuário que casa
    /// (<paramref name="action"/>, <paramref name="resourceCandidates"/>) —
    /// <c>null</c> quando nenhuma regra se aplica.
    /// </summary>
    /// <param name="resourceCandidates">
    /// Formas casadas pelo glob de <see cref="UserPermissionRule.Resource"/>
    /// (ex.: <c>shell_exec</c>, <c>shell_exec:exec:git</c> ou o path
    /// absoluto para <c>external_directory</c>).
    /// </param>
    public async Task<string?> EvaluateAsync(
        string userId, string action, IReadOnlyList<string> resourceCandidates,
        CancellationToken ct = default)
    {
        var rules = await db.UserPermissionRules.AsNoTracking()
            .Where(r => r.UserId == userId && r.Action == action)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);
        return rules
            .Where(r => MatchesAny(r.Resource, resourceCandidates))
            .Select(r => r.Effect)
            .LastOrDefault();
    }

    /// <summary>Sobrecarga para um único candidato.</summary>
    public Task<string?> EvaluateAsync(
        string userId, string action, string resource, CancellationToken ct = default) =>
        EvaluateAsync(userId, action, new[] { resource }, ct);

    /// <summary>
    /// Decisão gravada ("sempre") para a call — <c>true</c> auto-aprova,
    /// <c>false</c> rejeição durável, <c>null</c> sem regra salva. Casa por
    /// (<paramref name="userId"/>, <paramref name="workdir"/>,
    /// <paramref name="toolName"/>) com <see cref="SavedApproval.ArgPattern"/>
    /// em glob sobre a assinatura da call.
    /// </summary>
    public async Task<bool?> SavedDecisionAsync(
        string userId, string workdir, string toolName, string argPattern,
        CancellationToken ct = default)
    {
        var saved = await db.SavedApprovals.AsNoTracking()
            .Where(s => s.UserId == userId && s.Workdir == workdir
                && s.Tool == toolName)
            .ToListAsync(ct);
        var match = saved
            .Where(s => s.ArgPattern == "*"
                || WorkspaceFiles.GlobToRegex(s.ArgPattern).IsMatch(argPattern))
            .LastOrDefault();
        return match is null ? null : match.Decision == "allow";
    }

    /// <summary>True quando o glob casa qualquer candidato.</summary>
    private static bool MatchesAny(string pattern, IReadOnlyList<string> candidates)
    {
        if (pattern == "*")
        {
            return true;
        }
        var regex = WorkspaceFiles.GlobToRegex(pattern);
        return candidates.Any(regex.IsMatch);
    }
}
