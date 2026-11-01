using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints OAuth/OIDC, espelhando <c>/oauth/{provider}/*</c> do upstream.</summary>
public static class OAuthEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia as rotas de login/callback OAuth.</summary>
    public static RouteGroupBuilder MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/oauth");
        group.MapGet("/{provider}/login", LoginAsync).AllowAnonymous();
        group.MapGet("/{provider}/callback", CallbackAsync).AllowAnonymous();
        return group;
    }

    private static IResult LoginAsync(
        string provider, HttpContext http, AppDbContext db)
    {
        var config = OAuthProviderCatalog.Resolve(provider);
        if (config is null)
        {
            return Results.NotFound();
        }

        var state = CreateState(JwtSecret(db));
        var redirectUri = CallbackUri(http, config.Name);
        var query = new StringBuilder()
            .Append("client_id=").Append(Uri.EscapeDataString(config.ClientId))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri))
            .Append("&response_type=code")
            .Append("&scope=").Append(Uri.EscapeDataString(config.Scope))
            .Append("&state=").Append(Uri.EscapeDataString(state));
        if (config.ExtraAuthorizeParams is not null)
        {
            foreach (var (key, value) in config.ExtraAuthorizeParams)
            {
                query.Append('&').Append(Uri.EscapeDataString(key))
                    .Append('=').Append(Uri.EscapeDataString(value));
            }
        }

        return Results.Redirect($"{config.AuthorizeUrl}?{query}");
    }

    private static async Task<IResult> CallbackAsync(
        string provider, string? code, string? state,
        HttpContext http, [AsParameters] OAuthCallbackDeps deps,
        CancellationToken ct)
    {
        var providerConfig = OAuthProviderCatalog.Resolve(provider);
        if (providerConfig is null || string.IsNullOrEmpty(code))
        {
            return Results.NotFound();
        }

        if (!ValidateState(state, JwtSecret(deps.Db)))
        {
            return Results.BadRequest(new { detail = "State OAuth inválido." });
        }

        var redirectUri = CallbackUri(http, providerConfig.Name);
        var tokenResponse = await ExchangeCodeAsync(
            providerConfig, code, redirectUri, deps.HttpFactory, ct);
        if (tokenResponse is null)
        {
            return Results.BadRequest(new { detail = "Falha na autenticação com o provedor." });
        }

        var userInfo = await FetchUserInfoAsync(
            providerConfig, tokenResponse.Value, deps.HttpFactory, ct);
        if (userInfo is null)
        {
            return Results.BadRequest(new { detail = "Falha ao obter dados do usuário." });
        }

        var (subject, email, name) = userInfo.Value;
        if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(email))
        {
            return Results.BadRequest(new { detail = "Provedor não retornou e-mail verificado." });
        }

        var link = await deps.OAuth.LinkOrCreateAsync(
            providerConfig.Name, subject, email, name, ct);

        var user = await deps.Db.Users.FindAsync([link.UserId], ct);
        if (user is null || user.Role == UserRoles.Pending)
        {
            return Results.Redirect("/?oauth_error=pending");
        }

        var token = await deps.Tokens.CreateTokenAsync(user, ct);
        return Results.Redirect($"/?oauth_token={Uri.EscapeDataString(token.Token)}");
    }

    private static string CallbackUri(HttpContext http, string provider) =>
        $"{http.Request.Scheme}://{http.Request.Host}/oauth/{provider}/callback";

    private static string JwtSecret(AppDbContext db)
    {
        var entry = db.ConfigEntries.Find("webui.jwt.secret");
        return entry is null ? string.Empty
            : JsonSerializer.Deserialize<string>(entry.ValueJson) ?? string.Empty;
    }

    private static string CreateState(string secret)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var signature = Sign(nonce, secret);
        return $"{nonce}.{signature}";
    }

    private static bool ValidateState(string? state, string secret)
    {
        if (string.IsNullOrEmpty(state))
        {
            return false;
        }

        var parts = state.Split('.');
        return parts.Length == 2
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Sign(parts[0], secret)),
                Encoding.UTF8.GetBytes(parts[1]));
    }

    private static string Sign(string value, string secret) =>
        Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(value)));

    private static async Task<JsonElement?> ExchangeCodeAsync(
        OAuthProviderConfig config, string code, string redirectUri,
        IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var http = httpFactory.CreateClient();
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        });
        using var response = await http.PostAsync(config.TokenUrl, body, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.ValueKind == JsonValueKind.Object ? json : null;
    }

    private static async Task<(string Subject, string? Email, string? Name)?> FetchUserInfoAsync(
        OAuthProviderConfig config, JsonElement token, IHttpClientFactory httpFactory,
        CancellationToken ct)
    {
        if (!token.TryGetProperty("access_token", out var accessToken)
            || accessToken.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var http = httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, config.UserInfoUrl);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", accessToken.GetString());
        if (config.Name == "github")
        {
            request.Headers.UserAgent.ParseAdd("OpenWebUI");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
        }

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var subject = config.Name == "github"
            ? json.GetProperty("id").ToString()
            : GetString(json, "sub");
        var email = GetString(json, "email");
        var name = GetString(json, "name") ?? GetString(json, "login");

        // GitHub pode ocultar e-mail no perfil; consulta /user/emails como fallback.
        if (string.IsNullOrEmpty(email) && config.Name == "github")
        {
            email = await FetchGithubEmailAsync(http, accessToken.GetString()!, ct);
        }

        return subject is null ? null : (subject, email, name);
    }

    private static async Task<string?> FetchGithubEmailAsync(
        HttpClient http, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "https://api.github.com/user/emails");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd("OpenWebUI");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var emails = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        foreach (var entry in emails.EnumerateArray())
        {
            if (entry.TryGetProperty("primary", out var primary) && primary.GetBoolean()
                && entry.TryGetProperty("verified", out var verified) && verified.GetBoolean())
            {
                return entry.GetProperty("email").GetString();
            }
        }

        return null;
    }

    private static string? GetString(JsonElement json, string property) =>
        json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>Bundle DI do callback OAuth (S107).</summary>
internal sealed record OAuthCallbackDeps(
    AppDbContext Db, OAuthService OAuth,
    JwtTokenService Tokens, IHttpClientFactory HttpFactory);
