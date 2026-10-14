using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Papéis de modelo por usuário (SPEC-20261013-model-roles RF-001/RF-002):
/// <c>modelRoles</c> dentro de <c>User.SettingsJson</c> mapeia papel
/// (<c>tasks</c>, <c>memory</c>, <c>advisor</c>) → cadeia de ids de modelo
/// em ordem de fallback. Camada global do admin (kv <c>model.roles</c>,
/// SPEC-20261014-config-layers RF-004) entra depois da do usuário: a
/// resolução tenta a cadeia do usuário primeiro, depois a global, e por
/// fim cai no modelo do chat (comportamento original).
/// </summary>
public sealed class ModelRoleResolver(
    AppDbContext db,
    ConfigService config,
    Func<CancellationToken, Task<List<Application.Contracts.ModelInfo>>> listModels,
    ILogger<ModelRoleResolver> logger)
{
    /// <summary>Chave kv das cadeias globais (admin) por papel.</summary>
    public const string GlobalRolesKey = "model.roles";
    private static readonly HashSet<string> s_knownRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "tasks", "memory", "advisor",
    };

    /// <summary>Se <paramref name="role"/> é um papel reconhecido (desconhecido é rejeitado no PUT).</summary>
    public static bool IsKnownRole(string role) => s_knownRoles.Contains(role);

    /// <summary>Resolve o modelo efetivo de um papel para o usuário.</summary>
    /// <param name="role">Papel (<c>tasks</c>, <c>memory</c>, <c>advisor</c>).</param>
    /// <param name="userId">Dono da configuração.</param>
    /// <param name="fallbackModel">Modelo usado quando o papel está vazio ou nada resolve.</param>
    /// <param name="ct">Token de cancelamento.</param>
    /// <returns>Primeiro id da cadeia presente no catálogo de providers; senão o fallback.</returns>
    public async Task<string> ResolveAsync(
        string role, string userId, string fallbackModel, CancellationToken ct)
    {
        var roles = await GetRolesAsync(userId, ct);
        roles.TryGetValue(role, out var userChain);
        var globalRoles = await GetGlobalRolesAsync(ct);
        globalRoles.TryGetValue(role, out var globalChain);

        // RF-004: cadeia do usuário primeiro, depois a global do admin —
        // ids já resolvidos não reavaliam; concatena sem duplicar.
        var chain = (userChain ?? [])
            .Concat(globalChain ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (chain.Count == 0)
        {
            return fallbackModel;
        }

        List<Application.Contracts.ModelInfo> catalog;
        try
        {
            catalog = await listModels(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Falha ao listar modelos para resolver papel {Role}.", role);
            return fallbackModel;
        }
        var known = catalog.Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return chain.FirstOrDefault(known.Contains) ?? fallbackModel;
    }

    /// <summary>Lê <c>modelRoles</c> do <c>SettingsJson</c> do usuário.</summary>
    public async Task<Dictionary<string, List<string>>> GetRolesAsync(
        string userId, CancellationToken ct)
    {
        var json = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.SettingsJson)
            .FirstOrDefaultAsync(ct);
        return Parse(json);
    }

    /// <summary>Cadeias globais do admin (kv <c>model.roles</c>, RF-004).</summary>
    public async Task<Dictionary<string, List<string>>> GetGlobalRolesAsync(
        CancellationToken ct) =>
        Clean(await config.GetAsync<Dictionary<string, List<string>>?>(
            GlobalRolesKey, null, ct));

    /// <summary>
    /// Grava as cadeias globais (admin); cadeias vazias removem o papel e
    /// mapa vazio remove a chave — sem global, usuários voltam ao default.
    /// </summary>
    public async Task SetGlobalRolesAsync(
        Dictionary<string, List<string>> roles, CancellationToken ct)
    {
        var cleaned = Clean(roles);
        await config.SetAsync<Dictionary<string, List<string>>?>(
            GlobalRolesKey, cleaned.Count > 0 ? cleaned : null, ct);
    }

    /// <summary>Normaliza um mapa papel→cadeia (papel conhecido, ids limpos, sem vazios).</summary>
    private static Dictionary<string, List<string>> Clean(
        Dictionary<string, List<string>>? roles) =>
        (roles ?? [])
            .Where(kv => IsKnownRole(kv.Key))
            .ToDictionary(
                kv => kv.Key.ToLowerInvariant(),
                kv => (kv.Value ?? [])
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim()).Distinct().ToList(),
                StringComparer.OrdinalIgnoreCase)
            .Where(kv => kv.Value.Count > 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Grava <c>modelRoles</c> no <c>SettingsJson</c> preservando as demais
    /// chaves; cadeias vazias removem o papel; sem nenhum papel a chave some.
    /// </summary>
    public async Task SetRolesAsync(
        string userId, Dictionary<string, List<string>> roles, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([userId], ct)
            ?? throw new InvalidOperationException("Usuário não encontrado.");

        var doc = string.IsNullOrWhiteSpace(user.SettingsJson)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(user.SettingsJson) ?? [];

        var cleaned = Clean(roles);

        if (cleaned.Count == 0)
        {
            doc.Remove("modelRoles");
        }
        else
        {
            doc["modelRoles"] = JsonSerializer.SerializeToElement(cleaned);
        }
        user.SettingsJson = JsonSerializer.Serialize(doc);
        user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
    }

    private static Dictionary<string, List<string>> Parse(string? settingsJson)
    {
        var roles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return roles;
        }
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (!doc.RootElement.TryGetProperty("modelRoles", out var el)
                || el.ValueKind != JsonValueKind.Object)
            {
                return roles;
            }
            foreach (var prop in el.EnumerateObject())
            {
                if (!IsKnownRole(prop.Name))
                {
                    continue;
                }
                var chain = ReadChain(prop.Value);
                if (chain.Count > 0)
                {
                    roles[prop.Name.ToLowerInvariant()] = chain;
                }
            }
        }
        catch (JsonException)
        {
            // Settings corrompido: sem papéis — cai no modelo do chat.
        }
        return roles;
    }

    /// <summary>Lê uma chain de fallback: array de strings ou string única.</summary>
    private static List<string> ReadChain(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            return el.EnumerateArray()
                .Where(i => i.ValueKind == JsonValueKind.String)
                .Select(i => i.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }
        return el.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(el.GetString())
            ? [el.GetString()!]
            : [];
    }
}
