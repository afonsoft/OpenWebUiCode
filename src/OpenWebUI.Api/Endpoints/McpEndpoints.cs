using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints admin de servidores MCP (/api/v1/mcp/servers).</summary>
public static class McpEndpoints
{
    private const string Masked = "********";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HybridCacheEntryOptions ListCacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(60),
        LocalCacheExpiration = TimeSpan.FromSeconds(60),
    };

    /// <summary>Mapeia o grupo /api/v1/mcp/servers (admin).</summary>
    public static void MapMcpEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/mcp/servers").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapPost("/{id}/refresh", RefreshAsync);
        group.MapGet("/{id}/tools", ListToolsAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, HybridCache cache, CancellationToken ct)
    {
        var user = await RequireAdminAsync(http, db, ct);
        if (user is null)
        {
            return Results.Forbid();
        }

        // 60s: lista de servidores muda raramente e é consultada por
        // painel + resolução de tools a cada mensagem de chat.
        // O painel admin gerencia apenas os servidores globais (UserId null)
        // — servidores de usuário (SPEC-20261014-config-layers) ficam no
        // escopo de /users/user/mcp-servers.
        var servers = await cache.GetOrCreateAsync(
            "mcp-servers:list",
            async cancel => await db.McpServers.AsNoTracking()
                .Where(s => s.UserId == null).OrderBy(s => s.Name).ToListAsync(cancel),
            ListCacheOptions, tags: ["mcp"], cancellationToken: ct);
        return Results.Ok(servers.Select(ToResponse));
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] McpServerUpsertRequest request,
        HttpContext http, AppDbContext db, HybridCache cache, CancellationToken ct)
    {
        var user = await RequireAdminAsync(http, db, ct);
        if (user is null)
        {
            return Results.Forbid();
        }
        if (Validate(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var server = new McpServer
        {
            Name = request.Name.Trim(),
            Transport = request.Transport.Trim().ToLowerInvariant(),
            Command = request.Command?.Trim(),
            ArgsJson = JsonSerializer.Serialize(request.Args ?? [], JsonOptions),
            EnvJson = JsonSerializer.Serialize(request.EnvNames ?? [], JsonOptions),
            Url = request.Url?.Trim(),
            HeadersJson = JsonSerializer.Serialize(
                (request.Headers ?? []).Where(h => h.Value != Masked)
                    .ToDictionary(h => h.Key, h => h.Value), JsonOptions),
            Enabled = request.Enabled,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync(ct);
        await cache.RemoveByTagAsync("mcp", ct);
        return Results.Ok(ToResponse(server));
    }

    private static async Task<IResult> UpdateAsync(
        string id, [FromBody] McpServerUpsertRequest request,
        HttpContext http, AppDbContext db, HybridCache cache, CancellationToken ct)
    {
        var user = await RequireAdminAsync(http, db, ct);
        if (user is null)
        {
            return Results.Forbid();
        }
        var server = await db.McpServers.FirstOrDefaultAsync(s => s.Id == id && s.UserId == null, ct);
        if (server is null)
        {
            return Results.NotFound(new { detail = "Servidor MCP não encontrado." });
        }
        if (Validate(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }

        server.Name = request.Name.Trim();
        server.Transport = request.Transport.Trim().ToLowerInvariant();
        server.Command = request.Command?.Trim();
        server.ArgsJson = JsonSerializer.Serialize(request.Args ?? [], JsonOptions);
        server.EnvJson = JsonSerializer.Serialize(request.EnvNames ?? [], JsonOptions);
        server.Url = request.Url?.Trim();

        // "********" preserva o valor gravado do header (mesma convenção das API keys).
        var headers = ParseHeaders(server.HeadersJson);
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

        // Desligar o server tira suas tools do function-calling sem deletá-las.
        var virtualTools = await db.Tools
            .Where(t => t.UserId == null
                && t.Url.StartsWith($"{McpClientService.VirtualUrlPrefix}{id}/"))
            .ToListAsync(ct);
        foreach (var tool in virtualTools)
        {
            tool.Enabled = request.Enabled;
        }

        await db.SaveChangesAsync(ct);
        await cache.RemoveByTagAsync("mcp", ct);
        return Results.Ok(ToResponse(server));
    }

    private static async Task<IResult> DeleteAsync(
        string id, HttpContext http, AppDbContext db, HybridCache cache, CancellationToken ct)
    {
        var user = await RequireAdminAsync(http, db, ct);
        if (user is null)
        {
            return Results.Forbid();
        }
        var server = await db.McpServers.FirstOrDefaultAsync(s => s.Id == id && s.UserId == null, ct);
        if (server is null)
        {
            return Results.NotFound(new { detail = "Servidor MCP não encontrado." });
        }

        var virtualTools = db.Tools.Where(
            t => t.UserId == null
                && t.Url.StartsWith($"{McpClientService.VirtualUrlPrefix}{id}/"));
        db.Tools.RemoveRange(virtualTools);
        db.McpServers.Remove(server);
        await db.SaveChangesAsync(ct);
        await cache.RemoveByTagAsync("mcp", ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<IResult> RefreshAsync(
        string id, HttpContext http, AppDbContext db,
        McpClientService mcp, HybridCache cache, CancellationToken ct)
    {
        var user = await RequireAdminAsync(http, db, ct);
        if (user is null)
        {
            return Results.Forbid();
        }
        var server = await db.McpServers.FirstOrDefaultAsync(s => s.Id == id && s.UserId == null, ct);
        if (server is null)
        {
            return Results.NotFound(new { detail = "Servidor MCP não encontrado." });
        }

        try
        {
            var count = await mcp.RefreshToolsAsync(server, ct);
            await cache.RemoveByTagAsync("mcp", ct);
            return Results.Ok(new { status = true, tools = count });
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { detail = ex.Message });
        }
    }

    private static async Task<IResult> ListToolsAsync(
        string id, HttpContext http, AppDbContext db, HybridCache cache, CancellationToken ct)
    {
        var user = await RequireAdminAsync(http, db, ct);
        if (user is null)
        {
            return Results.Forbid();
        }
        var server = await db.McpServers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.UserId == null, ct);
        if (server is null)
        {
            return Results.NotFound(new { detail = "Servidor MCP não encontrado." });
        }

        // Cache 60s do resultado (o mesmo TTL do refresh do cliente MCP);
        // mutações de server/tool invalidam pela tag "mcp".
        var tools = await cache.GetOrCreateAsync(
            $"mcp-tools-list:{id}",
            async cancel => await db.Tools.AsNoTracking()
                .Where(t => t.UserId == null
                    && t.Url.StartsWith($"{McpClientService.VirtualUrlPrefix}{id}/"))
                .OrderBy(t => t.Name)
                .Select(t => new McpToolResponse(
                    t.Id, t.Name, ToolExecutor.FunctionName(t) ?? t.Name, t.Description, t.Enabled))
                .ToListAsync(cancel),
            ListCacheOptions, tags: ["mcp"], cancellationToken: ct);
        return Results.Ok(tools);
    }

    /// <summary>Valida o registro: transport, comando (stdio) ou url http(s) sem IP de metadata.</summary>
    internal static string? Validate(McpServerUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "Nome é obrigatório.";
        }
        var transport = request.Transport?.Trim().ToLowerInvariant();
        if (transport is not "stdio" and not "http")
        {
            return "Transport inválido — use \"stdio\" ou \"http\".";
        }
        if (transport == "stdio" && string.IsNullOrWhiteSpace(request.Command))
        {
            return "Command é obrigatório para transport stdio.";
        }
        if (transport == "http")
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
            {
                return "URL inválida (deve ser http(s) absoluta).";
            }
            if (uri.Host.StartsWith("169.254.", StringComparison.Ordinal)
                || uri.Host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
            {
                return "URL inválida: endereços de metadata de nuvem não são permitidos.";
            }
        }
        return null;
    }

    private static async Task<User?> RequireAdminAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        return user?.Role == UserRoles.Admin ? user : null;
    }

    private static McpServerResponse ToResponse(McpServer s) =>
        new(
            s.Id,
            s.Name,
            s.Transport,
            s.Command,
            McpClientService.ParseStringList(s.ArgsJson),
            McpClientService.ParseStringList(s.EnvJson),
            s.Url,
            ParseHeaders(s.HeadersJson).Keys.ToDictionary(k => k, _ => Masked),
            s.Enabled,
            s.LastError,
            s.CreatedAt);

    private static Dictionary<string, string> ParseHeaders(string? json) =>
        McpClientService.ParseHeaders(json);
}
