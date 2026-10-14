using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>Ação de um override de aprovação do usuário sobre uma tool.</summary>
public enum ToolOverride
{
    /// <summary>Sem override — vale o preset.</summary>
    None = 0,

    /// <summary>Sempre aprova (exceto deny do modo do agente).</summary>
    Allow = 1,

    /// <summary>Sempre pergunta — inclusive em <c>always-allow</c>.</summary>
    Prompt = 2,

    /// <summary>Sempre nega — a call devolve erro estruturado ao modelo.</summary>
    Deny = 3,
}

/// <summary>
/// Política de tiers + overrides de aprovação (SPEC-20261013-approval-tiers
/// RF-002/RF-003): o override do usuário por tool
/// (<c>allow|deny|prompt</c>, persistido em
/// <c>user:{id}:tool-approvals</c>) precede o preset, mas nunca
/// reabilita um <see cref="PermissionDecision.Deny"/> do modo do agente.
/// O tier efetivo de uma <see cref="Tool"/> qualquer é derivado:
/// built-in → tier declarado; MCP → <see cref="ApprovalTier.Write"/>;
/// python/arbitrário → <see cref="ApprovalTier.Exec"/>; HTTP comum segue
/// <c>RequiresApproval</c> (<see cref="ApprovalTier.Exec"/> quando mutável).
/// </summary>
public static class ToolApprovalPolicy
{
    /// <summary>Tier efetivo de uma tool carregada (built-in ou externa).</summary>
    public static ApprovalTier TierOf(Tool tool, BuiltinToolRegistry? builtins = null)
    {
        var url = tool.Url ?? string.Empty;
        if (url.StartsWith(BuiltinToolRegistry.UrlPrefix, StringComparison.Ordinal))
        {
            var name = url[BuiltinToolRegistry.UrlPrefix.Length..];
            return builtins?.TierOfName(name) ?? ApprovalTier.Exec;
        }
        if (!string.IsNullOrWhiteSpace(tool.Code))
        {
            return ApprovalTier.Exec;
        }
        if (McpClientService.ParseVirtualUrl(url) is not null)
        {
            return ApprovalTier.Write;
        }
        return tool.RequiresApproval ? ApprovalTier.Exec : ApprovalTier.Read;
    }

    /// <summary>
    /// Resolve o override do usuário para a tool da call. A chave é o
    /// nome puro da função (<see cref="PermissionRuleset.BuiltinName"/>
    /// remove os prefixos <c>builtin:</c>/<c>builtin_</c>).
    /// </summary>
    public static ToolOverride OverrideFor(
        IReadOnlyDictionary<string, string> overrides, string toolName)
    {
        var key = PermissionRuleset.BuiltinName(toolName).ToLowerInvariant();
        return overrides.TryGetValue(key, out var raw)
            ? raw?.Trim().ToLowerInvariant() switch
            {
                "allow" => ToolOverride.Allow,
                "deny" => ToolOverride.Deny,
                "prompt" => ToolOverride.Prompt,
                _ => ToolOverride.None,
            }
            : ToolOverride.None;
    }
}
