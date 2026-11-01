using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Lifecycle hooks do workspace (SPEC-20261031-lifecycle-hooks RF-007):
/// lista os hooks resolvidos (repo + usuário) e grava o arquivo nativo
/// <c>.openwebui/hooks.json</c> do repo (admin). <c>POST …/hooks/test</c>
/// roda um hook command com payload fornecido (dry-run).
/// </summary>
public static class HookEndpoints
{
    /// <summary>Mapeia as rotas dentro do grupo workspace/repo.</summary>
    public static void MapHookEndpoints(this IEndpointRouteBuilder app)
    {
        var repo = app.MapGroup("/api/v1/workspace/repo").RequireAuthorization();
        repo.MapGet("/hooks", ListAsync);
        repo.MapPut("/hooks", PutAsync);
        repo.MapPost("/hooks/test", TestAsync);
    }

    /// <summary><c>GET …/hooks</c> — hooks resolvidos com origem.</summary>
    private static async Task<IResult> ListAsync(
        string? chatId, HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, LifecycleHookService service,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var workdir = await repos.ResolveWorkdirAsync(user.Id, chatId, ct);
        var hooks = service.Resolve(workdir)
            .Select(h => new HookItem(
                h.Event, h.Matcher, h.Type, h.Command,
                h.TimeoutSeconds, h.Source))
            .ToList();
        return Results.Ok(hooks);
    }

    /// <summary><c>PUT …/hooks</c> — grava .openwebui/hooks.json (admin).</summary>
    private static async Task<IResult> PutAsync(
        string? chatId, JsonElement body, HttpContext http,
        AppDbContext db, WorkspaceRepoService repos,
        LifecycleHookService service, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (user.Role != "admin")
        {
            return Results.Forbid();
        }
        var workdir = await repos.ResolveWorkdirAsync(user.Id, chatId, ct);
        if (workdir is null)
        {
            return Results.NotFound(
                new { detail = "Nenhum repositório vinculado.", bound = false });
        }
        var dir = Path.Combine(workdir, ".openwebui");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, "hooks.json"), body.GetRawText(), ct);
        // Invalida o cache local (stamp muda no próximo resolve).
        return Results.Ok(new
        {
            written = $"{".openwebui"}/hooks.json",
            hooks = service.Resolve(workdir).Count,
        });
    }

    /// <summary><c>POST …/hooks/test</c> — dry-run de um hook command.</summary>
    private static async Task<IResult> TestAsync(
        JsonElement body, HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, CommandHookRunner runner,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var command = body.TryGetProperty("command", out var c)
            && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return Results.BadRequest(new { detail = "command é obrigatório." });
        }
        var workdir = await repos.ResolveWorkdirAsync(
            user.Id, body.TryGetProperty("chatId", out var ch)
                && ch.ValueKind == JsonValueKind.String
                    ? ch.GetString() : null, ct);
        var hook = new HookDefinition(
            body.TryGetProperty("event", out var e)
                && e.ValueKind == JsonValueKind.String
                    ? e.GetString()! : "PreToolUse",
            null, "command", command,
            body.TryGetProperty("timeout", out var t)
                && t.ValueKind == JsonValueKind.Number
                && t.TryGetInt32(out var n) ? n : 60, "test");
        var payload = body.TryGetProperty("payload", out var p)
            ? p : JsonSerializer.SerializeToElement(new { });
        var raw = await runner.ExecAsync(hook, payload,
            workdir ?? Directory.GetCurrentDirectory(), null, null, ct);
        return Results.Ok(new
        {
            exitCode = raw.ExitCode,
            stdout = raw.Stdout,
            stderr = raw.Stderr,
            timedOut = raw.TimedOut,
        });
    }

    /// <summary>Item da listagem de hooks.</summary>
    public sealed record HookItem(
        string Event, string? Matcher, string Type, string Command,
        int TimeoutSeconds, string Source);
}
