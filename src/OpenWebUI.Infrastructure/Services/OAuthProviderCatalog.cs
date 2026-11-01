
namespace OpenWebUI.Infrastructure.Services;

/// <summary>Configuração resolvida de um provedor OAuth/OIDC.</summary>
/// <param name="Name">Slug do provedor.</param>
/// <param name="ClientId">Client id registrado no provedor.</param>
/// <param name="ClientSecret">Client secret.</param>
/// <param name="AuthorizeUrl">Endpoint de autorização.</param>
/// <param name="TokenUrl">Endpoint de troca de código por token.</param>
/// <param name="UserInfoUrl">Endpoint de dados do usuário.</param>
/// <param name="Scope">Escopo solicitado.</param>
/// <param name="ExtraAuthorizeParams">Parâmetros extras do authorize (ex.: prompt).</param>
public sealed record OAuthProviderConfig(
    string Name,
    string ClientId,
    string ClientSecret,
    string AuthorizeUrl,
    string TokenUrl,
    string UserInfoUrl,
    string Scope,
    IReadOnlyDictionary<string, string>? ExtraAuthorizeParams = null);

/// <summary>
/// Catálogo de providers OAuth configurados por variáveis de ambiente, nos
/// mesmos nomes do upstream: <c>GOOGLE_CLIENT_ID/SECRET</c>,
/// <c>GITHUB_CLIENT_ID/SECRET</c>, <c>MICROSOFT_CLIENT_ID/SECRET(/TENANT)</c> e
/// OIDC genérico via <c>OPENID_PROVIDER_URL/CLIENT_ID/SECRET</c>.
/// </summary>
public static class OAuthProviderCatalog
{
    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private sealed record ProviderSpec(
        string Name, string ClientIdEnv, string ClientSecretEnv,
        Func<string> AuthorizeUrl, Func<string> TokenUrl, Func<string> UserInfoUrl,
        string Scope, IReadOnlyDictionary<string, string>? Extra = null);

    private static OAuthProviderConfig? Build(ProviderSpec s)
    {
        var name = s.Name; var clientIdEnv = s.ClientIdEnv;
        var clientSecretEnv = s.ClientSecretEnv;
        var authorizeUrl = s.AuthorizeUrl; var tokenUrl = s.TokenUrl;
        var userInfoUrl = s.UserInfoUrl; var scope = s.Scope;
        var extra = s.Extra;
        var clientId = Env(clientIdEnv);
        var clientSecret = Env(clientSecretEnv);
        if (clientId is null || clientSecret is null)
        {
            return null;
        }

        return new OAuthProviderConfig(
            name, clientId, clientSecret,
            authorizeUrl(), tokenUrl(), userInfoUrl(), scope, extra);
    }

    /// <summary>Resolve um provedor pelo slug; <c>null</c> quando não configurado.</summary>
    public static OAuthProviderConfig? Resolve(string provider)
    {
        switch (provider.Trim().ToLowerInvariant())
        {
            case "google":
                return Build(new ProviderSpec(
                    "google", "GOOGLE_CLIENT_ID", "GOOGLE_CLIENT_SECRET",
                    () => "https://accounts.google.com/o/oauth2/v2/auth",
                    () => "https://oauth2.googleapis.com/token",
                    () => "https://openidconnect.googleapis.com/v1/userinfo",
                    "openid email profile"));
            case "github":
                return Build(new ProviderSpec(
                    "github", "GITHUB_CLIENT_ID", "GITHUB_CLIENT_SECRET",
                    () => "https://github.com/login/oauth/authorize",
                    () => "https://github.com/login/oauth/access_token",
                    () => "https://api.github.com/user",
                    "read:user user:email"));
            case "microsoft":
            {
                var tenant = Env("MICROSOFT_CLIENT_TENANT_ID") ?? "common";
                return Build(new ProviderSpec(
                    "microsoft", "MICROSOFT_CLIENT_ID", "MICROSOFT_CLIENT_SECRET",
                    () => $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize",
                    () => $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token",
                    () => "https://graph.microsoft.com/oidc/userinfo",
                    "openid email profile"));
            }
            case "oidc":
            {
                var authority = Env("OPENID_PROVIDER_URL")?.TrimEnd('/');
                if (authority is null)
                {
                    return null;
                }

                return Build(new ProviderSpec(
                    "oidc", "OPENID_CLIENT_ID", "OPENID_CLIENT_SECRET",
                    () => $"{authority}/authorize",
                    () => $"{authority}/oauth/token",
                    () => $"{authority}/userinfo",
                    "openid email profile"));
            }
            default:
                return null;
        }
    }

    /// <summary>Slugs dos providers atualmente configurados.</summary>
    public static IReadOnlyList<string> ConfiguredProviders() =>
        new[] { "google", "github", "microsoft", "oidc" }
            .Where(p => Resolve(p) is not null)
            .ToList();
}
