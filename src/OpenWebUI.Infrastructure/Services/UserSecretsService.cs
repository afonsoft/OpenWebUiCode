using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Secret resolvido para listagem — nunca expõe o valor.</summary>
/// <param name="Name">Nome da variável (ex.: <c>SONAR_TOKEN</c>).</param>
/// <param name="Source"><c>user</c> quando definido pelo usuário (override), <c>global</c> quando herdado do default do admin.</param>
/// <param name="CreatedAt">Epoch seconds da criação.</param>
/// <param name="UpdatedAt">Epoch seconds da última edição.</param>
public sealed record SecretEntry(string Name, string Source, long CreatedAt, long UpdatedAt);

/// <summary>Valor persistido no kv (com timestamps para a listagem).</summary>
public sealed record StoredSecret(string Value, long CreatedAt, long UpdatedAt);

/// <summary>
/// Store de secrets chave→valor em dois níveis
/// (SPEC-20261017-agent-secrets-env RF-001): globais do admin no kv
/// <c>secrets</c> e do usuário em <c>u:{userId}:secrets</c> — no merge o
/// mesmo nome do usuário <b>vence</b> o global (padrão do PAT GitHub).
/// Valores nunca saem pela API; a resolução alimenta o env das tools.
/// </summary>
public sealed class UserSecretsService(ConfigService config)
{
    /// <summary>Máximo de secrets por nível (user e global).</summary>
    public const int MaxPerLevel = 50;

    /// <summary>Tamanho máximo do valor em chars (~8KB).</summary>
    public const int MaxValueChars = 8192;

    /// <summary>Chave kv dos defaults globais do admin.</summary>
    public const string GlobalKey = "secrets";

    private static string UserKey(string userId) => $"u:{userId}:secrets";

    private static readonly Regex NamePattern =
        new("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(250));

    /// <summary>Nomes gerenciados (integração GitHub) ou críticos do processo.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "GITHUB_TOKEN", "GH_TOKEN",
        "PATH", "HOME", "SHELL", "NODE_OPTIONS", "BASH_ENV", "ENV",
    };

    /// <summary>Prefixos reservados: GIT_* cobre GIT_ASKPASS/GIT_TERMINAL_PROMPT e cia.</summary>
    private static readonly string[] ReservedPrefixes = ["GIT_", "LD_", "DYLD_"];

    /// <summary>
    /// Valida o nome do secret. Devolve a mensagem de erro (pt-BR) ou null
    /// quando válido — reusado pelos endpoints user e admin.
    /// </summary>
    public static string? ValidateName(string name)
    {
        if (!NamePattern.IsMatch(name))
        {
            return "Nome inválido — use [A-Za-z_][A-Za-z0-9_] até 64 chars.";
        }
        if (ReservedNames.Contains(name))
        {
            return name is "GITHUB_TOKEN" or "GH_TOKEN"
                ? $"{name} é gerenciado pela integração GitHub (PAT em Integrations)."
                : $"{name} é um nome reservado do ambiente do processo.";
        }
        if (ReservedPrefixes.FirstOrDefault(p => name.StartsWith(p, StringComparison.Ordinal)) is { } hitPrefix)
        {
            return $"Nomes com prefixo {hitPrefix} são reservados.";
        }
        return null;
    }

    /// <summary>Secrets do usuário (raw, com valores) — uso interno.</summary>
    public async Task<Dictionary<string, StoredSecret>> GetUserSecretsAsync(
        string userId, CancellationToken ct) =>
        new(await config.GetAsync<Dictionary<string, StoredSecret>?>(
            UserKey(userId), null, ct) ?? []);

    /// <summary>Secrets globais (raw, com valores) — uso interno.</summary>
    public async Task<Dictionary<string, StoredSecret>> GetGlobalSecretsAsync(
        CancellationToken ct) =>
        new(await config.GetAsync<Dictionary<string, StoredSecret>?>(
            GlobalKey, null, ct) ?? []);

