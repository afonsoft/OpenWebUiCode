using System.Text.Json;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.DevPipeline;

/// <summary>
/// <c>builtin:pipeline_board</c> — quadro de coordenação entre workers
/// de um pipeline/fan-out (SPEC-20261124-workflow-v2): workers irmãos
/// são cegos uns aos outros; o board em kv (<c>board:{user}:{id}</c>)
/// deixa cada um postar bloqueios/descobertas e ler os relatos das
/// irmãs. O orquestrador colhe o board no join como
/// <c>PhaseResults[{step}:board]</c>, disponível a steps seguintes via
/// <c>{results:{step}:board}</c>.
/// </summary>
public sealed class PipelineBoardBuiltinTool(
    IServiceScopeFactory scopes) : IBuiltinChatTool
{
    /// <summary>Máximo de mensagens retidas por board (as mais antigas
    /// são descartadas).</summary>
    internal const int MaxMessages = 200;
    /// <summary>Teto de caracteres por mensagem.</summary>
    internal const int MaxMessageChars = 2000;
    /// <summary>Máximo devolvido num <c>read</c>.</summary>
    internal const int ReadWindow = 50;

    /// <summary>Mensagem do board: autor, texto e epoch (ms).</summary>
    public sealed record BoardMessage(string Author, string Text, long At);

    /// <inheritdoc />
    public string Name => "pipeline_board";

    /// <inheritdoc />
    public string Description =>
        "Shared coordination board for sibling pipeline workers: "
            + "op='post' appends a note (blockers, discoveries, interface "
            + "decisions other workers must know); op='read' lists recent "
            + "notes (optionally only after index 'since'). The board id "
            + "is given in your worker prompt.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "op": {
              "type": "string",
              "enum": ["post", "read"],
              "description": "'post' appends a message; 'read' lists recent messages."
            },
            "board": {
              "type": "string",
              "description": "Board id (from the worker prompt — usually the pipeline id)."
            },
            "message": {
              "type": "string",
              "description": "op=post: the note for sibling workers (max 2000 chars)."
            },
            "author": {
              "type": "string",
              "description": "op=post: optional label (e.g. your item id); defaults to your run id."
            },
            "since": {
              "type": "integer",
              "description": "op=read: only messages after this index."
            }
          },
          "required": ["board"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var board = ReadString(args, "board");
        if (string.IsNullOrWhiteSpace(board) || board.Length > 128)
        {
            return new BuiltinToolResult(
                "Parâmetro 'board' é obrigatório (id informado no prompt "
                    + "do worker, máx 128 chars).");
        }
        var key = $"board:{context.UserId}:{board}";
        var op = ReadString(args, "op")?.ToLowerInvariant() ?? "read";

        using var scope = scopes.CreateScope();
        var config = scope.ServiceProvider
            .GetRequiredService<ConfigService>();

        if (op == "post")
        {
            var message = ReadString(args, "message");
            if (string.IsNullOrWhiteSpace(message))
            {
                return new BuiltinToolResult(
                    "op=post exige 'message'.");
            }
            var author = ReadString(args, "author")
                ?? context.RunId ?? context.ChatId ?? "worker";
            var list = await config.GetAsync(
                key, new List<BoardMessage>(), ct) ?? [];
            list.Add(new BoardMessage(author,
                message.Length > MaxMessageChars
                    ? message[..MaxMessageChars] : message,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            if (list.Count > MaxMessages)
            {
                list.RemoveRange(0, list.Count - MaxMessages);
            }
            await config.SetAsync(key, list, ct);
            return new BuiltinToolResult(
                $"Board '{board}': mensagem #{list.Count} postada "
                    + $"como '{author}'.",
                new { board, index = list.Count - 1, author });
        }
        if (op == "read")
        {
            var list = await config.GetAsync(
                key, new List<BoardMessage>(), ct) ?? [];
            var since = args.TryGetProperty("since", out var s)
                && s.TryGetInt32(out var n) ? Math.Max(0, n) : 0;
            var start = Math.Max(since, list.Count - ReadWindow);
            if (start >= list.Count)
            {
                return new BuiltinToolResult(
                    $"Board '{board}' sem mensagens novas "
                        + $"(total {list.Count}).",
                    new { board, count = list.Count });
            }
            var lines = list.Skip(start).Select((m, i) =>
                $"#{start + i + 1} {m.Author}: {m.Text}");
            return new BuiltinToolResult(
                $"Board '{board}' ({list.Count - start} novas):\n"
                    + string.Join('\n', lines),
                new { board, count = list.Count });
        }
        return new BuiltinToolResult($"op '{op}' inválido — post|read.");
    }

    private static string? ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.String
                ? el.GetString()?.Trim()
                : null;
}
