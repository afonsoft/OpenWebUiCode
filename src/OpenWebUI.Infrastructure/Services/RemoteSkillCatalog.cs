using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Catálogo HTTP de skills (SPEC-20261018-agent-polish RF-002): raízes
/// declaradas no kv <c>skills.remoteRoots</c> (admin) no formato
/// <c>https://host/base</c>. Para cada raiz busca
/// <c>GET {root}/index.json</c> — aceita <c>["name",…]</c> ou
/// <c>{"skills":[{"name","description"},…]}</c> — e depois
/// <c>GET {root}/{name}/SKILL.md</c> com <c>If-None-Match</c> do ETag
/// cacheado (304 reusa o corpo). TTL de 24h por raiz; falha de fetch
/// mantém o cache anterior (stale-while-error). As raízes são superfície
/// de config admin — mesmo modelo de confiança dos MCP servers (sem
/// SSRF guard). As entradas entram no <see cref="SkillDiscoveryService.Scan"/>
/// com <c>Scope="remote"</c> e precedência abaixo das locais.
/// </summary>
public sealed class RemoteSkillCatalog(
    IHttpClientFactory httpFactory, ILogger<RemoteSkillCatalog>? logger = null)
{
    /// <summary>Kv das raízes remotas (<c>List&lt;string&gt;</c> de URLs https://).</summary>
    public const string ConfigKey = "skills.remoteRoots";

    /// <summary>TTL do cache por raiz (24h).</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    /// <summary>Teto por SKILL.md remoto — mesmo do arquivo local.</summary>
    private const int MaxSkillBytes = 64 * 1024;

    /// <summary>Máximo de skills por raiz remota.</summary>
    private const int MaxPerRoot = 50;

    private sealed record CachedSkill(string? ETag, string Body);
    private sealed record RootState(
        DateTimeOffset RefreshedAt,
        ConcurrentDictionary<string, CachedSkill> Skills);
    private sealed record IndexItem(string Name, string? Description);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, RootState> _roots =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Seam de teste: injeta o relógio.</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Entradas remotas cacheadas (snapshot de leitura — alimenta o
    /// merge síncrono do <see cref="SkillDiscoveryService.Scan"/>).
    /// </summary>
    public IReadOnlyList<RepoEntry> Entries
    {
        get
        {
            var entries = new List<RepoEntry>();
            foreach (var (root, state) in _roots)
            {
                foreach (var (name, skill) in state.Skills)
                {
                    if (SkillDiscoveryService.ParseEntryContent(
                            skill.Body, name, $"{root.TrimEnd('/')}/{name}/SKILL.md", "remote")
                        is { } entry)
                    {
                        entries.Add(entry);
                    }
                }
            }
            return entries;
        }
    }

    /// <summary>
    /// Re-busca as raízes cujo cache passou do TTL (ou ainda não foram
    /// buscadas). Chamada pelos pontos de entrada async (endpoints,
    /// contributors) antes do <c>Scan</c> — nunca lança.
    /// </summary>
    /// <param name="config">ConfigService para ler o kv de raízes.</param>
    /// <param name="ct">Cancelamento.</param>
    /// <returns>Número de raízes efetivamente atualizadas.</returns>
    public async Task<int> RefreshIfStaleAsync(ConfigService config, CancellationToken ct)
    {
        List<string> roots;
        try
        {
            roots = await config.GetAsync<List<string>?>(ConfigKey, null, ct) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogDebug(ex, "skills.remoteRoots ilegível — catálogo remoto ignorado.");
            return 0;
        }

        var refreshed = 0;
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw)
                || !Uri.TryCreate(raw, UriKind.Absolute, out var root)
                || root.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            var key = root.ToString().TrimEnd('/');
            if (_roots.TryGetValue(key, out var state)
                && Now() - state.RefreshedAt < Ttl)
            {
                continue;
            }

            try
            {
                if (await FetchRootAsync(key, ct))
                {
                    refreshed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogDebug(ex, "Skill catalog remoto {Root} falhou no refresh.", key);
            }
        }
        return refreshed;
    }

    /// <summary>Seam de teste: estado interno por raiz (para asserts).</summary>
    internal bool HasRoot(string root) => _roots.ContainsKey(root.TrimEnd('/'));

    private async Task<bool> FetchRootAsync(string root, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(nameof(RemoteSkillCatalog));
        var index = await FetchIndexAsync(http, root, ct);
        if (index is null)
        {
            return false;
        }

        var state = _roots.GetOrAdd(root,
            _ => new RootState(DateTimeOffset.MinValue, new(StringComparer.OrdinalIgnoreCase)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in index.Take(MaxPerRoot))
        {
            seen.Add(item.Name);
            var cached = state.Skills.TryGetValue(item.Name, out var c) ? c : null;
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{root}/{Uri.EscapeDataString(item.Name)}/SKILL.md");
            if (cached?.ETag is { } etag)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                continue;
            }
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength > MaxSkillBytes)
            {
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length > MaxSkillBytes)
            {
                body = body[..MaxSkillBytes];
            }
            state.Skills[item.Name] = new CachedSkill(
                response.Headers.ETag?.Tag, body);
        }

        // Podar skills removidas do índice.
        foreach (var stale in state.Skills.Keys.Where(n => !seen.Contains(n)).ToList())
        {
            state.Skills.TryRemove(stale, out _);
        }
        _roots[root] = state with { RefreshedAt = Now() };
        return true;
    }

    /// <summary>
    /// GET {root}/index.json tolerante a formato: array de strings ou
    /// objeto <c>{"skills":[…]}</c> com items {name,description?}.
    /// </summary>
    private static async Task<List<IndexItem>?> FetchIndexAsync(
        HttpClient http, string root, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{root}/index.json", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var items = new List<IndexItem>();
        if (!TryGetItemsArray(doc.RootElement, out var array))
        {
            return items;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (ParseItem(item) is { } parsed)
            {
                items.Add(parsed);
            }
        }
        return items;
    }

    private static bool TryGetItemsArray(JsonElement root, out JsonElement array)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
            return true;
        }
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("skills", out var skills)
            && skills.ValueKind == JsonValueKind.Array)
        {
            array = skills;
            return true;
        }
        array = default;
        return false;
    }

    private static IndexItem? ParseItem(JsonElement item)
    {
        var name = item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("name", out var n)
                && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        var description = item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("description", out var d)
            && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
        return new IndexItem(name.Trim(), description);
    }
}
