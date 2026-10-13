using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Armazena e recupera configurações persistidas no banco (tabela chave-valor).</summary>
public class ConfigService(AppDbContext db, HybridCache cache)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // TTL curto: limita o stale em deployments multi-instância (a escrita
    // invalida/atualiza só o cache local da instância que escreveu).
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = CacheTtl,
        LocalCacheExpiration = CacheTtl,
    };

    /// <summary>Obtém uma configuração desserializada ou o valor padrão.</summary>
    /// <typeparam name="T">Tipo do valor.</typeparam>
    /// <param name="key">Chave da configuração.</param>
    /// <param name="defaultValue">Valor usado quando a chave não existe.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken ct = default)
    {
        // O slot é compartilhado entre T e T? (mesma cache key): uma leitura
        // nullable que cacheie null não pode vazar o null para a leitura
        // não-nullable seguinte — o defaultValue sempre vence.
        var result = await cache.GetOrCreateAsync(
            CacheKey<T>(key),
            async cancel => await GetFromDbAsync(key, defaultValue, cancel),
            CacheOptions,
            tags: ["config"],
            cancellationToken: ct);
        return result is null ? defaultValue : result;
    }

    private async Task<T> GetFromDbAsync<T>(string key, T defaultValue, CancellationToken ct)
    {
        var entry = await db.ConfigEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == key, ct);
        if (entry is null)
        {
            return defaultValue;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(entry.ValueJson, JsonOptions) ?? defaultValue;
        }
        catch (JsonException)
        {
            return defaultValue;
        }
    }

    // Nullable<T> e T compartilham o slot — um SetAsync("k", false) deve
    // invalidar o GetAsync<bool?>("k") (mesmo valor serializado no DB).
    private static string CacheKey<T>(string key) =>
        $"cfg:{key}:{(Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T)).FullName}";

    /// <summary>Grava uma configuração serializada em JSON.</summary>
    /// <typeparam name="T">Tipo do valor.</typeparam>
    /// <param name="key">Chave da configuração.</param>
    /// <param name="value">Valor a persistir.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var entry = await db.ConfigEntries.FirstOrDefaultAsync(e => e.Key == key, ct);
        if (entry is null)
        {
            db.ConfigEntries.Add(new ConfigEntry
            {
                Key = key,
                ValueJson = json,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }
        else
        {
            entry.ValueJson = json;
            entry.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        await db.SaveChangesAsync(ct);
        // Valor null não pode ser cacheado: o GetOrCreateAsync devolveria o
        // null "como hit" e engoliria o ?? defaultValue do factory (o
        // TryGetValue antigo caía no branch do banco com `hit is T` = false).
        if (value is null)
        {
            await cache.RemoveAsync(CacheKey<T>(key), ct);
        }
        else
        {
            await cache.SetAsync(CacheKey<T>(key), value, CacheOptions, cancellationToken: ct);
        }
    }

    /// <summary>
    /// Entradas kv cujas chaves começam com <paramref name="prefix"/> —
    /// leitura administrativa sem cache (usado pelo refcount de bindings
    /// repo↔chat do SPEC-15).
    /// </summary>
    public async Task<IReadOnlyList<KeyValuePair<string, T?>>> ListByPrefixAsync<T>(
        string prefix, CancellationToken ct = default)
    {
        var rows = await db.ConfigEntries.AsNoTracking()
            .Where(e => e.Key.StartsWith(prefix))
            .Select(e => new { e.Key, e.ValueJson })
            .ToListAsync(ct);
        return rows
            .Select(r =>
            {
                try
                {
                    return new KeyValuePair<string, T?>(
                        r.Key, JsonSerializer.Deserialize<T>(r.ValueJson, JsonOptions));
                }
                catch (JsonException)
                {
                    return new KeyValuePair<string, T?>(r.Key, default);
                }
            })
            .ToList();
    }

    /// <summary>Obtém a configuração de conexões com provedores de IA.</summary>
    /// <param name="ct">Token de cancelamento.</param>
    public Task<ConnectionsConfig> GetConnectionsAsync(CancellationToken ct = default) =>
        GetAsync("connections", ConnectionsConfig.Default, ct);

    /// <summary>
    /// Resolve uma conexão OpenAI cadastrada pela URL base (compara ignorando
    /// <c>/</c> final e maiúsculas) — usado pelos providers de áudio, imagem e
    /// vídeo que referenciam uma conexão em vez de duplicar URL+chave.
    /// </summary>
    /// <param name="baseUrl">URL base gravada no campo Provider da config.</param>
    /// <param name="ct">Token de cancelamento.</param>
    /// <returns>URL e chave da conexão, ou null quando não encontrada.</returns>
    public async Task<(string Url, string? Key)?> FindOpenAiConnectionAsync(
        string? baseUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var connections = await GetConnectionsAsync(ct);
        var index = connections.OpenAiBaseUrls.ToList().FindIndex(u =>
            string.Equals(u?.TrimEnd('/'), baseUrl.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase));
        return index < 0
            ? null
            : (connections.OpenAiBaseUrls[index],
                connections.OpenAiApiKeys.ElementAtOrDefault(index));
    }

    /// <summary>Obtém a configuração administrativa (flags de auth e features).</summary>
    /// <param name="ct">Token de cancelamento.</param>
    public Task<AdminConfig> GetAdminConfigAsync(CancellationToken ct = default) =>
        GetAsync("admin.config", AdminConfig.Default, ct);

    /// <summary>Obtém (ou gera e persiste) o segredo usado para assinar tokens JWT.</summary>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<string> GetOrCreateJwtSecretAsync(CancellationToken ct = default)
    {
        var secret = await GetAsync<string?>("webui.jwt.secret", null, ct);
        if (string.IsNullOrEmpty(secret))
        {
            secret = Convert.ToBase64String(RandomNumberGeneratorBytes(64));
            await SetAsync("webui.jwt.secret", secret, ct);
        }

        return secret;
    }

    private static byte[] RandomNumberGeneratorBytes(int length)
    {
        var bytes = new byte[length];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return bytes;
    }
}
