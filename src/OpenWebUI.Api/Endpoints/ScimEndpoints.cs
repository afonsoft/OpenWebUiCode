using System.Text.Json;
using System.Text.Json.Nodes;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Provisionamento SCIM 2.0 (`/scim/v2/`): Users CRUD + Groups básico +
/// ServiceProviderConfig. Autenticação por bearer token dedicado
/// (`scim.token`), nunca JWT de usuário.
/// </summary>
public static class ScimEndpoints
{
    private const string UsersByIdRoute = "/Users/{id}";
    private const string UserNotFoundMessage = "User não encontrado";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapScimEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/scim/v2");

        group.MapGet("/ServiceProviderConfig",
            (HttpContext http, ScimService scim, CancellationToken ct) =>
                ServiceProviderConfigAsync(http, scim, ct));
        group.MapGet("/Users",
            (HttpContext http, ScimService scim, CancellationToken ct) =>
                ListUsersAsync(http, scim, ct));
        group.MapGet(UsersByIdRoute,
            (HttpContext http, string id, ScimService scim, CancellationToken ct) =>
                GetUserAsync(http, id, scim, ct));
        group.MapPost("/Users",
            (HttpContext http, ScimService scim, CancellationToken ct) =>
                CreateUserAsync(http, scim, ct));
        group.MapPut(UsersByIdRoute,
            (HttpContext http, string id, ScimService scim, CancellationToken ct) =>
                ReplaceUserAsync(http, id, scim, ct));
        group.MapMethods(UsersByIdRoute, ["PATCH"],
            (HttpContext http, string id, ScimService scim, CancellationToken ct) =>
                PatchUserAsync(http, id, scim, ct));
        group.MapDelete(UsersByIdRoute,
            (HttpContext http, string id, ScimService scim, CancellationToken ct) =>
                DeleteUserAsync(http, id, scim, ct));
        group.MapGet("/Groups",
            (HttpContext http, ScimService scim, CancellationToken ct) =>
                ListGroupsAsync(http, scim, ct));
        group.MapPost("/Groups",
            (HttpContext http, ScimService scim, CancellationToken ct) =>
                CreateGroupAsync(http, scim, ct));

