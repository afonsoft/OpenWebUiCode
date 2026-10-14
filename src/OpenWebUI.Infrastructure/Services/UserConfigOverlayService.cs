using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Proveniência de um valor efetivo (SPEC-20261014-config-layers RF-001):
/// <c>user</c> (override do usuário), <c>global</c> (padrão do admin) ou
/// <c>default</c> (valor de código).
/// </summary>
public enum ConfigSource
{
    /// <summary>Valor definido pelo próprio usuário.</summary>
    User,
    /// <summary>Padrão definido pelo admin.</summary>
    Global,
    /// <summary>Default de código — nenhuma camada definiu.</summary>
    Default,
}

/// <summary>Valor efetivo com a camada que o forneceu.</summary>
public sealed record LayeredValue<T>(T? Value, ConfigSource Source);

/// <summary>
/// Resolvedor de configuração em camadas
/// (<c>u:{userId}:{key}</c> → kv global <c>{key}</c> → default de código) e
/// catálogo efetivo de servidores MCP por usuário (RF-002): globais
/// habilitados − desabilitados pelo usuário − sombreados por nome +
/// servidores do próprio usuário. Tombstone explícito
/// (<c>u:{id}:mcp-disabled</c>) separa "ausente" de "desligado".
/// </summary>
public sealed class UserConfigOverlayService(AppDbContext db, ConfigService config)
{
    private static string UserKey(string userId, string key) => $"u:{userId}:{key}";
    private static string McpDisabledKey(string userId) => $"u:{userId}:mcp-disabled";
    private static string SkillsDisabledKey(string userId) => $"u:{userId}:skills-disabled";

    /// <summary>Chave kv das chaves de provider do usuário (BYOK, RF-005).</summary>
    public static string ProviderKeysKey(string userId) => $"user:{userId}:provider-keys";

    /// <summary>Chave kv do flag BYOK global (admin, default off).</summary>
    public const string UserProviderKeysFlag = "features.userProviderKeys";

    /// <summary>Chave kv global das repo-skills desligadas pelo admin (deny absoluto).</summary>
    public const string GlobalSkillsDisabledKey = "skills-disabled";

