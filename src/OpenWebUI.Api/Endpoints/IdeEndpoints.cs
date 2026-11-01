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

    private static Task<IResult> ConfigGetAsync(
        HttpContext http, ConfigService config, IConfiguration configuration,
        CancellationToken ct) =>
        FeatureFlagEndpoints.GetAsync(
            http, config, configuration, FeatureFlagEndpoints.Ide, ct);

    private static Task<IResult> ConfigPutAsync(
        HttpContext http, ConfigService config, CancellationToken ct) =>
        FeatureFlagEndpoints.PutAsync(http, config, FeatureFlagEndpoints.Ide, ct);

    /// <summary>
    /// Flag: ligada por padrão (rollout aberto — S7 decide se vira opt-in).
    /// Precedência: <c>Ide:Enabled</c>/<c>IDE_ENABLED</c> → kv
    /// <c>ide.enabled</c> → default <c>true</c>.
    /// </summary>
}
