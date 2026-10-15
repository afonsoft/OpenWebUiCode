using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Endpoints de servidores MCP do usuário
/// (<c>/api/v1/users/user/mcp-servers</c>, SPEC-20261014-config-layers
/// RF-002): o GET devolve o catálogo efetivo (globais habilitados −
/// desligados pelo usuário − sombreados por nome + os próprios) com
/// provenance; o CRUD só toca linhas <c>UserId == user</c>. Transport
/// restrito a <c>http</c> remoto — <c>stdio</c> de usuário é superfície
/// admin-only (OQ-3 da SPEC).
/// </summary>
public static class UserMcpEndpoints
{
    private const string Masked = "********";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia o grupo /api/v1/users/user/mcp-servers (autenticado).</summary>
    public static void MapUserMcpEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/users/user/mcp-servers").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapPost("/{id}/refresh", RefreshAsync);
        group.MapPut("/{id}/enabled", SetEnabledAsync);
    }

    /// <summary>Catálogo efetivo: globais + próprios, com provenance e toggle por usuário.</summary>
    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db,
        UserConfigOverlayService overlay, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var effective = await overlay.EffectiveMcpServersAsync(user.Id, ct);
        return Results.Ok(effective.Select(e => new UserMcpServerEntry(
            ToResponse(e.Server),
            e.Source == ConfigSource.User ? "user" : "global",
            e.EnabledForMe)));
    }

    /// <summary>Cria servidor do usuário (só transport http) e já descobre as tools.</summary>
    private static async Task<IResult> CreateAsync(
        [FromBody] McpServerUpsertRequest request,
        HttpContext http, AppDbContext db, McpClientService mcp,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (McpEndpoints.Validate(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }
        if (!string.Equals(request.Transport?.Trim(), "http", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new
            {
                detail = "Servidores de usuário aceitam só transport http — stdio é admin-only.",
            });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var server = new McpServer
        {
            Name = request.Name.Trim(),
            Transport = "http",
            Url = request.Url?.Trim(),
            HeadersJson = JsonSerializer.Serialize(
                (request.Headers ?? []).Where(h => h.Value != Masked)
                    .ToDictionary(h => h.Key, h => h.Value), JsonOptions),
            Enabled = request.Enabled,
            UserId = user.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync(ct);
        await TryRefreshAsync(mcp, server, ct);
        return Results.Ok(ToResponse(server));
    }

    /// <summary>Edita servidor do usuário (404 em servidor global ou de outro usuário).</summary>
    private static async Task<IResult> UpdateAsync(
        string id, [FromBody] McpServerUpsertRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var server = await db.McpServers
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == user.Id, ct);
        if (server is null)
        {
            return Results.NotFound(new { detail = "Servidor MCP não encontrado." });
        }
        if (McpEndpoints.Validate(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }
        if (!string.Equals(request.Transport?.Trim(), "http", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { detail = "Servidores de usuário aceitam só transport http." });
        }

        server.Name = request.Name.Trim();
        server.Transport = "http";
        server.Url = request.Url?.Trim();
        var headers = McpClientService.ParseHeaders(server.HeadersJson);
        foreach (var (key, value) in request.Headers ?? [])
        {
            if (value != Masked)
            {
                headers[key] = value;
            }
        }
        var stale = headers.Keys
            .Where(k => request.Headers is not null && !request.Headers.ContainsKey(k)).ToList();
        foreach (var key in stale)
        {
            headers.Remove(key);
        }
        server.HeadersJson = JsonSerializer.Serialize(headers, JsonOptions);
        server.Enabled = request.Enabled;
        server.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(server));
    }

    /// <summary>Remove servidor do usuário e suas tools virtuais.</summary>
    private static async Task<IResult> DeleteAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var server = await db.McpServers
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == user.Id, ct);
        if (server is null)
        {
            return Results.NotFound(new { detail = "Servidor MCP não encontrado." });
        }
        db.Tools.RemoveRange(db.Tools.Where(
            t => t.Url.StartsWith($"{McpClientService.VirtualUrlPrefix}{id}/")));
        db.McpServers.Remove(server);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    /// <summary>Redescobre as tools de um servidor do usuário.</summary>
    private static async Task<IResult> RefreshAsync(
        string id, HttpContext http, AppDbContext db,
        McpClientService mcp, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var server = await db.McpServers
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == user.Id, ct);
        if (server is null)
        {
            return Results.NotFound(new { detail = "Servidor MCP não encontrado." });
        }
        try
        {
            var count = await mcp.RefreshToolsAsync(server, ct);
            return Results.Ok(new { status = true, tools = count });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { detail = ex.Message });
        }
    }

    /// <summary>
    /// Toggle no escopo do usuário: em servidor global grava o tombstone
    /// <c>u:{id}:mcp-disabled</c>; em servidor próprio troca <c>Enabled</c>
    /// (e o Enabled das tools virtuais, igual ao caminho admin).
    /// </summary>
    private static async Task<IResult> SetEnabledAsync(
        string id, [FromBody] McpServerEnabledRequest request,
        HttpContext http, AppDbContext db,
        UserConfigOverlayService overlay, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var server = await db.McpServers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null)
        {
            return Results.NotFound(new { detail = "Servidor MCP não encontrado." });
        }
        if (server.UserId is not null && server.UserId != user.Id)
        {
            return Results.NotFound(new { detail = "Servidor MCP não encontrado." });
        }

        if (server.UserId is null)
        {
            await overlay.SetMcpDisabledAsync(user.Id, id, disabled: !request.Enabled, ct);
            return Results.Ok(new { status = true });
        }

        server.Enabled = request.Enabled;
        server.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var tools = await db.Tools
            .Where(t => t.Url.StartsWith($"{McpClientService.VirtualUrlPrefix}{id}/"))
            .ToListAsync(ct);
        foreach (var tool in tools)
        {
            tool.Enabled = request.Enabled;
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    /// <summary>Refresh best-effort após criar — erro vira LastError, não falha do POST.</summary>
    private static async Task TryRefreshAsync(
        McpClientService mcp, McpServer server, CancellationToken ct)
    {
        try
        {
            await mcp.RefreshToolsAsync(server, ct);
        }
        catch (InvalidOperationException)
        {
            // LastError já gravado pelo RefreshToolsAsync — a UI mostra.
        }
    }

    private static McpServerResponse ToResponse(McpServer s) =>
        new(
            s.Id, s.Name, s.Transport, s.Command,
            McpClientService.ParseStringList(s.ArgsJson),
            McpClientService.ParseStringList(s.EnvJson),
            s.Url,
            McpClientService.ParseHeaders(s.HeadersJson)
                .Keys.ToDictionary(k => k, _ => Masked),
            s.Enabled, s.LastError, s.CreatedAt);
}
