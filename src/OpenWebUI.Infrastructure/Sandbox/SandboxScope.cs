using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.Sandbox;

/// <summary>
/// Escopo efetivo de uma execução em sandbox
/// (SPEC-20261031-sandboxed-exec RF-002/RF-005): workspace sempre rw,
/// <see cref="WriteScopes"/> adiciona mounts rw, <see cref="DenyReads"/>
/// esconde paths sensíveis, <see cref="AllowedDomains"/> alimenta o proxy,
/// <see cref="ExcludedTools"/> roda fora do sandbox por decisão consciente.
/// </summary>
public sealed record SandboxScope(
    IReadOnlyList<string> WriteScopes,
    IReadOnlyList<string> DenyReads,
    IReadOnlyList<string> AllowedDomains,
    IReadOnlyList<string> ExcludedTools,
    bool NetworkEnabled)
{
    /// <summary>
    /// Deny-reads padrão: secrets e bases sensíveis — default configurável
    /// (append em <c>Sandbox:DenyReads</c>; <c>Sandbox:DisableDefaultDeny</c>
    /// desliga o conjunto padrão).
    /// </summary>
    public static readonly string[] DefaultDenyReads =
    [
        ".env", ".ssh", ".aws", ".gnupg", "id_rsa", "id_ed25519",
        "credentials", "webui.db", "webui.db-shm", "webui.db-wal",
    ];

    /// <summary>
    /// Concessões de domínio aprovadas na sessão (request_scope) —
    /// chave = <c>runId|workspace</c>, valor = conjunto de domínios.
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<
        string, System.Collections.Concurrent.ConcurrentDictionary<string, byte>>
        SessionDomainGrants = new(StringComparer.Ordinal);

    /// <summary>Concede um domínio extra à sessão (após aprovação).</summary>
    public static void GrantDomain(string sessionKey, string domain)
    {
        var bag = SessionDomainGrants.GetOrAdd(sessionKey,
            _ => new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        bag.TryAdd(domain, 1);
    }

    /// <summary>Domínios concedidos à sessão, se houver.</summary>
    public static IReadOnlyCollection<string> GrantedDomains(string sessionKey) =>
        SessionDomainGrants.TryGetValue(sessionKey, out var bag)
            ? bag.Keys.ToArray()
            : [];

    /// <summary>Limpa concessões da sessão no fim da run.</summary>
    public static void ClearSession(string sessionKey) =>
        SessionDomainGrants.TryRemove(sessionKey, out _);

    /// <summary>Tool excluída roda fora do sandbox (allowlist explícita).</summary>
    public bool IsExcluded(string toolName) =>
        ExcludedTools.Any(t =>
            string.Equals(t, toolName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Carrega o escopo a partir de <c>Sandbox:*</c> no
    /// <see cref="ConfigService"/> (listas como arrays JSON).
    /// </summary>
    public static async Task<SandboxScope> LoadAsync(
        ConfigService config, string sessionKey, CancellationToken ct)
    {
        var write = await config.GetAsync<string[]>(
            "Sandbox:WriteScopes", [], ct);
        var deny = (await config.GetAsync<string[]>(
            "Sandbox:DenyReads", [], ct)).ToList();
        var allow = (await config.GetAsync<string[]>(
            "Sandbox:AllowedDomains", [], ct)).ToList();
        var excluded = await config.GetAsync<string[]>(
            "Sandbox:ExcludedTools", [], ct);
        var network = await config.GetAsync("Sandbox:Network", true, ct);
        var disableDefault = await config.GetAsync(
            "Sandbox:DisableDefaultDeny", false, ct);

        if (!disableDefault)
        {
            deny.AddRange(DefaultDenyReads);
        }
        allow.AddRange(GrantedDomains(sessionKey));
        return new SandboxScope(write, deny.Distinct().ToArray(),
            allow.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            excluded, network);
    }
}
