using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Contexto estruturado do wizard de geração
/// (SPEC-20261027-context-gen-wizard): upsert do contexto (1 ativo por
/// usuário) e <c>POST /generate</c> que cria o chat com a run injetando o
/// system prompt construído via <c>Params["gen_context"]</c> →
/// <see cref="Completions.Contributors.GenContextContributor"/>.
/// </summary>
public static class GenContextEndpoints
{
    /// <summary>Mapeia os endpoints do gen-context.</summary>
    public static IEndpointRouteBuilder MapGenContextEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/gen-context").RequireAuthorization();
        group.MapGet("", GetAsync);
        group.MapPost("", UpsertAsync);
        group.MapPost("/generate", GenerateAsync);
        return app;
    }

    /// <summary>GET /api/v1/gen-context — contexto ativo do usuário.</summary>
    private static async Task<IResult> GetAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var entry = await FindActiveAsync(db, user.Id, ct);
        if (entry is null)
        {
            return Results.NotFound(new { detail = "Nenhum contexto de geração salvo." });
        }

        return Results.Ok(ToView(entry));
    }

    /// <summary>POST /api/v1/gen-context — upsert validado (RF-001).</summary>
    private static async Task<IResult> UpsertAsync(
        HttpContext http, AppDbContext db,
        JsonElement body, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var raw = body.GetRawText();
        if (!GenContextValidator.TryParse(raw, out var ctx, out var error))
        {
            return Results.BadRequest(new { detail = error });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entry = await db.GenerationContexts
            .FirstOrDefaultAsync(g => g.UserId == user.Id, ct);
        if (entry is null)
        {
            entry = new GenerationContextEntry
            {
                UserId = user.Id,
                CreatedAt = now,
            };
            db.GenerationContexts.Add(entry);
        }

        entry.Json = JsonSerializer.Serialize(ctx, JsonOptions);
        entry.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToView(entry));
    }

    /// <summary>
    /// POST /api/v1/gen-context/generate (RF-004): cria o chat com a
    /// primeira mensagem = prompt livre/conteúdo e a run com
    /// <c>Params["gen_context"]</c> = id do contexto — o contributor
    /// injeta o system prompt construído no início da run.
    /// </summary>
    private static async Task<IResult> GenerateAsync(
        HttpContext http, AppDbContext db,
        ChatRunDispatcher dispatcher, BudgetPolicyService budgets,
        ConfigService config, GenContextGenerateRequest request,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var entry = await db.GenerationContexts
            .FirstOrDefaultAsync(g => g.UserId == user.Id, ct);
        if (entry is null
            || !GenContextValidator.TryParse(entry.Json, out var ctx, out _))
        {
            return Results.Json(
                new { detail = "Contexto de geração inexistente — salve o wizard antes." },
                statusCode: 422);
        }

        var model = request.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return Results.BadRequest(new { detail = "model é obrigatório." });
        }

        var content = request.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            content = ctx!.FreePrompt;
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            return Results.BadRequest(new
            {
                detail = "Informe o prompt livre (wizard passo 4 ou content).",
            });
        }

        var gate = await budgets.CheckStartAsync(user.Id, model, ct);
        if (!gate.Allowed)
        {
            return Results.Json(
                new { detail = gate.Detail, error_code = gate.Code },
                statusCode: 429);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var chat = new Chat
        {
            UserId = user.Id,
            Title = TitleFor(ctx!, content),
            ModelsJson = JsonSerializer.Serialize(new[] { model }, JsonOptions),
            ToolIdsJson = "[]",
            AdvisorEnabled = await config.GetAsync<bool?>(
                "advisor.default", null, ct) ?? false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        chat.Messages =
        [
            new ChatMessage
            {
                ChatId = chat.Id,
                Role = "user",
                Content = content,
                Model = model,
                Position = 0,
                Timestamp = now,
            },
        ];
        db.Chats.Add(chat);

        var runParams = new Dictionary<string, object>(
            request.Params ?? new Dictionary<string, object>())
        {
            ["gen_context"] = entry.Id,
        };
        var completionRequest = new ChatCompletionRequest(
            model,
            [new ChatCompletionMessage("user", content)],
            Stream: true,
            Params: runParams);
        var run = new ChatRun
        {
            ChatId = chat.Id,
            UserId = user.Id,
            Model = model,
            RequestJson = JsonSerializer.Serialize(completionRequest, JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(run);

        entry.LastChatId = chat.Id;
        entry.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        dispatcher.Enqueue(run.Id);
        return Results.Accepted(
            value: new GenContextGenerateResponse(chat.Id, run.Id));
    }

    private static async Task<GenerationContextEntry?> FindActiveAsync(
        AppDbContext db, string userId, CancellationToken ct) =>
        await db.GenerationContexts.AsNoTracking()
            .FirstOrDefaultAsync(g => g.UserId == userId, ct);

    private static object ToView(GenerationContextEntry e) => new
    {
        id = e.Id,
        context = JsonSerializer.Deserialize<JsonElement>(e.Json),
        lastChatId = e.LastChatId,
        updatedAt = e.UpdatedAt,
    };

    private static string TitleFor(GenerationContext ctx, string content)
    {
        var baseTitle = string.IsNullOrWhiteSpace(ctx.FreePrompt)
            ? content
            : ctx.FreePrompt;
        var title = baseTitle.Trim();
        return title.Length <= 80 ? title : title[..80].TrimEnd() + "…";
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
}
