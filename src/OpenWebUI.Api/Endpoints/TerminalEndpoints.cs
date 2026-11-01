using System.Text.Json;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Terminal servers (Jupyter/pty) configurados pelo admin + proxy
/// autenticado HTTP/WS — o "Open Terminal" do upstream.
/// </summary>
public static class TerminalEndpoints
{
    private static readonly TimeSpan WsIdleTimeout = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapTerminalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/terminals").RequireAuthorization();

        group.MapGet("/", (HttpContext http, TerminalProxyService terminals, CancellationToken ct) =>
            ListAsync(terminals, ct));
        group.MapGet("/config", (HttpContext http, TerminalProxyService terminals, CancellationToken ct) =>
            ListConfigAsync(http, terminals, ct));
        group.MapPost("/config", (HttpContext http, TerminalProxyService terminals, CancellationToken ct) =>
            SaveConfigAsync(http, terminals, ct));
        group.MapDelete("/config/{id}", (HttpContext http, string id, TerminalProxyService terminals, CancellationToken ct) =>
            DeleteConfigAsync(http, id, terminals, ct));

        // Catch-all: proxy HTTP e túnel WS (Jupyter /api/*).
        group.Map("/{id}/{**path}", (HttpContext http, string id, string? path,
            TerminalProxyService terminals, CancellationToken ct) =>
            ProxyAsync(http, id, path, terminals, ct));

        return group;
    }

    private static async Task<IResult> ListAsync(
        TerminalProxyService terminals, CancellationToken ct)
    {
        var servers = await terminals.GetServersAsync(ct);
        return Results.Ok(servers.Select(s => new TerminalServerResponse(
            s.Id, s.Name, s.Url, s.AuthType, s.Type, !string.IsNullOrEmpty(s.Key))).ToList());
    }

    private static async Task<IResult> ListConfigAsync(
        HttpContext http, TerminalProxyService terminals, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, ct))
        {
            return Results.Forbid();
        }

        return await ListAsync(terminals, ct);
    }

    private static async Task<IResult> SaveConfigAsync(
        HttpContext http, TerminalProxyService terminals, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, ct))
        {
            return Results.Forbid();
        }

        var request = await JsonSerializer.DeserializeAsync<TerminalServerRequest>(
            http.Request.Body, JsonOptions, ct);
        var isLocal = string.Equals(request?.Type, "local", StringComparison.OrdinalIgnoreCase);
        if (request is null || string.IsNullOrWhiteSpace(request.Name)
            || (!isLocal && (string.IsNullOrWhiteSpace(request.Url)
                || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))))
        {
            return Results.BadRequest(new { detail = "name e url http(s) válida são obrigatórios (url é opcional no type local)." });
        }

        if (request.AuthType is not ("none" or "token" or "password"))
        {
            return Results.BadRequest(new { detail = "auth_type deve ser none, token ou password." });
        }

        if (request.Type is not ("jupyter" or "pty" or "local"))
        {
            return Results.BadRequest(new { detail = "type deve ser jupyter, pty ou local." });
        }

        var servers = await terminals.GetServersAsync(ct);
        var id = request.Name.Trim().ToLowerInvariant().Replace(' ', '-');
        var existing = servers.FirstOrDefault(s => s.Id == id);
        var key = request.Key is null or "********"
            ? existing?.Key ?? string.Empty
            : request.Key;

        var server = new TerminalServerConfig(
            id, request.Name.Trim(), request.Url?.Trim() ?? string.Empty, request.AuthType, key, request.Type);
        terminals.StopLocal(id); // respawna na próxima requisição com a config nova
        servers.RemoveAll(s => s.Id == id);
        servers.Add(server);
        await terminals.SaveServersAsync(servers, ct);

        return Results.Ok(new TerminalServerResponse(
            server.Id, server.Name, server.Url, server.AuthType, server.Type,
            !string.IsNullOrEmpty(server.Key)));
    }

    private static async Task<IResult> DeleteConfigAsync(
        HttpContext http, string id, TerminalProxyService terminals, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, ct))
        {
            return Results.Forbid();
        }

        var servers = await terminals.GetServersAsync(ct);
        if (servers.RemoveAll(s => s.Id == id) == 0)
        {
            return Results.NotFound(new { detail = "Servidor de terminal não encontrado." });
        }

        await terminals.SaveServersAsync(servers, ct);
        terminals.StopLocal(id);
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> ProxyAsync(
        HttpContext http, string id, string? path,
        TerminalProxyService terminals, CancellationToken ct)
    {
        var clean = TerminalProxyService.SanitizePath(path);
        if (clean is null)
        {
            return Results.NotFound(new { detail = "Path não permitido." });
        }

        TerminalServerConfig server;
        try
        {
            var resolved = await terminals.ResolveServerAsync(id, ct);
            if (resolved is null)
            {
                return Results.NotFound(new { detail = "Servidor de terminal não encontrado." });
            }
            server = resolved;
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 502);
        }

        if (http.WebSockets.IsWebSocketRequest)
        {
            var subProtocol = http.WebSockets.WebSocketRequestedProtocols.FirstOrDefault();
            using var socket = await http.WebSockets.AcceptWebSocketAsync(subProtocol);
            await TerminalProxyService.TunnelAsync(
                socket, server, clean, http.Request.QueryString.Value,
                subProtocol, WsIdleTimeout, ct);
            return Results.Empty;
        }

        byte[]? body = null;
        if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, ct);
            body = buffer.ToArray();
        }

        var result = await terminals.ForwardAsync(
            server, new HttpMethod(http.Request.Method), clean,
            http.Request.QueryString.Value, body, http.Request.ContentType, ct);
        if (result.Response is null)
        {
            return Results.Problem(result.Error, statusCode: result.StatusCode);
        }

        var response = result.Response;
        http.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers)
                     .Where(h => h.Key is not ("transfer-encoding" or "connection")))
        {
            http.Response.Headers[header.Key] = header.Value.ToArray();
        }

        await response.Content.CopyToAsync(http.Response.Body, ct);
        response.Dispose();
        return Results.Empty;
    }

    private static async Task<bool> IsAdminAsync(HttpContext http, CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        return user?.Role == UserRoles.Admin;
    }
}
