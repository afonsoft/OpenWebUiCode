using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Ecossistema de plugins: `/api/v1/skills` (CRUD por usuário),
/// `/api/v1/functions` (registro admin de filters/pipes/actions) e
/// `/api/v1/pipelines` (servidores externos + descoberta de pipes).
/// Nenhum código arbitrário executa no servidor .NET — execução é delegada
/// ao servidor de pipelines via HTTP.
/// </summary>
public static class PluginEndpoints
{
    private const string ByIdRoute = "/{id}";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        var skills = app.MapGroup("/api/v1/skills").RequireAuthorization();
        skills.MapGet("/", (HttpContext http, AppDbContext db, CancellationToken ct) =>
            ListSkillsAsync(http, db, ct));
        skills.MapPost("/", (HttpContext http, AppDbContext db, CancellationToken ct) =>
            CreateSkillAsync(http, db, ct));
        skills.MapGet(ByIdRoute, (HttpContext http, string id, AppDbContext db, CancellationToken ct) =>
            GetSkillAsync(http, id, db, ct));
        skills.MapPost(ByIdRoute, (HttpContext http, string id, AppDbContext db, CancellationToken ct) =>
            UpdateSkillAsync(http, id, db, ct));
        skills.MapDelete(ByIdRoute, (HttpContext http, string id, AppDbContext db, CancellationToken ct) =>
            DeleteSkillAsync(http, id, db, ct));

        var functions = app.MapGroup("/api/v1/functions").RequireAuthorization();
        functions.MapGet("/", (HttpContext http, AppDbContext db, CancellationToken ct) =>
            ListFunctionsAsync(http, db, ct));
        functions.MapPost("/", (HttpContext http, AppDbContext db, CancellationToken ct) =>
            CreateFunctionAsync(http, db, ct));
        functions.MapGet(ByIdRoute, (HttpContext http, string id, AppDbContext db, CancellationToken ct) =>
            GetFunctionAsync(http, id, db, ct));
        functions.MapPost(ByIdRoute, (HttpContext http, string id, AppDbContext db, CancellationToken ct) =>
            UpdateFunctionAsync(http, id, db, ct));
        functions.MapPost("/{id}/toggle", (HttpContext http, string id, AppDbContext db, CancellationToken ct) =>
            ToggleFunctionAsync(http, id, db, ct));
        functions.MapPost("/{id}/valves", (HttpContext http, string id, AppDbContext db, CancellationToken ct) =>
            UpdateValvesAsync(http, id, db, ct));
        functions.MapDelete(ByIdRoute, (HttpContext http, string id, AppDbContext db, CancellationToken ct) =>
            DeleteFunctionAsync(http, id, db, ct));

