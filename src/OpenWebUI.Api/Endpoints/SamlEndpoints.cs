using System.Text.Json;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// SAML 2.0 SP-initiated (HTTP-POST binding): metadata do SP, redirect ao
/// IdP e ACS que valida a assertion e emite JWT — paridade com o upstream.
/// </summary>
public static class SamlEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapSamlEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/saml");

        group.MapGet("/metadata",
            (HttpContext http, SamlService saml, CancellationToken ct) =>
                MetadataAsync(http, saml, ct));
        group.MapGet("/login",
            (HttpContext http, SamlService saml, CancellationToken ct) =>
                LoginAsync(http, saml, ct));
        group.MapPost("/acs",
            (HttpContext http, SamlService saml, OAuthService oauth,
                AppDbContext db, JwtTokenService tokens, CancellationToken ct) =>
                AcsAsync(http, saml, oauth, db, tokens, ct));

        // Config admin
        var config = app.MapGroup("/api/v1/configs").RequireAuthorization();
        config.MapGet("/saml",
            (HttpContext http, SamlService saml, CancellationToken ct) =>
                GetConfigAsync(http, saml, ct));
        config.MapPost("/saml",
            (HttpContext http, SamlService saml, ConfigService cfg, CancellationToken ct) =>
                SetConfigAsync(http, saml, cfg, ct));
        config.MapGet("/scim",
            (HttpContext http, ScimService scim, ConfigService cfg, CancellationToken ct) =>
                GetScimConfigAsync(http, scim, cfg, ct));
        config.MapPost("/scim",
            (HttpContext http, ScimService scim, ConfigService cfg, CancellationToken ct) =>
                SetScimConfigAsync(http, scim, cfg, ct));
    }

    private static async Task<IResult> MetadataAsync(
        HttpContext http, SamlService saml, CancellationToken ct)
    {
        var settings = await saml.GetSettingsAsync(ct);
        if (!settings.Enabled)
        {
            return Results.NotFound(new { detail = "SAML desabilitado." });
        }

        var entityId = SpEntityId(http, settings);
        var xml = SamlService.BuildMetadata(settings, entityId, AcsUrl(http));
        return Results.Text(xml, "application/samlmetadata+xml");
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http, SamlService saml, CancellationToken ct)
    {
        var settings = await saml.GetSettingsAsync(ct);
        if (!settings.Enabled || string.IsNullOrEmpty(settings.IdpSsoUrl))
        {
            return Results.NotFound(new { detail = "SAML não configurado." });
        }

        return Results.Redirect(
            SamlService.BuildLoginRedirect(settings, SpEntityId(http, settings), AcsUrl(http)));
    }

    private static async Task<IResult> AcsAsync(
        HttpContext http, SamlService saml, OAuthService oauth,
        AppDbContext db, JwtTokenService tokens, CancellationToken ct)
    {
        var settings = await saml.GetSettingsAsync(ct);
        if (!settings.Enabled)
        {
            return Results.NotFound(new { detail = "SAML desabilitado." });
        }

        string? samlResponse = null;
        if (http.Request.HasFormContentType)
        {
            var form = await http.Request.ReadFormAsync(ct);
            samlResponse = form["SAMLResponse"];
        }

        if (string.IsNullOrEmpty(samlResponse))
        {
            return Results.BadRequest(new { detail = "SAMLResponse ausente." });
        }

        var assertion = SamlService.ValidateResponse(
            samlResponse, settings, AcsUrl(http));
        if (assertion is null)
        {
            return Results.Unauthorized();
        }

        var link = await oauth.LinkOrCreateAsync(
            "saml", assertion.NameId, assertion.Email, assertion.DisplayName, ct);
        var user = await db.Users.FindAsync([link.UserId], ct);
        if (user is null || user.Role == UserRoles.Pending)
        {
            return Results.Redirect("/?oauth_error=pending");
        }

        // Role mapping: atributo configurado pode promover a admin/user.
        if (assertion.Role is "admin" or "user" && user.Role != assertion.Role)
        {
            user.Role = assertion.Role;
            user.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync(ct);
        }

        var token = await tokens.CreateTokenAsync(user, ct);
        return Results.Redirect($"/?oauth_token={Uri.EscapeDataString(token.Token)}");
    }

    private static async Task<IResult> GetConfigAsync(
        HttpContext http, SamlService saml, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, ct))
        {
            return Results.Forbid();
        }

        var s = await saml.GetSettingsAsync(ct);
        return Results.Ok(new SamlConfigResponse(
            s.Enabled, s.IdpSsoUrl, s.IdpEntityId,
            !string.IsNullOrEmpty(s.IdpCert), s.SpEntityId, s.RoleAttribute));
    }

    private static async Task<IResult> SetConfigAsync(
        HttpContext http, SamlService saml, ConfigService cfg, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, ct))
        {
            return Results.Forbid();
        }

        var request = await JsonSerializer.DeserializeAsync<SamlConfigRequest>(
            http.Request.Body, JsonOptions, ct);
        if (request is null)
        {
            return Results.BadRequest(new { detail = "Config SAML inválida." });
        }

        await cfg.SetAsync("saml.enabled", request.Enabled, ct);
        if (request.IdpSsoUrl is not null)
        {
            await cfg.SetAsync("saml.idp_sso_url", request.IdpSsoUrl, ct);
        }
        if (request.IdpEntityId is not null)
        {
            await cfg.SetAsync("saml.idp_entity_id", request.IdpEntityId, ct);
        }
        if (request.IdpCert is not null and not "********")
        {
            await cfg.SetAsync("saml.idp_cert", request.IdpCert, ct);
        }
        if (request.SpEntityId is not null)
        {
            await cfg.SetAsync("saml.sp_entity_id", request.SpEntityId, ct);
        }
        if (request.RoleAttribute is not null)
        {
            await cfg.SetAsync("saml.role_attribute", request.RoleAttribute, ct);
        }

        return await GetConfigAsync(http, saml, ct);
    }

    private static async Task<IResult> GetScimConfigAsync(
        HttpContext http, ScimService scim, ConfigService cfg, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, ct))
        {
            return Results.Forbid();
        }

        var enabled = await scim.IsEnabledAsync(ct);
        var hasToken = !string.IsNullOrEmpty(await cfg.GetAsync<string?>("scim.token", null, ct));
        return Results.Ok(new ScimConfigResponse(enabled, hasToken));
    }

    private static async Task<IResult> SetScimConfigAsync(
        HttpContext http, ScimService scim, ConfigService cfg, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, ct))
        {
            return Results.Forbid();
        }

        var request = await JsonSerializer.DeserializeAsync<ScimConfigRequest>(
            http.Request.Body, JsonOptions, ct);
        if (request is null)
        {
            return Results.BadRequest(new { detail = "Config SCIM inválida." });
        }

        await cfg.SetAsync("scim.enabled", request.Enabled, ct);
        if (request.Token is not null and not "********")
        {
            await cfg.SetAsync("scim.token", request.Token, ct);
        }

        return await GetScimConfigAsync(http, scim, cfg, ct);
    }

    private static string AcsUrl(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}/saml/acs";

    private static string SpEntityId(HttpContext http, SamlSettings settings) =>
        string.IsNullOrEmpty(settings.SpEntityId)
            ? $"{http.Request.Scheme}://{http.Request.Host}/saml/metadata"
            : settings.SpEntityId;

    private static async Task<bool> IsAdminAsync(HttpContext http, CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        return user?.Role == UserRoles.Admin;
    }
}
