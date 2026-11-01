using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Agentes declarativos (SPEC-20261014-agent-definitions RF-004):
/// <c>GET /api/v1/agents</c> devolve o catálogo efetivo do usuário com
/// provenance — built-in &lt; admin (<c>data/agents/</c>) &lt; usuário
/// (<c>data/users/{id}/agents/</c>) &lt; workspace
/// (<c>{workdir}/.openwebui/agents/</c>, gated pelo opt-in
/// <c>u:{id}:agents.workspace.enabled</c>).
/// </summary>
public static class AgentEndpoints
{
    /// <summary>Mapeia as rotas do catálogo de agentes.</summary>
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/agents").RequireAuthorization();
        group.MapGet("", ListAgentsAsync);
        group.MapPut("/workspace-enabled", SetWorkspaceEnabledAsync);
    }

    /// <summary>
    /// GET /api/v1/agents — catálogo efetivo. <c>chatId</c> opcional resolve
    /// o workdir para incluir agentes de workspace quando o opt-in está on.
    /// </summary>
    private static async Task<IResult> ListAgentsAsync(
        string? chatId, HttpContext http, AppDbContext db,
        AgentDefinitionService agents, WorkspaceRepoService repos,
        ConfigService config, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (chatId is not null
            && !await db.Chats.AsNoTracking()
                .AnyAsync(c => c.Id == chatId && c.UserId == user.Id, ct))
        {
            return Results.NotFound(new { detail = "Chat não encontrado." });
        }

        string? workdir = null;
        if ((await repos.ResolveBindingAsync(user.Id, chatId, ct)).Binding is not null)
        {
            workdir = await repos.ResolveWorkdirAsync(user.Id, chatId, ct);
        }

        var catalog = await agents.ListAsync(user.Id, workdir, ct);
        return Results.Ok(new AgentCatalogResponse(
            catalog.Values
                .OrderBy(d => d.Name, StringComparer.Ordinal)
                .Select(d => new AgentDefinitionResponse(
                    d.Name, d.Description, d.Model, d.Source,
                    d.Tools, d.Spawns, d.MaxDepth, d.Blocking))
                .ToList(),
            WorkspaceOptIn: await config.GetAsync(
                $"u:{user.Id}:agents.workspace.enabled", false, ct),
            WorkspaceAgentsPresent: AgentDefinitionService.HasWorkspaceAgents(workdir)));
    }

    /// <summary>
    /// PUT /api/v1/agents/workspace-enabled — opt-in do usuário para
    /// agentes vindos do workdir do repo vinculado (OQ-2 da SPEC: repo
    /// clonado não injeta system prompt/tools sem consentimento).
    /// </summary>
    private static async Task<IResult> SetWorkspaceEnabledAsync(
        McpServerEnabledRequest request, HttpContext http, AppDbContext db,
        ConfigService config, AgentDefinitionService agents, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        await config.SetAsync(
            $"u:{user.Id}:agents.workspace.enabled", request.Enabled, ct);
        AgentDefinitionService.Invalidate(user.Id);
        return Results.Ok(new StatusResponse(true));
    }
}
