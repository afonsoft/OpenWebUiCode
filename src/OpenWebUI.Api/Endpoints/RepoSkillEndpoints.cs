using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Repo skills &amp; commands (SPEC-20261009-repo-skills-slash-commands,
/// E16 S3+S4): expõe <c>SKILL.md</c> e commands markdown do workdir do repo
/// vinculado como catálogo para slash commands no composer e para a tool
/// <c>builtin:skill</c>. Sem repo vinculado: 404 <c>{detail, bound:false}</c>.
/// </summary>
public static class RepoSkillEndpoints
{
    /// <summary>Mapeia as rotas de skills/commands dentro do grupo workspace/repo.</summary>
    public static void MapRepoSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var repo = app.MapGroup("/api/v1/workspace/repo").RequireAuthorization();
        repo.MapGet("/skills", ListSkillsAsync);
        repo.MapGet("/skills/{name}", GetSkillAsync);
        repo.MapPut("/skills/{name}/enabled", SetSkillEnabledAsync);
        repo.MapGet("/commands", ListCommandsAsync);
        repo.MapGet("/commands/{name}", GetCommandAsync);
        repo.MapPost("/commands/{name}/expand", ExpandCommandAsync);
    }

    /// <summary>
    /// Catálogo de skills do repo com provenance de desligamento
    /// (SPEC-20261014-config-layers): <c>enabled=false</c> quando o admin
    /// desligou globalmente (<c>disabledByAdmin</c>) ou o usuário desligou
    /// para si — deny admin é absoluto.
    /// </summary>
    private static async Task<IResult> ListSkillsAsync(
        string? chatId, string? scope, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        SkillDiscoveryService skills, UserConfigOverlayService overlay, CancellationToken ct)
    {
        var (userId, workdir, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }

        var hidden = await overlay.HiddenSkillNamesAsync(userId!, ct);
        var adminOff = await overlay.AdminDisabledSkillNamesAsync(ct);
        var scan = skills.Scan(workdir!);
        var items = scan.Skills
            .Where(s => scope is not ("global" or "project") || s.Scope == scope)
            .Select(s => new RepoSkillItemResponse(
                s.Name, s.Description, "skill", s.Path,
                !hidden.Contains(s.Name), adminOff.Contains(s.Name), s.Scope))
            .ToList();
        return Results.Ok(items);
    }

    /// <summary>
    /// Liga/desliga uma repo-skill só para o usuário atual
    /// (<c>u:{id}:skills-disabled</c>). Skill desligada pelo admin não
    /// religa — deny global é absoluto (RF-006).
    /// </summary>
    private static async Task<IResult> SetSkillEnabledAsync(
        string name, string? chatId, McpServerEnabledRequest request,
        HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        SkillDiscoveryService skills, UserConfigOverlayService overlay, CancellationToken ct)
    {
        var (userId, workdir, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }

        var entry = skills.Scan(workdir!).Skills
            .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return Results.NotFound(new { detail = $"Skill '{name}' não encontrada no repositório." });
        }
        var adminOff = await overlay.AdminDisabledSkillNamesAsync(ct);
        if (request.Enabled && adminOff.Contains(entry.Name))
        {
            return Results.BadRequest(new
            {
                detail = "Skill desabilitada pelo administrador — o usuário não pode religar.",
            });
        }

        await overlay.SetSkillDisabledAsync(userId!, entry.Name, disabled: !request.Enabled, ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<IResult> GetSkillAsync(
        string name, string? chatId, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        SkillDiscoveryService skills, CancellationToken ct)
    {
        var (_, workdir, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }

        var entry = skills.Scan(workdir!).Skills
            .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return entry is null
            ? Results.NotFound(new { detail = $"Skill '{name}' não encontrada no repositório." })
            : Results.Ok(new RepoSkillDetailResponse(
                entry.Name, entry.Description, "skill", entry.Path, entry.Body, entry.Scope));
    }

    private static async Task<IResult> ListCommandsAsync(
        string? chatId, string? scope, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        SkillDiscoveryService skills, CancellationToken ct)
    {
        var (_, workdir, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }

        var scan = skills.Scan(workdir!);
        return Results.Ok(scan.Commands
            .Where(c => scope is not ("global" or "project") || c.Scope == scope)
            .Select(c => new RepoCommandItemResponse(
                c.Name, c.Description, c.Agent, c.Model, c.Subtask, "command", c.Path, c.Scope))
            .ToList());
    }

    private static async Task<IResult> GetCommandAsync(
        string name, string? chatId, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        SkillDiscoveryService skills, CancellationToken ct)
    {
        var (_, workdir, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }

        var entry = skills.Scan(workdir!).Commands
            .FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return entry is null
            ? Results.NotFound(new { detail = $"Command '{name}' não encontrado no repositório." })
            : Results.Ok(new RepoCommandDetailResponse(
                entry.Name, entry.Description, entry.Agent, entry.Model,
                entry.Subtask, "command", entry.Path, entry.Body, entry.Scope));
    }

    /// <summary>
    /// Expande um command no servidor (SPEC-20261016-commands-v2 RF-003):
    /// <c>$ARGUMENTS</c>/<c>$N</c>, <c>!`shell`</c> (opt-in) e validação de
    /// <c>agent</c>/<c>model</c> declarados. O composer usa o resultado para
    /// montar o submit da run.
    /// </summary>
    private static async Task<IResult> ExpandCommandAsync(
        string name, RepoCommandExpandRequest request,
        string? chatId, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        RepoCommandService commands, CancellationToken ct)
    {
        var (userId, workdir, reject) = await BoundWorkdirAsync(http, db, repos, chatId, ct);
        if (reject is not null)
        {
            return reject;
        }

        var (result, error) = await commands.ExpandAsync(
            userId!, workdir!, name, request.Args, ct);
        if (result is null)
        {
            return error is null
                ? Results.NotFound(new { detail = $"Command '{name}' não encontrado." })
                : Results.BadRequest(new { detail = error });
        }
        if (error is not null)
        {
            return Results.BadRequest(new { detail = error });
        }

        return Results.Ok(new RepoCommandExpandResponse(
            result.Prompt, result.Agent, result.Model, result.Mode, result.Subagent));
    }

    /// <summary>Guard comum: usuário autenticado + repo vinculado → workdir.</summary>
    private static async Task<(string? UserId, string? Workdir, IResult? Reject)> BoundWorkdirAsync(
        HttpContext http, AppDbContext db, WorkspaceRepoService repos, string? chatId, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null, Results.Unauthorized());
        }

        if (chatId is not null
            && !await db.Chats.AsNoTracking().AnyAsync(c => c.Id == chatId && c.UserId == user.Id, ct))
        {
            return (null, null, Results.NotFound(new { detail = "Chat não encontrado." }));
        }

        var binding = await repos.ResolveBindingAsync(user.Id, chatId, ct);
        if (binding.Binding is null)
        {
            return (null, null, Results.NotFound(
                new { detail = "Nenhum repositório vinculado.", bound = false }));
        }

        return (user.Id, await repos.ResolveWorkdirAsync(user.Id, chatId, ct), null);
    }
}
