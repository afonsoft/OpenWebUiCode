using Microsoft.EntityFrameworkCore;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Fluxo OAuth de servidores MCP do usuário
/// (SPEC-20261018-mcp-oauth RF-001..RF-004):
/// <c>GET .../oauth/authorize</c> devolve a authorize URL para o client
/// abrir em popup; <c>GET /api/mcp/oauth/callback</c> (anônimo — o state
/// prova a posse) conclui a troca code→token; <c>POST .../disconnect</c>
/// revoga e apaga. O status alimenta o badge/botão da UI.
/// </summary>
public static class McpOAuthEndpoints
{
    /// <summary>Mapeia rotas OAuth de MCP (callback anônimo + ações do usuário).</summary>
    public static void MapMcpOAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/users/user/mcp-servers").RequireAuthorization();
        group.MapGet("/{id}/oauth/authorize", AuthorizeAsync);
        group.MapGet("/{id}/oauth/status", StatusAsync);
        group.MapPost("/{id}/oauth/disconnect", DisconnectAsync);

        // Redirect do authorization server — sem auth: o `state` assinado
        // carrega (userId, serverId) e expira em 10min.
        app.MapGet("/api/mcp/oauth/callback", CallbackAsync).AllowAnonymous();
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http, string id, AppDbContext db, McpOAuthService oauth,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var server = await db.McpServers
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == user.Id, ct);
        if (server is null || server.Transport != "http")
        {
            return Results.NotFound(new { error = "server não encontrado" });
        }
        var callback = $"{http.Request.Scheme}://{http.Request.Host}/api/mcp/oauth/callback";
        var url = await oauth.BeginAuthorizeAsync(user.Id, server, callback, ct);
        return url is null
            ? Results.BadRequest(new { error = "server does not advertise OAuth" })
            : Results.Ok(new { url });
    }

    private static async Task<IResult> StatusAsync(
        HttpContext http, string id, AppDbContext db, McpOAuthService oauth,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var exists = await db.McpServers
            .AnyAsync(s => s.Id == id && s.UserId == user.Id, ct);
        return exists
            ? Results.Ok(new { authState = await oauth.GetAuthStateAsync(user.Id, id, ct) })
            : Results.NotFound(new { error = "server não encontrado" });
    }

    private static async Task<IResult> DisconnectAsync(
        HttpContext http, string id, AppDbContext db, McpOAuthService oauth,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var exists = await db.McpServers
            .AnyAsync(s => s.Id == id && s.UserId == user.Id, ct);
        if (!exists)
        {
            return Results.NotFound(new { error = "server não encontrado" });
        }
        await oauth.DisconnectAsync(user.Id, id, ct);
        return Results.Ok(new { authState = "none" });
    }

    private static async Task<IResult> CallbackAsync(
        string? code, string? state, McpOAuthService oauth, CancellationToken ct)
    {
        var done = code is { Length: > 0 } && state is { Length: > 0 }
            && await oauth.CompleteAsync(state, code, ct) is not null;
        return Results.Content(
            "<!doctype html><title>MCP OAuth</title><body style=\"font-family:sans-serif;text-align:center;padding-top:4em\">"
            + (done
                ? "Autenticado — pode fechar esta janela.<script>window.close()</script>"
                : "Falha na autenticação ou sessão expirada — feche e tente de novo.")
            + "</body>", "text/html");
    }
}