        return group;
    }

    private static async Task<IResult> ServiceProviderConfigAsync(
        HttpContext http, ScimService scim, CancellationToken ct)
    {
        if (!await AuthorizeAsync(http, scim, ct))
        {
            return UnauthorizedOrDisabled(http);
        }

        return ScimJson(new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig" },
            patch = new { supported = true },
            bulk = new { supported = false },
            filter = new { supported = true, maxResults = 100 },
            changePassword = new { supported = false },
            sort = new { supported = false },
            etag = new { supported = false },
            authenticationSchemes = new[]
            {
                new { type = "oauthbearertoken", name = "OAuth Bearer Token", primary = true },
            },
        });
    }

    private static async Task<IResult> ListUsersAsync(
        HttpContext http, ScimService scim, CancellationToken ct)
    {
        if (!await AuthorizeAsync(http, scim, ct))
        {
            return UnauthorizedOrDisabled(http);
        }

        var startIndex = int.TryParse(http.Request.Query["startIndex"], out var s) ? s : 1;
        var count = int.TryParse(http.Request.Query["count"], out var c) ? c : 100;
        var result = await scim.ListUsersAsync(
            http.Request.Query["filter"], startIndex, Math.Min(count, 200),
            BaseUrl(http), ct);
        return ScimJson(result);
    }

    private static async Task<IResult> GetUserAsync(
        HttpContext http, string id, ScimService scim, CancellationToken ct)
    {
        if (!await AuthorizeAsync(http, scim, ct))
        {
            return UnauthorizedOrDisabled(http);
        }

        var user = await scim.GetUserAsync(id, BaseUrl(http), ct);
        return user is null
            ? ScimJson(ScimError.Of(404, UserNotFoundMessage), 404)
            : ScimJson(user);
    }

    private static async Task<IResult> CreateUserAsync(
        HttpContext http, ScimService scim, CancellationToken ct)
    {
        if (!await AuthorizeAsync(http, scim, ct))
        {
            return UnauthorizedOrDisabled(http);
        }

        var input = await JsonSerializer.DeserializeAsync<ScimUser>(
            http.Request.Body, JsonOptions, ct);
        if (input is null || string.IsNullOrWhiteSpace(input.UserName))
        {
            return ScimJson(ScimError.Of(400, "userName é obrigatório", "invalidValue"), 400);
        }

        var user = await scim.CreateUserAsync(input, BaseUrl(http), ct);
        return ScimJson(user, 201);
    }

    private static async Task<IResult> ReplaceUserAsync(
        HttpContext http, string id, ScimService scim, CancellationToken ct)
    {
        if (!await AuthorizeAsync(http, scim, ct))
        {
            return UnauthorizedOrDisabled(http);
        }

        var input = await JsonSerializer.DeserializeAsync<ScimUser>(
            http.Request.Body, JsonOptions, ct);
        if (input is null)
        {
            return ScimJson(ScimError.Of(400, "Payload SCIM inválido", "invalidSyntax"), 400);
        }

        var user = await scim.ReplaceUserAsync(id, input, BaseUrl(http), ct);
        return user is null
            ? ScimJson(ScimError.Of(404, UserNotFoundMessage), 404)
            : ScimJson(user);
    }

    private static async Task<IResult> PatchUserAsync(
        HttpContext http, string id, ScimService scim, CancellationToken ct)
    {
        if (!await AuthorizeAsync(http, scim, ct))
        {
            return UnauthorizedOrDisabled(http);
        }

        var patch = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct);
        if (patch is null)
        {
            return ScimJson(ScimError.Of(400, "Payload SCIM inválido", "invalidSyntax"), 400);
        }

        var user = await scim.PatchUserAsync(id, patch, BaseUrl(http), ct);
        return user is null
            ? ScimJson(ScimError.Of(404, UserNotFoundMessage), 404)
            : ScimJson(user);
    }

    private static async Task<IResult> DeleteUserAsync(
        HttpContext http, string id, ScimService scim, CancellationToken ct)
    {
        if (!await AuthorizeAsync(http, scim, ct))
        {
            return UnauthorizedOrDisabled(http);
        }

        return await scim.DeleteUserAsync(id, ct)
            ? Results.NoContent()
            : ScimJson(ScimError.Of(404, UserNotFoundMessage), 404);
    }

    private static async Task<IResult> ListGroupsAsync(
        HttpContext http, ScimService scim, CancellationToken ct)
    {
        if (!await AuthorizeAsync(http, scim, ct))
        {
            return UnauthorizedOrDisabled(http);
        }

        var startIndex = int.TryParse(http.Request.Query["startIndex"], out var s) ? s : 1;
        var count = int.TryParse(http.Request.Query["count"], out var c) ? c : 100;
        var result = await scim.ListGroupsAsync(startIndex, Math.Min(count, 200), BaseUrl(http), ct);
        return ScimJson(result);
    }

    private static async Task<IResult> CreateGroupAsync(
        HttpContext http, ScimService scim, CancellationToken ct)
    {
        if (!await AuthorizeAsync(http, scim, ct))
        {
            return UnauthorizedOrDisabled(http);
        }

        var input = await JsonSerializer.DeserializeAsync<ScimGroup>(
            http.Request.Body, JsonOptions, ct);
        var group = input is null ? null : await scim.CreateGroupAsync(input, BaseUrl(http), ct);
        return group is null
            ? ScimJson(ScimError.Of(400, "displayName é obrigatório", "invalidValue"), 400)
            : ScimJson(group, 201);
    }

    /// <summary>SCIM ativo + bearer token dedicado válido.</summary>
    private static async Task<bool> AuthorizeAsync(
        HttpContext http, ScimService scim, CancellationToken ct)
    {
        if (!await scim.IsEnabledAsync(ct))
        {
            return false;
        }

        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim() : null;
        return await scim.ValidateTokenAsync(token, ct);
    }

    private static IResult UnauthorizedOrDisabled(HttpContext http) =>
        Results.Json(ScimError.Of(401, "SCIM desabilitado ou token inválido"),
            statusCode: 401, contentType: "application/scim+json");

    private static string BaseUrl(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}/scim/v2";

    private static IResult ScimJson(object payload, int status = 200) =>
        Results.Json(payload, JsonOptions, statusCode: status,
            contentType: "application/scim+json");
}
