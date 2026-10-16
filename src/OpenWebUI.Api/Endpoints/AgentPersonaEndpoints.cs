using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Rotas de personas de agente acionáveis por canal
/// (SPEC-20261016-agent-personas-channels): CRUD em
/// <c>/api/v1/agent-personas</c>, dispatch de mensagem de canal em
/// <c>/{id}/message</c> (webhook — síncrono até 2min ou
/// <c>?wait=false</c> → 202) e audit de turnos em <c>/{id}/audit</c>.
/// </summary>
public static class AgentPersonaEndpoints
{
    private static readonly HashSet<string> GuardrailLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "basic", "strict",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia o grupo /api/v1/agent-personas.</summary>
    public static void MapAgentPersonaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/agent-personas").WithTags("AgentPersonas");
        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);
        group.MapPost("/{id}/message", SendMessageAsync);
        group.MapGet("/{id}/audit", AuditAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var personas = await db.AgentPersonas.AsNoTracking()
            .Where(p => p.UserId == user.Id)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
        return Results.Ok(personas.Select(ToResponse));
    }

    private static async Task<IResult> CreateAsync(
        AgentPersonaUpsertRequest request, HttpContext http,
        AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (Validate(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var persona = new AgentPersona
        {
            UserId = user.Id,
            Name = request.Name!.Trim(),
            SystemPrompt = request.SystemPrompt?.Trim() ?? string.Empty,
            ModelId = request.ModelId!.Trim(),
            GuardrailLevel = request.GuardrailLevel?.Trim().ToLowerInvariant() ?? "basic",
            ToolsJson = request.Tools is { Count: > 0 }
                ? JsonSerializer.Serialize(request.Tools, JsonOptions)
                : null,
            Enabled = request.Enabled ?? true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AgentPersonas.Add(persona);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/agent-personas/{persona.Id}", ToResponse(persona));
    }

    private static async Task<IResult> UpdateAsync(
        string id, AgentPersonaUpsertRequest request, HttpContext http,
        AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var persona = await db.AgentPersonas
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == user.Id, ct);
        if (persona is null)
        {
            return Results.NotFound(new { detail = "Persona não encontrada." });
        }
        if (Validate(request) is { } error)
        {
            return Results.BadRequest(new { detail = error });
        }

        persona.Name = request.Name!.Trim();
        persona.SystemPrompt = request.SystemPrompt?.Trim() ?? string.Empty;
        persona.ModelId = request.ModelId!.Trim();
        persona.GuardrailLevel = request.GuardrailLevel?.Trim().ToLowerInvariant() ?? "basic";
        persona.ToolsJson = request.Tools is { Count: > 0 }
            ? JsonSerializer.Serialize(request.Tools, JsonOptions)
            : null;
        persona.Enabled = request.Enabled ?? persona.Enabled;
        persona.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(persona));
    }

    private static async Task<IResult> DeleteAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var persona = await db.AgentPersonas
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == user.Id, ct);
        if (persona is null)
        {
            return Results.NotFound(new { detail = "Persona não encontrada." });
        }

        db.AgentPersonas.Remove(persona);
        await db.AgentPersonaSessions.Where(s => s.PersonaId == id)
            .ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    /// <summary>
    /// RF-002/RF-006: mensagem de canal (webhook) → sessão da persona.
    /// Síncrono por default (até ~2min, 200 + reply); <c>?wait=false</c>
    /// devolve 202 + runId e o turno fecha no audit.
    /// </summary>
    private static async Task<IResult> SendMessageAsync(
        string id, PersonaMessageRequest request, HttpContext http,
        AppDbContext db, PersonaDispatchService dispatch, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.UnprocessableEntity(new { detail = "message é obrigatória." });
        }

        var persona = await db.AgentPersonas.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == user.Id, ct);
        if (persona is null || !persona.Enabled)
        {
            return Results.NotFound(new { detail = "Persona não encontrada." });
        }

        var wait = http.Request.Query["wait"] != "false";
        var outcome = await dispatch.SendAsync(
            persona, request.Message.Trim(), request.ChatKey, wait, ct);
        if (outcome.RateLimited)
        {
            return Results.Json(
                new { detail = "Rate limit da persona excedido (60/min)." },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var response = new PersonaMessageResponse(
            outcome.Reply, outcome.SessionId!, outcome.RunId!, outcome.Status);
        return outcome.Status == "running"
            ? Results.Accepted(value: response)
            : Results.Ok(response);
    }

    private static async Task<IResult> AuditAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct,
        int take = 50)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var owns = await db.AgentPersonas.AsNoTracking()
            .AnyAsync(p => p.Id == id && p.UserId == user.Id, ct);
        if (!owns)
        {
            return Results.NotFound(new { detail = "Persona não encontrada." });
        }

        var turns = await db.AgentTurns.AsNoTracking()
            .Where(t => t.PersonaId == id)
            .OrderByDescending(t => t.Timestamp)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);
        return Results.Ok(turns.Select(t => new AgentTurnResponse(
            t.Id, t.Timestamp, t.ChatKey, t.Channel, t.InText, t.OutText, t.Status)));
    }

    private static string? Validate(AgentPersonaUpsertRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100)
        {
            return "name é obrigatório (até 100 chars).";
        }
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            return "modelId é obrigatório.";
        }
        if (request.GuardrailLevel is not null
            && !GuardrailLevels.Contains(request.GuardrailLevel))
        {
            return "guardrailLevel deve ser none|basic|strict.";
        }
        if (request.SystemPrompt is { Length: > 8192 })
        {
            return "systemPrompt deve ter até 8KB.";
        }
        return null;
    }

    private static AgentPersonaResponse ToResponse(AgentPersona p) => new(
        p.Id, p.Name, p.SystemPrompt, p.ModelId, p.GuardrailLevel,
        p.ToolsJson is null
            ? null
            : JsonSerializer.Deserialize<List<string>>(p.ToolsJson, JsonOptions),
        p.Enabled, p.CreatedAt, p.UpdatedAt);
}
