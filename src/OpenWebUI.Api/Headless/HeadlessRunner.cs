using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Headless;

/// <summary>
/// Runner one-shot do modo <c>--headless</c>
/// (SPEC-20261021-harness-seams RF-C1): cria/reusa um chat, enfileira a
/// mensagem e executa a run no mesmo processo, emitindo JSON Lines em
/// stdout via <see cref="JsonLinesRunObserver"/>.
///
/// Uso: <c>open-webui --headless --user-email a@b --prompt "..." [--model m]
/// [--chat-id c]</c>. Exit codes: 0 completed · 1 run failed · 2 erro de
/// uso/usuário ausente.
/// </summary>
public static class HeadlessRunner
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <see langword="true"/> quando os args pedem o modo headless.
    /// </summary>
    public static bool IsHeadless(string[] args) => args.Contains("--headless");

    /// <summary>
    /// Executa a run one-shot. <paramref name="services"/> é o provider já
    /// construído pelo host (migrations/seed já aplicados no boot).
    /// </summary>
    public static async Task<int> RunAsync(
        IServiceProvider services, string[] args, CancellationToken ct)
    {
        var prompt = Arg(args, "--prompt") ?? Arg(args, "-p");
        var email = Arg(args, "--user-email");
        var model = Arg(args, "--model");
        var chatId = Arg(args, "--chat-id");

        if (string.IsNullOrWhiteSpace(prompt))
        {
            await Console.Error.WriteLineAsync(
                "headless: --prompt é obrigatório "
                + "(uso: --headless --user-email x --prompt '...' [--model m] [--chat-id c])");
            return 2;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        User? user = null;
        if (!string.IsNullOrWhiteSpace(email))
        {
            user = await db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email == email, ct);
        }
        else
        {
            // Sem --user-email: usa o primeiro usuário (instalação single-user).
            user = await db.Users.AsNoTracking().OrderBy(u => u.CreatedAt)
                .FirstOrDefaultAsync(ct);
        }
        if (user is null)
        {
            await Console.Error.WriteLineAsync(
                $"headless: usuário '{email}' não encontrado.");
            return 2;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var chat = chatId is null
            ? null
            : await db.Chats.Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == chatId && c.UserId == user.Id, ct);
        if (chatId is not null && chat is null)
        {
            await Console.Error.WriteLineAsync(
                $"headless: chat '{chatId}' não encontrado para o usuário.");
            return 2;
        }

        model ??= FirstModel(chat?.ModelsJson);
        if (model is null)
        {
            await Console.Error.WriteLineAsync(
                "headless: sem modelo — passe --model ou use --chat-id de um chat com modelo.");
            return 2;
        }

        if (chat is null)
        {
            chat = new Chat
            {
                UserId = user.Id,
                Title = prompt.Length > 60 ? prompt[..60] : prompt,
                ModelsJson = JsonSerializer.Serialize(
                    new[] { model }, JsonOptions),
                CreatedAt = now,
            };
            db.Chats.Add(chat);
        }

        var leaf = chat.Messages?.OrderByDescending(m => m.Position)
            .FirstOrDefault();
        var message = new ChatMessage
        {
            ChatId = chat.Id,
            Role = "user",
            Content = prompt,
            Position = (leaf?.Position ?? -1) + 1,
            Timestamp = now,
            ParentId = leaf?.Id,
        };
        db.ChatMessages.Add(message);
        chat.LeafMessageId = message.Id;
        chat.UpdatedAt = now;

        var run = new ChatRun
        {
            ChatId = chat.Id,
            UserId = user.Id,
            Model = model,
            RequestJson = JsonSerializer.Serialize(
                new ChatCompletionRequest(
                    model,
                    [new ChatCompletionMessage("user", prompt)],
                    Stream: true),
                JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync(ct);

        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "run.created",
            runId = run.Id,
            chatId = chat.Id,
            model,
        }, JsonOptions));

        var executor = scope.ServiceProvider
            .GetRequiredService<ChatRunExecutor>();
        await executor.ExecuteAsync(run, ct);

        var final = await db.ChatRuns.AsNoTracking()
            .Where(r => r.Id == run.Id)
            .Select(r => new { r.Id, r.Status })
            .FirstAsync(ct);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "result",
            runId = final.Id,
            status = final.Status,
        }, JsonOptions));
        return final.Status == ChatRunStatus.Completed ? 0 : 1;
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static string? FirstModel(string? modelsJson)
    {
        try
        {
            var models = JsonSerializer.Deserialize<List<string>>(
                modelsJson ?? "[]", JsonOptions);
            return models?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