        var pipelines = app.MapGroup("/api/v1/pipelines").RequireAuthorization();
        pipelines.MapGet("/", (HttpContext http, AppDbContext db, CancellationToken ct) =>
            ListServersAsync(http, db, ct));
        pipelines.MapPost("/", (HttpContext http, AppDbContext db, CancellationToken ct) =>
            SaveServerAsync(http, db, ct));
        pipelines.MapGet("/list", (HttpContext http, AppDbContext db, PipelineClientService client, CancellationToken ct) =>
            ListAllPipesAsync(http, db, client, ct));
        pipelines.MapDelete(ByIdRoute, (HttpContext http, string id, AppDbContext db, CancellationToken ct) =>
            DeleteServerAsync(http, id, db, ct));
    }

    // ---------------- Skills ----------------

    private static async Task<IResult> ListSkillsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var skills = user.Role == UserRoles.Admin
            ? await db.Skills.OrderBy(s => s.Name).ToListAsync(ct)
            : await db.Skills.Where(s => s.UserId == user.Id).OrderBy(s => s.Name).ToListAsync(ct);
        return Results.Ok(skills.Select(ToSkillResponse));
    }

    private static async Task<IResult> GetSkillAsync(
        HttpContext http, string id, AppDbContext db, CancellationToken ct)
    {
        var (user, skill) = await FindOwnedAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        return skill is null ? Results.NotFound() : Results.Ok(ToSkillResponse(skill));
    }

    private static async Task<IResult> CreateSkillAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var request = await JsonSerializer.DeserializeAsync<SkillRequest>(
            http.Request.Body, JsonOptions, ct);
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { detail = "name é obrigatório." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var skill = new Skill
        {
            UserId = user.Id,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            Content = request.Content ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Skills.Add(skill);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToSkillResponse(skill));
    }

    private static async Task<IResult> UpdateSkillAsync(
        HttpContext http, string id, AppDbContext db, CancellationToken ct)
    {
        var (user, skill) = await FindOwnedAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (skill is null)
        {
            return Results.NotFound();
        }

        var request = await JsonSerializer.DeserializeAsync<SkillRequest>(
            http.Request.Body, JsonOptions, ct);
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { detail = "name é obrigatório." });
        }

        skill.Name = request.Name.Trim();
        skill.Description = request.Description?.Trim();
        skill.Content = request.Content ?? skill.Content;
        skill.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToSkillResponse(skill));
    }

    private static async Task<IResult> DeleteSkillAsync(
        HttpContext http, string id, AppDbContext db, CancellationToken ct)
    {
        var (user, skill) = await FindOwnedAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (skill is null)
        {
            return Results.NotFound();
        }

        db.Skills.Remove(skill);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<(User? User, Skill? Skill)> FindOwnedAsync(
        HttpContext http, string id, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null);
        }

        var skill = await db.Skills.FindAsync([id], ct);
        if (skill is not null && skill.UserId != user.Id && user.Role != UserRoles.Admin)
        {
            skill = null;
        }

        return (user, skill);
    }

    private static SkillResponse ToSkillResponse(Skill s) =>
        new(s.Id, s.Name, s.Description, s.Content, s.IsActive, s.CreatedAt, s.UpdatedAt);

    // ---------------- Functions ----------------

    private static async Task<IResult> ListFunctionsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, db, ct))
        {
            return Results.Forbid();
        }

        var functions = await db.Functions.OrderBy(f => f.Name).ToListAsync(ct);
        return Results.Ok(functions.Select(ToFunctionResponse));
    }

    private static async Task<IResult> GetFunctionAsync(
        HttpContext http, string id, AppDbContext db, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, db, ct))
        {
            return Results.Forbid();
        }

        var function = await db.Functions.FindAsync([id], ct);
        return function is null ? Results.NotFound() : Results.Ok(ToFunctionResponse(function));
    }

    private static async Task<IResult> CreateFunctionAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AdminUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Forbid();
        }

        var request = await ReadFunctionRequestAsync(http, ct);
        if (request is null)
        {
            return Results.BadRequest(new { detail = "name e type são obrigatórios." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var function = new Function
        {
            UserId = user.Id,
            Name = request.Name.Trim(),
            Type = request.Type.Trim().ToLowerInvariant(),
            ManifestJson = request.ManifestJson ?? "{}",
            ValvesJson = request.ValvesJson,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Functions.Add(function);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToFunctionResponse(function));
    }

    private static async Task<IResult> UpdateFunctionAsync(
        HttpContext http, string id, AppDbContext db, CancellationToken ct)
    {
        if (await AdminUserAsync(http, db, ct) is null)
        {
            return Results.Forbid();
        }

        var function = await db.Functions.FindAsync([id], ct);
        if (function is null)
        {
            return Results.NotFound();
        }

        var request = await ReadFunctionRequestAsync(http, ct);
        if (request is null)
        {
            return Results.BadRequest(new { detail = "name e type são obrigatórios." });
        }

        function.Name = request.Name.Trim();
        function.Type = request.Type.Trim().ToLowerInvariant();
        function.ManifestJson = request.ManifestJson ?? function.ManifestJson;
        function.ValvesJson = request.ValvesJson ?? function.ValvesJson;
        function.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToFunctionResponse(function));
    }

    private static async Task<IResult> ToggleFunctionAsync(
        HttpContext http, string id, AppDbContext db, CancellationToken ct)
    {
        if (await AdminUserAsync(http, db, ct) is null)
        {
            return Results.Forbid();
        }

        var function = await db.Functions.FindAsync([id], ct);
        if (function is null)
        {
            return Results.NotFound();
        }

        function.Active = !function.Active;
        function.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToFunctionResponse(function));
    }

    private static async Task<IResult> UpdateValvesAsync(
        HttpContext http, string id, AppDbContext db, CancellationToken ct)
    {
        if (await AdminUserAsync(http, db, ct) is null)
        {
            return Results.Forbid();
        }

        var function = await db.Functions.FindAsync([id], ct);
        if (function is null)
        {
            return Results.NotFound();
        }

        var request = await JsonSerializer.DeserializeAsync<FunctionValvesRequest>(
            http.Request.Body, JsonOptions, ct);
        if (request is null)
        {
            return Results.BadRequest(new { detail = "valvesJson inválido." });
        }

        try
        {
            JsonDocument.Parse(request.ValvesJson);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { detail = "valvesJson não é JSON válido." });
        }

        function.ValvesJson = request.ValvesJson;
        function.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToFunctionResponse(function));
    }

    private static async Task<IResult> DeleteFunctionAsync(
        HttpContext http, string id, AppDbContext db, CancellationToken ct)
    {
        if (await AdminUserAsync(http, db, ct) is null)
        {
            return Results.Forbid();
        }

        var function = await db.Functions.FindAsync([id], ct);
        if (function is null)
        {
            return Results.NotFound();
        }

        db.Functions.Remove(function);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<FunctionRequest?> ReadFunctionRequestAsync(
        HttpContext http, CancellationToken ct)
    {
        var request = await JsonSerializer.DeserializeAsync<FunctionRequest>(
            http.Request.Body, JsonOptions, ct);
        if (request is null || string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.Type))
        {
            return null;
        }

        if (request.Type.Trim().ToLowerInvariant() is not ("filter" or "pipe" or "action"))
        {
            return null;
        }

        return request;
    }

    private static FunctionResponse ToFunctionResponse(Function f) =>
        new(f.Id, f.Name, f.Type, f.ManifestJson, f.ValvesJson, f.Active, f.CreatedAt, f.UpdatedAt);

    // ---------------- Pipelines ----------------

    private static async Task<IResult> ListServersAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (!await IsAdminAsync(http, db, ct))
        {
            return Results.Forbid();
        }

        var servers = await db.PipelineServers.OrderBy(s => s.Name).ToListAsync(ct);
        return Results.Ok(servers.Select(ToServerResponse));
    }

    private static async Task<IResult> SaveServerAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await AdminUserAsync(http, db, ct) is null)
        {
            return Results.Forbid();
        }

        var request = await JsonSerializer.DeserializeAsync<PipelineServerRequest>(
            http.Request.Body, JsonOptions, ct);
        if (request is null || string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.Url)
            || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return Results.BadRequest(new { detail = "name e url (http/https) são obrigatórios." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var server = await db.PipelineServers
            .FirstOrDefaultAsync(s => s.Name == request.Name.Trim(), ct);
        if (server is null)
        {
            server = new PipelineServer { CreatedAt = now };
            db.PipelineServers.Add(server);
        }

        server.Name = request.Name.Trim();
        server.Url = request.Url.Trim().TrimEnd('/');
        if (request.Key is not null and not "********")
        {
            server.Key = string.IsNullOrWhiteSpace(request.Key) ? null : request.Key.Trim();
        }
        server.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToServerResponse(server));
    }

    private static async Task<IResult> DeleteServerAsync(
        HttpContext http, string id, AppDbContext db, CancellationToken ct)
    {
        if (await AdminUserAsync(http, db, ct) is null)
        {
            return Results.Forbid();
        }

        var server = await db.PipelineServers.FindAsync([id], ct);
        if (server is null)
        {
            return Results.NotFound();
        }

        db.PipelineServers.Remove(server);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    /// <summary>Agrega pipes de todos os servidores (usado pelo seletor de modelos).</summary>
    private static async Task<IResult> ListAllPipesAsync(
        HttpContext http, AppDbContext db, PipelineClientService client, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var servers = await db.PipelineServers.ToListAsync(ct);
        var pipes = new List<PipelinePipeResponse>();
        foreach (var server in servers)
        {
            var found = await client.ListPipesAsync(server, ct);
            if (found is not null)
            {
                pipes.AddRange(found.Select(
                    p => new PipelinePipeResponse($"pipeline:{p.Id}", p.Name, server.Id)));
            }
        }

        return Results.Ok(pipes);
    }

    private static PipelineServerResponse ToServerResponse(PipelineServer s) =>
        new(s.Id, s.Name, s.Url, !string.IsNullOrEmpty(s.Key), s.CreatedAt);

    private static async Task<bool> IsAdminAsync(
        HttpContext http, AppDbContext db, CancellationToken ct) =>
        (await AuthEndpoints.FindUserAsync(http, db, ct))?.Role == UserRoles.Admin;

    private static async Task<User?> AdminUserAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        return user?.Role == UserRoles.Admin ? user : null;
    }
}