    /// <summary>
    /// Lista mesclada para a API: global ∪ user (user vence) com
    /// <see cref="SecretEntry.Source"/> — sem valores.
    /// </summary>
    public async Task<IReadOnlyList<SecretEntry>> ListResolvedAsync(
        string userId, CancellationToken ct)
    {
        var merged = new Dictionary<string, SecretEntry>(StringComparer.Ordinal);
        foreach (var (name, s) in await GetGlobalSecretsAsync(ct))
        {
            merged[name] = new SecretEntry(name, "global", s.CreatedAt, s.UpdatedAt);
        }
        foreach (var (name, s) in await GetUserSecretsAsync(userId, ct))
        {
            merged[name] = new SecretEntry(name, "user", s.CreatedAt, s.UpdatedAt);
        }
        return merged.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Lista do nível global (admin) — sem valores.</summary>
    public async Task<IReadOnlyList<SecretEntry>> ListGlobalAsync(CancellationToken ct) =>
        (await GetGlobalSecretsAsync(ct))
            .Select(kv => new SecretEntry(kv.Key, "global", kv.Value.CreatedAt, kv.Value.UpdatedAt))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Mapa nome→valor efetivo para o env do processo: global ∪ user
    /// (user sobrescreve). Uso interno — nunca serializar.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveValuesAsync(
        string userId, CancellationToken ct)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, s) in await GetGlobalSecretsAsync(ct))
        {
            env[name] = s.Value;
        }
        foreach (var (name, s) in await GetUserSecretsAsync(userId, ct))
        {
            env[name] = s.Value;
        }
        return env;
    }

    /// <summary>Mapa só da camada global (processos compartilhados — ex.: terminal local).</summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveGlobalValuesAsync(
        CancellationToken ct) =>
        (await GetGlobalSecretsAsync(ct)).ToDictionary(kv => kv.Key, kv => kv.Value.Value);

    /// <summary>
    /// Upsert de secret do usuário. Devolve mensagem de erro ou null.
    /// Cadastrar nome já existente no global vira override legítimo.
    /// </summary>
    public async Task<string?> SetUserAsync(
        string userId, string name, string value, CancellationToken ct) =>
        await SetAsync(UserKey(userId), name, value, ct);

    /// <summary>Upsert de secret global (admin). Devolve mensagem de erro ou null.</summary>
    public async Task<string?> SetGlobalAsync(
        string name, string value, CancellationToken ct) =>
        await SetAsync(GlobalKey, name, value, ct);

    /// <summary>Remove secret do usuário (o global de mesmo nome volta a valer).</summary>
    public async Task DeleteUserAsync(string userId, string name, CancellationToken ct) =>
        await DeleteAsync(UserKey(userId), name, ct);

    /// <summary>Remove secret global (admin).</summary>
    public async Task DeleteGlobalAsync(string name, CancellationToken ct) =>
        await DeleteAsync(GlobalKey, name, ct);

    private async Task<string?> SetAsync(
        string key, string name, string value, CancellationToken ct)
    {
        if (ValidateName(name) is { } error)
        {
            return error;
        }
        if (value.Length > MaxValueChars)
        {
            return $"Valor excede {MaxValueChars} chars.";
        }
        var dict = await config.GetAsync<Dictionary<string, StoredSecret>?>(
            key, null, ct) ?? [];
        if (!dict.ContainsKey(name) && dict.Count >= MaxPerLevel)
        {
            return $"Limite de {MaxPerLevel} secrets por nível.";
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var created = dict.TryGetValue(name, out var existing) ? existing.CreatedAt : now;
        dict[name] = new StoredSecret(value, created, now);
        await config.SetAsync(key, dict, ct);
        return null;
    }

    private async Task DeleteAsync(string key, string name, CancellationToken ct)
    {
        var dict = await config.GetAsync<Dictionary<string, StoredSecret>?>(key, null, ct);
        if (dict is null || !dict.Remove(name))
        {
            return;
        }
        await config.SetAsync(key, dict, ct);
    }
}
