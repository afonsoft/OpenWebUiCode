namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Overrides persistentes de aprovação por tool, por usuário
/// (SPEC-20261013-approval-tiers RF-002): kv
/// <c>user:{userId}:tool-approvals</c> = <c>{toolName: allow|deny|prompt}</c>.
/// A chave é o nome puro da função (<c>shell_exec</c>, <c>file_read</c>,
/// ou o nome de uma tool MCP/HTTP).
/// </summary>
public sealed class ToolApprovalOverrideService(ConfigService config)
{
    private static readonly HashSet<string> s_validActions =
        new(StringComparer.OrdinalIgnoreCase) { "allow", "deny", "prompt" };

    /// <summary>Se <paramref name="action"/> é um valor válido de override.</summary>
    public static bool IsValidAction(string action) =>
        s_validActions.Contains(action);

    private static string Key(string userId) => $"user:{userId}:tool-approvals";

    /// <summary>Lê os overrides do usuário (mapa tool → ação).</summary>
    public async Task<Dictionary<string, string>> GetAsync(
        string userId, CancellationToken ct) =>
        await config.GetAsync(Key(userId),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ct)
        is { } dict
            ? new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mescla um patch: valor <c>null</c>/vazio remove a chave; valor
    /// inválido é ignorado. Devolve o mapa resultante.
    /// </summary>
    public async Task<Dictionary<string, string>> MergeAsync(
        string userId, IReadOnlyDictionary<string, string?> patch, CancellationToken ct)
    {
        var current = await GetAsync(userId, ct);
        foreach (var (tool, action) in patch)
        {
            var name = tool?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(action))
            {
                current.Remove(name);
            }
            else if (s_validActions.Contains(action))
            {
                current[name] = action.Trim().ToLowerInvariant();
            }
        }
        await config.SetAsync(Key(userId), current, ct);
        return current;
    }
}
