using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Papéis de modelo por usuário (SPEC-20261013-model-roles RF-001/RF-002):
/// <c>modelRoles</c> dentro de <c>User.SettingsJson</c> mapeia papel
/// (<c>tasks</c>, <c>memory</c>, <c>advisor</c>) → cadeia de ids de modelo
/// em ordem de fallback. Resolver itera a cadeia até achar um id que o
/// provider conhece; nenhum → modelo do chat (comportamento atual).
/// </summary>
public sealed class ModelRoleResolver(
    AppDbContext db,
    Func<CancellationToken, Task<List<Application.Contracts.ModelInfo>>> listModels,
    ILogger<ModelRoleResolver> logger)
{
    /// <summary>Papéis reconhecidos (papel desconhecido no payload é rejeitado no PUT).</summary>
    public static readonly HashSet<string> KnownRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "tasks", "memory", "advisor",
    };

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
        if (!roles.TryGetValue(role, out var chain) || chain.Count == 0)
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
        foreach (var id in chain)
        {
            if (known.Contains(id))
            {
                return id;
            }
        }
        return fallbackModel;
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

        var cleaned = roles
            .Where(kv => KnownRoles.Contains(kv.Key))
            .ToDictionary(
                kv => kv.Key.ToLowerInvariant(),
                kv => (kv.Value ?? [])
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim()).Distinct().ToList());
        foreach (var empty in cleaned.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
        {
            cleaned.Remove(empty);
        }

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
                if (!KnownRoles.Contains(prop.Name))
                {
                    continue;
                }
                var chain = prop.Value.ValueKind == JsonValueKind.Array
                    ? prop.Value.EnumerateArray()
                        .Where(i => i.ValueKind == JsonValueKind.String)
                        .Select(i => i.GetString()!)
                        .Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                    : prop.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(prop.Value.GetString())
                        ? [prop.Value.GetString()!]
                        : [];
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
}
