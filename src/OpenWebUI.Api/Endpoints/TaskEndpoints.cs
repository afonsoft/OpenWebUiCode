using System.Text.Json;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de tarefas auxiliares de IA (título, follow-ups, tags), espelhando <c>/api/v1/tasks</c>.</summary>
public static class TaskEndpoints
{
    /// <summary>Mapeia as rotas de tarefas.</summary>
    public static RouteGroupBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tasks").RequireAuthorization();

        group.MapPost("/title/completions", GenerateTitleAsync);
        group.MapPost("/follow_up/completions", GenerateFollowUpsAsync);
        group.MapPost("/tags/completions", GenerateTagsAsync);
        group.MapPost("/queries/completions", GenerateQueriesAsync);

        return group;
    }

    private static async Task<IResult> GenerateTitleAsync(
        TaskGenerationRequest request, HttpContext http, AppDbContext db,
        ProviderService providers, ModelRoleResolver roles, CancellationToken ct)
    {
        var model = await ResolveTaskModelAsync(request, http, db, roles, ct);
        if (model is null)
        {
            return Results.Unauthorized();
        }
        var title = await RunTaskAsync(providers, model, request, ct, """
            ### Task:
            Generate a concise, 3-5 word title with an emoji summarizing the chat history.
            ### Guidelines:
            - The title should clearly represent the main theme or subject of the conversation.
            - Do not use quotation marks or special characters.
            - Respond with only the title text.
            ### Chat History:
            """);

        if (title is null)
        {
            // Sem título gerado: o cliente mantém o título digitado em vez de sobrescrever.
            return Results.NotFound();
        }

        var cleaned = title.Trim().Trim('"', '\'', '`').Trim();
        var firstLine = cleaned.Split('\n')[0].Trim();
        return Results.Ok(new TaskTitleResponse(
            firstLine.Length > 80 ? firstLine[..80] : firstLine));
    }

    private static async Task<IResult> GenerateFollowUpsAsync(
        TaskGenerationRequest request, HttpContext http, AppDbContext db,
        ProviderService providers, ModelRoleResolver roles, CancellationToken ct)
    {
        var model = await ResolveTaskModelAsync(request, http, db, roles, ct);
        if (model is null)
        {
            return Results.Unauthorized();
        }
        var text = await RunTaskAsync(providers, model, request, ct, """
            ### Task:
            Propose 2-3 relevant follow-up questions or prompts that the user might naturally ask next in this conversation as a **user**, based on the chat history, to help continue or deepen the discussion.
            ### Guidelines:
            - Write all follow-up questions from the user's point of view, directed to the assistant.
            - Make sure the follow-ups are natural in tone and relevant to the conversation.
            - Respond ONLY with a JSON array of strings, e.g. ["Question 1?", "Question 2?"].
            ### Chat History:
            """);

        return Results.Ok(new TaskFollowUpsResponse(ParseJsonStringList(text)));
    }

    private static async Task<IResult> GenerateTagsAsync(
        TaskGenerationRequest request, HttpContext http, AppDbContext db,
        ProviderService providers, ModelRoleResolver roles, CancellationToken ct)
    {
        var model = await ResolveTaskModelAsync(request, http, db, roles, ct);
        if (model is null)
        {
            return Results.Unauthorized();
        }
        var text = await RunTaskAsync(providers, model, request, ct, """
            ### Task:
            Generate 1-3 broad tags categorizing the main themes of the chat history.
            ### Guidelines:
            - Tags should be short, lowercase, single words or two-word phrases.
            - Respond ONLY with a JSON array of strings, e.g. ["finance", "health"].
            ### Chat History:
            """);

        var tags = ParseJsonStringList(text)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct()
            .Take(5)
            .ToList();

        return Results.Ok(new TaskTagsResponse(tags));
    }

    private static async Task<IResult> GenerateQueriesAsync(
        TaskGenerationRequest request, HttpContext http, AppDbContext db,
        ProviderService providers, ModelRoleResolver roles, CancellationToken ct)
    {
        var model = await ResolveTaskModelAsync(request, http, db, roles, ct);
        if (model is null)
        {
            return Results.Unauthorized();
        }
        var text = await RunTaskAsync(providers, model, request, ct, """
            ### Task:
            Generate 1-3 precise web search queries to retrieve additional context relevant to the conversation.
            Respond ONLY with a JSON array of strings.
            ### Chat History:
            """);

        return Results.Ok(new { queries = ParseJsonStringList(text) });
    }

    /// <summary>
    /// Resolve o modelo efetivo das tasks auxiliares (SPEC-20261013-model-roles
    /// RF-002): papel <c>tasks</c> do usuário com fallback para o modelo do
    /// chat (<c>request.Model</c>). Null quando o usuário não autentica.
    /// </summary>
    private static async Task<string?> ResolveTaskModelAsync(
        TaskGenerationRequest request, HttpContext http, AppDbContext db,
        ModelRoleResolver roles, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return null;
        }
        return await roles.ResolveAsync("tasks", user.Id, request.Model, ct);
    }

    private static async Task<string?> RunTaskAsync(
        ProviderService providers,
        string model,
        TaskGenerationRequest request,
        CancellationToken ct,
        string template)
    {
        try
        {
            var history = string.Join("\n", request.Messages.Select(m => $"{m.Role}: {m.Content}"));
            var prompt = $"{template}\n{history}";

            var taskRequest = new ChatCompletionRequest(
                model,
                [new ChatCompletionMessage("user", prompt)],
                Stream: false);

            return await providers.CompleteAsync(taskRequest, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static List<string> ParseJsonStringList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var trimmed = text.Trim();
        var start = trimmed.IndexOf('[');
        var end = trimmed.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(trimmed[start..(end + 1)]) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