    private static readonly HashSet<string> s_byokProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        ProviderTypes.OpenAi, ProviderTypes.Anthropic, ProviderTypes.Google,
    };

    /// <summary>Se <paramref name="provider"/> aceita chave de usuário (Ollama é sem chave).</summary>
    public static bool IsByokProvider(string provider) => s_byokProviders.Contains(provider);

    /// <summary>
    /// Resolve <paramref name="key"/> em camadas. Para coleções, a camada do
    /// usuário substitui a global inteira (replace, não merge) — overrides
    /// granulares por item ficam por conta de tombstones específicos.
    /// </summary>
    public async Task<LayeredValue<T>> GetAsync<T>(
        string userId, string key, T? fallback = default, CancellationToken ct = default)
    {
        var userValue = await config.GetAsync<T?>(UserKey(userId, key), default, ct);
        if (userValue is not null)
        {
            return new LayeredValue<T>(userValue, ConfigSource.User);
        }
        var globalValue = await config.GetAsync<T?>(key, default, ct);
        if (globalValue is not null)
        {
            return new LayeredValue<T>(globalValue, ConfigSource.Global);
        }
        return new LayeredValue<T>(fallback, ConfigSource.Default);
    }

    /// <summary>Grava o override do usuário (null remove → volta a herdar).</summary>
    public Task SetUserAsync<T>(string userId, string key, T? value, CancellationToken ct) =>
        config.SetAsync(UserKey(userId, key), value, ct);

    /// <summary>Ids de servidores MCP globais que o usuário desligou para si.</summary>
    public async Task<HashSet<string>> GetMcpDisabledAsync(
        string userId, CancellationToken ct) =>
        new(await config.GetAsync<List<string>>(McpDisabledKey(userId), [], ct),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Liga/desliga um servidor MCP global só para este usuário.</summary>
    public async Task SetMcpDisabledAsync(
        string userId, string serverId, bool disabled, CancellationToken ct)
    {
        var set = await GetMcpDisabledAsync(userId, ct);
        if (disabled)
        {
            set.Add(serverId);
        }
        else
        {
            set.Remove(serverId);
        }
        await config.SetAsync(McpDisabledKey(userId), set.ToList(), ct);
    }

    /// <summary>
    /// Catálogo efetivo de MCP do usuário (RF-002): servidores globais
    /// habilitados pelo admin − desligados pelo usuário − sombreados por um
    /// servidor próprio de mesmo nome (override por nome) + os servidores do
    /// usuário. Ordenado por nome.
    /// </summary>
    public async Task<List<(McpServer Server, ConfigSource Source, bool EnabledForMe)>>
        EffectiveMcpServersAsync(string userId, CancellationToken ct)
    {
        var all = await db.McpServers.AsNoTracking()
            .OrderBy(s => s.Name).ToListAsync(ct);
        var mine = all.Where(s => s.UserId == userId).ToList();
        var globals = all.Where(s => s.UserId == null).ToList();
        var disabled = await GetMcpDisabledAsync(userId, ct);
        var myNames = mine
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<(McpServer, ConfigSource, bool)>();
        foreach (var g in globals)
        {
            if (!g.Enabled)
            {
                // Desligado pelo admin some de todos — nem aparece no catálogo.
                continue;
            }
            if (myNames.Contains(g.Name))
            {
                // Servidor do usuário com o mesmo nome substitui o global.
                continue;
            }
            result.Add((g, ConfigSource.Global, EnabledForMe: !disabled.Contains(g.Id)));
        }
        result.AddRange(mine.Select(s => (s, ConfigSource.User, s.Enabled)));
        return result;
    }

    /// <summary>
    /// Ids de servidores cujas tools NÃO devem chegar ao usuário: globais
    /// desligados por ele + globais sombreados por nome + servidores de
    /// outros usuários + globais desligados pelo admin (paranoia — Enabled
    /// já filtra na query do picker).
    /// </summary>
    public async Task<HashSet<string>> HiddenMcpServerIdsAsync(
        string userId, CancellationToken ct)
    {
        var all = await db.McpServers.AsNoTracking()
            .Select(s => new { s.Id, s.Name, s.UserId, s.Enabled })
            .ToListAsync(ct);
        var myNames = all.Where(s => s.UserId == userId)
            .Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disabled = await GetMcpDisabledAsync(userId, ct);
        return all.Where(s =>
                s.UserId is not null && s.UserId != userId          // de outro usuário
                || (s.UserId is null && (!s.Enabled                 // admin desligou
                    || disabled.Contains(s.Id)                       // eu desliguei
                    || myNames.Contains(s.Name))))                   // sombreado por nome
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ---------------- BYOK (RF-005) ----------------

    /// <summary>
    /// Chaves de provider do usuário quando o admin habilitou
    /// <see cref="UserProviderKeysFlag"/>; vazio/null quando desligado —
    /// o mapa nunca sai em resposta de API (só <c>hasKey</c>).
    /// </summary>
    public async Task<Dictionary<string, string>> UserProviderKeysAsync(
        string userId, CancellationToken ct)
    {
        if (!await config.GetAsync(UserProviderKeysFlag, false, ct))
        {
            return [];
        }
        return await config.GetAsync<Dictionary<string, string>?>(
            ProviderKeysKey(userId), null, ct) ?? [];
    }

    // ---------------- Repo-skills (tombstone por nome) ----------------

    /// <summary>
    /// Nomes de repo-skills invisíveis ao usuário: desligadas pelo admin
    /// (deny absoluto — usuário não religa) ∪ desligadas por ele.
    /// </summary>
    public async Task<HashSet<string>> HiddenSkillNamesAsync(
        string userId, CancellationToken ct)
    {
        var adminOff = await config.GetAsync<List<string>>(
            GlobalSkillsDisabledKey, [], ct);
        var mineOff = await config.GetAsync<List<string>>(
            SkillsDisabledKey(userId), [], ct);
        return adminOff.Concat(mineOff)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Conjunto só com as skills desligadas pelo admin (provenance na UI).</summary>
    public async Task<HashSet<string>> AdminDisabledSkillNamesAsync(CancellationToken ct) =>
        new(await config.GetAsync<List<string>>(GlobalSkillsDisabledKey, [], ct),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Liga/desliga uma repo-skill só para este usuário (admin-denied permanece off).</summary>
    public async Task SetSkillDisabledAsync(
        string userId, string skillName, bool disabled, CancellationToken ct)
    {
        var set = await config.GetAsync<List<string>>(
            SkillsDisabledKey(userId), [], ct);
        var names = set.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (disabled)
        {
            names.Add(skillName);
        }
        else
        {
            names.Remove(skillName);
        }
        await config.SetAsync(SkillsDisabledKey(userId), names.ToList(), ct);
    }
}
