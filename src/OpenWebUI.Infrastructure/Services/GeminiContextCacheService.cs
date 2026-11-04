using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Cache explícito de contexto do Gemini (SPEC-20261103-gemini-context-caching):
/// CRUD de <c>cachedContents</c> na base da conexão, hash estável de
/// (baseUrl + modelo + system) para reúso com refresh de TTL e delete
/// best-effort dos caches criados por esta instância no dispose.
/// Registrado como singleton — o mapa vive a vida do processo.
/// </summary>
public sealed class GeminiContextCacheService(
    IHttpClientFactory httpClientFactory,
    ILogger<GeminiContextCacheService> logger) : IAsyncDisposable
{
    private sealed record Entry(
        string Name,
        string BaseUrl,
        string ApiKey,
        DateTimeOffset ExpiresAt);

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan RefreshGrace = TimeSpan.FromMinutes(5);
    private static readonly string TtlSeconds =
        $"{(int)DefaultTtl.TotalSeconds}s";

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>
    /// Resolve o <c>cachedContents/&lt;id&gt;</c> para (conexão, modelo,
    /// system): hit fresco → refresh de TTL e reúso; miss/expirado → cria.
    /// Qualquer falha → <c>null</c> (degrade: a run segue sem cache).
    /// </summary>
    /// <param name="connection">Conexão Google tipada (baseUrl + apiKey).</param>
    /// <param name="model">Modelo Gemini alvo do generateContent.</param>
    /// <param name="systemText">Texto de systemInstruction a cachear.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<string?> ResolveAsync(
        ProviderConnection connection,
        string model,
        string systemText,
        CancellationToken ct = default)
    {
        try
        {
            var key = Hash(connection.BaseUrl, model, systemText);
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt - RefreshGrace > DateTimeOffset.UtcNow)
                {
                    await RefreshTtlAsync(connection, entry.Name, ct);
                    return entry.Name;
                }
                _entries.TryRemove(key, out _);
            }
            return await CreateAsync(connection, model, key, systemText, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "gemini context cache indisponível — seguindo sem cache.");
            return null;
        }
    }

    private async Task<string?> CreateAsync(
        ProviderConnection connection,
        string model,
        string key,
        string systemText,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = $"models/{model}",
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(
                    new JsonObject { ["text"] = systemText }),
            },
            ["ttl"] = TtlSeconds,
        };
        var url = $"{TrimSlash(connection.BaseUrl)}/cachedContents";
        using var response = await SendAsync(
            connection, HttpMethod.Post, url, body, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await TruncatedBodyAsync(response, ct);
            logger.LogWarning(
                "cachedContents create {Status}: {Body}",
                (int)response.StatusCode, err);
            return null;
        }
        var json = JsonNode.Parse(
            await response.Content.ReadAsStringAsync(ct));
        var name = Str(Prop(json, "name"));
        if (name is null)
        {
            return null;
        }
        var expiresAt = DateTimeOffset.TryParse(
            Str(Prop(json, "expireTime")), out var exp)
            ? exp
            : DateTimeOffset.UtcNow.Add(DefaultTtl);
        _entries[key] = new Entry(
            name, connection.BaseUrl, connection.ApiKey ?? string.Empty,
            expiresAt);
        return name;
    }

    private async Task RefreshTtlAsync(
        ProviderConnection connection, string name, CancellationToken ct)
    {
        var url = $"{TrimSlash(connection.BaseUrl)}/{name}";
        try
        {
            using var response = await SendAsync(
                connection, HttpMethod.Patch, url,
                new JsonObject { ["ttl"] = TtlSeconds }, ct);
            foreach (var (key, e) in _entries)
            {
                if (e.Name == name)
                {
                    _entries[key] = e with
                    {
                        ExpiresAt = DateTimeOffset.UtcNow.Add(DefaultTtl),
                    };
                }
            }
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("cachedContents refresh {Status}",
                    (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "cachedContents refresh falhou — reusa mesmo assim.");
        }
    }

    /// <summary>Delete best-effort de todos os caches criados por esta
    /// instância (shutdown do singleton).</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var (_, e) in _entries)
        {
            try
            {
                var url = $"{TrimSlash(e.BaseUrl)}/{e.Name}";
                var conn = new ProviderConnection("google", e.BaseUrl, e.ApiKey);
                using var response = await SendAsync(
                    conn, HttpMethod.Delete, url, null,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "cachedContents delete best-effort falhou.");
            }
        }
        _entries.Clear();
    }

    private async Task<HttpResponseMessage> SendAsync(
        ProviderConnection connection, HttpMethod method, string url,
        JsonObject? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        if (!string.IsNullOrEmpty(connection.ApiKey))
        {
            request.Headers.Add("x-goog-api-key", connection.ApiKey);
        }
        return await httpClientFactory.CreateClient().SendAsync(request, ct);
    }

    private static string Hash(string baseUrl, string model, string systemText) =>
        Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes($"{baseUrl}|{model}|{systemText}")));

    private static string TrimSlash(string url) => url.TrimEnd('/');

    private static JsonNode? Prop(JsonNode? node, string name) =>
        node is JsonObject obj ? obj[name] : null;

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static async Task<string> TruncatedBodyAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Length > 512 ? body[..512] : body;
    }
}
