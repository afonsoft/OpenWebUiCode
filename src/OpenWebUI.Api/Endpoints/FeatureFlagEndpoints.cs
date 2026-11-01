using System.Text.Json;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Helper compartilhado dos endpoints de feature flag (terminal/ide/preview):
/// GET lê o flag efetivo para qualquer usuário autenticado; PUT persiste o kv
/// somente para admin. Precedência do flag: variável de configuração
/// (<c>{Area}:Enabled</c>) → env var (<c>{AREA}_ENABLED</c>) → kv persistido →
/// default <c>true</c> (rollout aberto).
/// </summary>
internal static class FeatureFlagEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Descritor de um feature flag (kv + overrides de configuração).</summary>
    internal sealed record FeatureFlag(string ConfigKey, string ConfigVar, string EnvVar);

    internal static readonly FeatureFlag Terminal = new("terminal.enabled", "Terminal:Enabled", "TERMINAL_ENABLED");
    internal static readonly FeatureFlag Ide = new("ide.enabled", "Ide:Enabled", "IDE_ENABLED");
    internal static readonly FeatureFlag Preview = new("preview.enabled", "Preview:Enabled", "PREVIEW_ENABLED");

    /// <summary>GET do flag: usuário autenticado lê o enabled efetivo.</summary>
    internal static async Task<IResult> GetAsync(
        HttpContext http, ConfigService config, IConfiguration configuration,
        FeatureFlag flag, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, ct);
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(new { enabled = await IsEnabledAsync(config, configuration, flag, ct) });
    }

    /// <summary>PUT do flag: admin persiste o kv (default permanece true).</summary>
    internal static async Task<IResult> PutAsync(
        HttpContext http, ConfigService config, FeatureFlag flag, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }

        var request = await JsonSerializer.DeserializeAsync<FeatureFlagRequest>(
            http.Request.Body, JsonOptions, ct);
        await config.SetAsync(flag.ConfigKey, request?.Enabled == true, ct);
        return Results.Ok(new { enabled = request?.Enabled == true });
    }

    /// <summary>
    /// Resolve o flag efetivo: <c>{Area}:Enabled</c>/<c>{AREA}_ENABLED</c>
    /// (override booleano) → kv persistido → default <c>true</c>.
    /// </summary>
    internal static async Task<bool> IsEnabledAsync(
        ConfigService config, IConfiguration configuration,
        FeatureFlag flag, CancellationToken ct)
    {
        if (bool.TryParse(configuration[flag.ConfigVar], out var fromConfig)
            || bool.TryParse(Environment.GetEnvironmentVariable(flag.EnvVar),
                out fromConfig))
        {
            return fromConfig;
        }

        // bool? null = nunca configurado → default ligado.
        return await config.GetAsync<bool?>(flag.ConfigKey, null, ct) ?? true;
    }

    /// <summary>Usuário autenticado atual (null → 401 nos callers).</summary>
    internal static Task<User?> CurrentUserAsync(HttpContext http, CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        return AuthEndpoints.FindUserAsync(http, db, ct);
    }

    private sealed record FeatureFlagRequest(bool Enabled);
}
