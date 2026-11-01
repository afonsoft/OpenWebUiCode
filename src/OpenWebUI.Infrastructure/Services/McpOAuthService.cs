using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// SPEC-20261018-mcp-oauth: Authorization Code + PKCE (S256) para MCP
/// servers HTTP remotos. Discovery via
/// <c>/.well-known/oauth-protected-resource</c> +
/// <c>/.well-known/oauth-authorization-server</c>; DCR como default com
/// clientId/secret fixos por config; tokens persistidos por (userId,
/// serverId) como secret do usuário (mesma proteção das API keys) e
/// refresh automático com mutex por server. Estado transitório do fluxo
/// (verifier/state) fica 10min no HybridCache — nunca no client.
/// </summary>
public sealed class McpOAuthService(
    UserSecretsService secrets,
    HybridCache cache,
    IHttpClientFactory httpFactory,
    ILogger<McpOAuthService> logger)
{
    private const string ClientIdKey = "client_id";
    private const string RefreshTokenKey = "refresh_token";
    /// <summary>Nome do HttpClient registrado no DI para chamadas OAuth.</summary>
    public const string HttpClientName = "mcp-oauth";

    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>ServerId → sufixo válido do padrão de nomes de secret.</summary>
    private static string Sanitize(string serverId)
    {
        var sb = new StringBuilder(serverId.Length);
        foreach (var c in serverId)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
        var s = sb.ToString().ToUpperInvariant();
        return s.Length > 40 ? s[..40] : s;
    }

    private static string TokenKey(string serverId) => $"MCP_OAUTH_{Sanitize(serverId)}";
    private static string ClientKey(string serverId) => $"MCP_OAUTH_CLIENT_{Sanitize(serverId)}";
    private static string RequiredKey(string serverId) => $"MCP_OAUTH_REQUIRED_{Sanitize(serverId)}";
    private static string StateKey(string state) => $"mcp-oauth-state:{state}";

    /// <summary>Estado de auth do server para a UI: none|required|authorized.</summary>
    public async Task<string> GetAuthStateAsync(
        string userId, string serverId, CancellationToken ct)
    {
        if (await secrets.GetUserSecretsAsync(userId, ct) is { } map)
        {
            // Marker de re-auth vence token velho (refresh falhou).
            if (map.ContainsKey(RequiredKey(serverId)))
            {
                return "required";
            }
            if (map.ContainsKey(TokenKey(serverId)))
            {
                return "authorized";
            }
        }
        return "none";
    }

    /// <summary>
    /// Monta a authorize URL (RF-001/RF-002): discovery → metadata do AS →
    /// client (config fixo ou DCR) → PKCE. Devolve a URL ou null quando o
    /// server não anuncia OAuth. O estado fica no cache 10min.
    /// </summary>
    public async Task<string?> BeginAuthorizeAsync(
        string userId, McpServer server, string callbackUrl, CancellationToken ct)
    {
        if (server.Url is not { Length: > 0 } url
            || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
        {
            return null;
        }

        var http = httpFactory.CreateClient(HttpClientName);
        var asBase = await DiscoverAuthorizationServerAsync(http, endpoint, ct);
        if (asBase is null
            || await FetchJsonAsync(http,
                $"{asBase}/.well-known/oauth-authorization-server", ct) is not { } meta
            || meta.TryGetProperty("authorization_endpoint", out var authEl) is false
            || authEl.GetString() is not { Length: > 0 } authorizeEndpoint
            || meta.TryGetProperty("token_endpoint", out var tokEl) is false
            || tokEl.GetString() is not { Length: > 0 } tokenEndpoint)
        {
            return null;
        }

        var (clientId, clientSecret) = await ResolveClientAsync(
            http, userId, server, meta, callbackUrl, ct);
        if (clientId is null)
        {
            return null;
        }

        var verifier = B64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(24));
        await cache.SetAsync(StateKey(state), new PendingAuth(
            userId, server.Id, verifier, tokenEndpoint, clientId, clientSecret, callbackUrl),
            new HybridCacheEntryOptions
            {
                Expiration = StateTtl, LocalCacheExpiration = StateTtl,
            }, cancellationToken: ct);

        var scopes = meta.TryGetProperty("scopes_supported", out var sc)
            && sc.ValueKind == JsonValueKind.Array
                ? string.Join(' ', sc.EnumerateArray()
                    .Select(s => s.GetString()).Where(s => s is not null))
                : null;
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            [ClientIdKey] = clientId,
            ["redirect_uri"] = callbackUrl,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["scope"] = scopes,
            ["resource"] = endpoint.GetLeftPart(UriPartial.Authority),
        };
        return authorizeEndpoint + "?" + string.Join('&',
            query.Where(kv => kv.Value is { Length: > 0 })
                .Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!)}"));
    }

    /// <summary>
    /// Callback (RF-003): valida o state, troca code→tokens com o
    /// code_verifier e persiste criptografado. Devolve (userId, serverId)
    /// ou null quando o state expirou/é inválido.
    /// </summary>
    public async Task<(string UserId, string ServerId)?> CompleteAsync(
        string state, string code, CancellationToken ct)
    {
        var pending = await cache.GetOrCreateAsync<PendingAuth?>(
            StateKey(state), _ => ValueTask.FromResult<PendingAuth?>(null),
            cancellationToken: ct);
        if (pending is null)
        {
            return null;
        }
        await cache.RemoveAsync(StateKey(state), ct);

        var http = httpFactory.CreateClient(HttpClientName);
        using var resp = await http.PostAsync(pending.TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = pending.CallbackUrl,
                [ClientIdKey] = pending.ClientId,
                ["code_verifier"] = pending.Verifier,
            }), ct);
        var token = resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement?>(JsonOptions, ct)
            : null;
        if (token is not { } tok
            || !tok.TryGetProperty("access_token", out var at)
            || at.GetString() is not { Length: > 0 } access)
        {
            logger.LogWarning("mcp-oauth: troca code→token falhou ({Status})",
                resp.StatusCode);
            return null;
        }

        var seed = new StoredToken(access, null, pending.TokenEndpoint,
            pending.ClientId, pending.ClientSecret, DateTimeOffset.MinValue);
        await StoreTokenAsync(pending.UserId, pending.ServerId, tok, access, ct, seed);
        return (pending.UserId, pending.ServerId);
    }

    /// <summary>
    /// Access token válido para (userId, serverId): devolve o atual,
    /// refresh quando expira em &lt;30s (mutex por server) — ou null
    /// quando ausente/irrecuperável (marca <c>required</c>).
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(
        string userId, string serverId, CancellationToken ct)
    {
        var stored = await LoadTokenAsync(userId, serverId, ct);
        if (stored is null)
        {
            return null;
        }
        if (stored.ExpiresAt > DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30)
            || stored.RefreshToken is not { Length: > 0 })
        {
            return stored.AccessToken;
        }

        var mutex = RefreshLocks.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        await mutex.WaitAsync(ct);
        try
        {
            // Re-lê dentro do mutex — outro request pode ter renovado.
            stored = await LoadTokenAsync(userId, serverId, ct);
            if (stored is not null
                && stored.ExpiresAt <= DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30)
                && stored.RefreshToken is { Length: > 0 })
            {
                stored = await RefreshAsync(userId, serverId, stored, ct);
            }
            return stored?.AccessToken;
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>Marca o server como exigindo re-auth (401 mid-session).</summary>
    public async Task MarkRequiredAsync(
        string userId, string serverId, CancellationToken ct) =>
        await secrets.SetUserAsync(userId, RequiredKey(serverId), "1", ct);

    /// <summary>Desconecta: apaga token, marker e client registration.</summary>
    public async Task DisconnectAsync(
        string userId, string serverId, CancellationToken ct)
    {
        await secrets.DeleteUserAsync(userId, TokenKey(serverId), ct);
        await secrets.DeleteUserAsync(userId, RequiredKey(serverId), ct);
        await secrets.DeleteUserAsync(userId, ClientKey(serverId), ct);
    }

    /// <summary>Verdadeiro quando o erro indica 401 do endpoint remoto.</summary>
    public static bool IsAuthError(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }
        || ex.Message.Contains("401", StringComparison.Ordinal);

    // ---- internos ----

    private async Task<StoredToken?> LoadTokenAsync(
        string userId, string serverId, CancellationToken ct)
    {
        var map = await secrets.GetUserSecretsAsync(userId, ct);
        if (!map.TryGetValue(TokenKey(serverId), out var s))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<StoredToken>(s.Value, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<StoredToken?> RefreshAsync(
        string userId, string serverId, StoredToken stored, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = RefreshTokenKey,
            [RefreshTokenKey] = stored.RefreshToken!,
            [ClientIdKey] = stored.ClientId,
        };
        if (stored.ClientSecret is { Length: > 0 })
        {
            fields["client_secret"] = stored.ClientSecret;
        }
        try
        {
            using var resp = await http.PostAsync(stored.TokenEndpoint,
                new FormUrlEncodedContent(fields), ct);
            var token = resp.IsSuccessStatusCode
                ? await resp.Content.ReadFromJsonAsync<JsonElement?>(JsonOptions, ct)
                : null;
            if (token is not { } tok
                || !tok.TryGetProperty("access_token", out var at)
                || at.GetString() is not { Length: > 0 } access)
            {
                await MarkRequiredAsync(userId, serverId, ct);
                return null;
            }
            return await StoreTokenAsync(
                userId, serverId, tok, access, ct, stored);
        }
        catch (HttpRequestException)
        {
            await MarkRequiredAsync(userId, serverId, ct);
            return null;
        }
    }

    private async Task<StoredToken> StoreTokenAsync(
        string userId, string serverId, JsonElement token, string access,
        CancellationToken ct, StoredToken? previous = null)
    {
        var expiresIn = token.TryGetProperty("expires_in", out var ex)
            && ex.TryGetInt32(out var secs) ? secs : 3600;
        var refresh = token.TryGetProperty(RefreshTokenKey, out var rt)
            ? rt.GetString() : previous?.RefreshToken;
        var stored = new StoredToken(
            access, refresh,
            previous?.TokenEndpoint ?? string.Empty,
            previous?.ClientId ?? string.Empty, previous?.ClientSecret,
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(expiresIn));
        await secrets.SetUserAsync(userId, TokenKey(serverId),
            JsonSerializer.Serialize(stored, JsonOptions), ct);
        await secrets.DeleteUserAsync(userId, RequiredKey(serverId), ct);
        return stored;
    }

    /// <summary>
    /// Client registration (RF-002): secret salvo → config fixa → DCR
    /// no <c>registration_endpoint</c> do metadata.
    /// </summary>
    private async Task<(string? ClientId, string? ClientSecret)> ResolveClientAsync(
        HttpClient http, string userId, McpServer server, JsonElement meta,
        string callbackUrl, CancellationToken ct)
    {
        var map = await secrets.GetUserSecretsAsync(userId, ct);
        if (map.TryGetValue(ClientKey(server.Id), out var existing))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<ClientReg>(
                    existing.Value, JsonOptions);
                return (saved?.ClientId, saved?.ClientSecret);
            }
            catch (JsonException) { /* cai no re-registro */ }
        }

        if (!meta.TryGetProperty("registration_endpoint", out var regEl)
            || regEl.GetString() is not { Length: > 0 } registerEndpoint)
        {
            return (null, null);
        }
        using var resp = await http.PostAsJsonAsync(registerEndpoint, new
        {
            client_name = "open-webui",
            redirect_uris = new[] { callbackUrl },
            grant_types = new[] { "authorization_code", RefreshTokenKey },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        }, JsonOptions, ct);
        var regBody = resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<JsonElement?>(JsonOptions, ct)
            : null;
        if (regBody is not { } reg
            || !reg.TryGetProperty(ClientIdKey, out var cid)
            || cid.GetString() is not { Length: > 0 } clientId)
        {
            return (null, null);
        }

        var clientSecret = reg.TryGetProperty("client_secret", out var cs)
            ? cs.GetString() : null;
        await secrets.SetUserAsync(userId, ClientKey(server.Id),
            JsonSerializer.Serialize(new ClientReg(clientId, clientSecret), JsonOptions), ct);
        return (clientId, clientSecret);
    }

    /// <summary>
    /// Discovery (RF-001): protected-resource metadata anuncia os
    /// authorization_servers; fallback = origem do endpoint.
    /// </summary>
    private static async Task<string?> DiscoverAuthorizationServerAsync(
        HttpClient http, Uri endpoint, CancellationToken ct)
    {
        var origin = endpoint.GetLeftPart(UriPartial.Authority);
        if (await FetchJsonAsync(http,
                $"{origin}/.well-known/oauth-protected-resource", ct) is { } pr
            && pr.TryGetProperty("authorization_servers", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in list.EnumerateArray())
            {
                if (el.GetString() is { Length: > 0 } server)
                {
                    return server.TrimEnd('/');
                }
            }
        }
        return origin;
    }

    private static async Task<JsonElement?> FetchJsonAsync(
        HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }
            var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(ct);
            if (doc is null)
            {
                return null;
            }
            var clone = doc.RootElement.Clone();
            doc.Dispose();
            return clone;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');

    private sealed record PendingAuth(
        string UserId, string ServerId, string Verifier,
        string TokenEndpoint, string ClientId, string? ClientSecret,
        string CallbackUrl);

    private sealed record ClientReg(
        [property: JsonPropertyName("clientId")] string ClientId,
        [property: JsonPropertyName("clientSecret")] string? ClientSecret);

    private sealed record StoredToken(
        [property: JsonPropertyName("accessToken")] string AccessToken,
        [property: JsonPropertyName("refreshToken")] string? RefreshToken,
        [property: JsonPropertyName("tokenEndpoint")] string TokenEndpoint,
        [property: JsonPropertyName("clientId")] string ClientId,
        [property: JsonPropertyName("clientSecret")] string? ClientSecret,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);
}
