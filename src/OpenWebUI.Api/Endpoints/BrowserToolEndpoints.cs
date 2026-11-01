using System.Text.Json;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Browser;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Config do <c>builtin:browser_screenshot</c>
/// (SPEC-20261007-chat-agent-parity RF-017): flag off por padrão; admin
/// liga por <c>PUT /api/v1/browser/config</c> ou env
/// <c>BrowserTools__Enabled</c>. GET também expõe se há browser headless
/// resolvível no host — a UI de admin pode avisar antes de ligar.
/// </summary>
public static class BrowserToolEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia as rotas de config do browser headless.</summary>
    public static RouteGroupBuilder MapBrowserToolEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/browser").RequireAuthorization();

        group.MapGet("/config", (HttpContext http, ConfigService config,
            IConfiguration configuration, BrowserScreenshotService screenshots,
            BrowserSessionService sessions, TesseractOcr ocr,
            CancellationToken ct) =>
            ConfigGetAsync(http, config, configuration, screenshots, sessions, ocr, ct));
        group.MapPut("/config", (HttpContext http, ConfigService config,
            AppDbContext db, CancellationToken ct) =>
            ConfigPutAsync(http, config, ct));

        return group;
    }

    private static async Task<IResult> ConfigGetAsync(
        HttpContext http, ConfigService config, IConfiguration configuration,
        BrowserScreenshotService screenshots, BrowserSessionService sessions,
        TesseractOcr ocr, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, ct);
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(new
            {
                enabled = await IsEnabledAsync(config, configuration, ct),
                browserPath = screenshots.ResolvePath(),
                playwrightReady = sessions.IsBrowserReady(),
                ocrAvailable = ocr.IsAvailable,
            });
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

        var request = await JsonSerializer.DeserializeAsync<BrowserConfigRequest>(
            http.Request.Body, JsonOptions, ct);
        await config.SetAsync(BrowserScreenshotBuiltinTool.EnabledKey,
            request?.Enabled == true, ct);
        return Results.Ok(new { enabled = request?.Enabled == true });
    }

    /// <summary>Flag: <c>BrowserTools:Enabled</c>/env ou kv persistido.</summary>
    private static async Task<bool> IsEnabledAsync(
        ConfigService config, IConfiguration configuration, CancellationToken ct)
    {
        if (string.Equals(configuration["BrowserTools:Enabled"], "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return await config.GetAsync(BrowserScreenshotBuiltinTool.EnabledKey, false, ct);
    }

    private static Task<User?> CurrentUserAsync(HttpContext http, CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        return AuthEndpoints.FindUserAsync(http, db, ct);
    }

    private sealed record BrowserConfigRequest(bool Enabled);
}
