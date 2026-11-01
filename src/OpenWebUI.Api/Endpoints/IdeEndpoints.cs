using System.Text.Json;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Feature flag da superfície /ide (SPEC-20261009-web-ide-surface, E16 S2):
/// <c>GET /api/v1/ide/config</c> responde se a UI deve expor a rota e os
/// pontos de entrada. Ligado por padrão; admin desliga por
/// <c>PUT /api/v1/ide/config</c> ou <c>Ide:Enabled</c>/<c>IDE_ENABLED</c>.
/// Espelha o padrão de <c>/api/v1/terminal/config</c>.
/// </summary>
public static class IdeEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia as rotas de configuração da IDE web.</summary>
    public static RouteGroupBuilder MapIdeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ide").RequireAuthorization();
        group.MapGet("/config", ConfigGetAsync);
        group.MapPut("/config", ConfigPutAsync);
        return group;
    }

    private static async Task<IResult> ConfigGetAsync(
        HttpContext http, ConfigService config, IConfiguration configuration,
        CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, ct);
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(new { enabled = await IsEnabledAsync(config, configuration, ct) });
    }

    private static async Task<IResult> ConfigPutAsync(
        HttpContext http, ConfigService config, CancellationToken ct)
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

        var request = await JsonSerializer.DeserializeAsync<IdeConfigRequest>(
            http.Request.Body, JsonOptions, ct);
        await config.SetAsync("ide.enabled", request?.Enabled == true, ct);
        return Results.Ok(new { enabled = request?.Enabled == true });
    }

    /// <summary>
    /// Flag: ligada por padrão (rollout aberto — S7 decide se vira opt-in).
    /// Precedência: <c>Ide:Enabled</c>/<c>IDE_ENABLED</c> → kv
    /// <c>ide.enabled</c> → default <c>true</c>.
    /// </summary>
    private static async Task<bool> IsEnabledAsync(
        ConfigService config, IConfiguration configuration, CancellationToken ct)
    {
        if (bool.TryParse(configuration["Ide:Enabled"], out var fromConfig)
            || bool.TryParse(Environment.GetEnvironmentVariable("IDE_ENABLED"),
                out fromConfig))
        {
            return fromConfig;
        }

        return await config.GetAsync<bool?>("ide.enabled", null, ct) ?? true;
    }

    private static Task<User?> CurrentUserAsync(HttpContext http, CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        return AuthEndpoints.FindUserAsync(http, db, ct);
    }

    private sealed record IdeConfigRequest(bool Enabled);
}
