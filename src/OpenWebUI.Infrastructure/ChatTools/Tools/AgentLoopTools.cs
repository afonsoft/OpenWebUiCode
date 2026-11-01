using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Context;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:unseen_messages</c> — devolve mensagens do usuário que
/// chegaram durante a run (steer queue ainda pendente) e as marca como
/// vistas (SPEC-20261031-agent-prompt-pack RF-002). Somente leitura da
/// sessão atual; steering real-time sem parar a run.
/// </summary>
public sealed class UnseenMessagesBuiltinTool(AppDbContext db) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "unseen_messages";

    /// <inheritdoc />
    public string Description =>
        "Read user messages that arrived while you were working — call to "
        + "see pending steer instructions before continuing.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {}
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        List<ChatRunSteer> pending;
        try
        {
            pending = await db.ChatRunSteers
                .Where(s => s.RunId == context.RunId && s.Status == "pending")
                .OrderBy(s => s.Timestamp)
                .ToListAsync(ct);
        }
        catch (Exception)
        {
            // Tabela pode não existir em base antiga mid-deploy.
            return new BuiltinToolResult("no new messages");
        }
        if (pending.Count == 0)
        {
            return new BuiltinToolResult("no new messages");
        }
        var sb = new StringBuilder();
        foreach (var steer in pending)
        {
            steer.Status = "promoted";
            var ts = DateTimeOffset.FromUnixTimeSeconds(steer.Timestamp)
                .ToString("HH:mm:ss");
            sb.AppendLine($"[{ts}] ({steer.Mode}) {steer.Content}");
        }
        await db.SaveChangesAsync(ct);
        return new BuiltinToolResult(sb.ToString().TrimEnd());
    }
}

/// <summary>
/// <c>builtin:extend_context</c> — o modelo pede extensão da janela de
/// contexto (SPEC-20261031-agent-prompt-pack RF-004): concede até o teto
/// (<c>AgentLoop:ExtendContextCeiling</c>, default 2); cada concessão
/// permite ao loop pular uma compactação de emergência.
/// </summary>
public sealed class ExtendContextBuiltinTool(
    ExtendContextBudget budget,
    Microsoft.Extensions.Configuration.IConfiguration config)
    : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "extend_context";

    /// <inheritdoc />
    public string Description =>
        "Request more context window — call when the conversation is "
        + "approaching the limit and you still need the full history.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "reason": { "type": "string", "description": "Why you need more context (one line)." }
          }
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;

    /// <inheritdoc />
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var ceiling = int.TryParse(config["AgentLoop:ExtendContextCeiling"], out var ceil)
            ? ceil
            : ExtendContextBudget.DefaultCeiling;
        var status = budget.Request(context.RunId ?? string.Empty, ceiling);
        var text = status switch
        {
            ExtendContextBudget.Status.Granted =>
                "{\"status\":\"granted\"} — context window extended; keep working normally.",
            ExtendContextBudget.Status.AtCeiling =>
                "{\"status\":\"at_ceiling\"} — extension ceiling reached; "
                + "summarize your progress so far and continue with less context.",
            _ => "{\"status\":\"limit\"} — context extension is disabled.",
        };
        return Task.FromResult(new BuiltinToolResult(text));
    }
}

/// <summary>
/// <c>builtin:instant_context</c> — coleta cirúrgica de contexto via
/// <see cref="RestrictedExecRunner"/> (SPEC-20261031-agent-prompt-pack
/// RF-003): o modelo manda comandos allowlisted (<c>rg</c>, <c>readfile</c>,
/// <c>tree</c>) e recebe de volta XML estrito
/// <c>&lt;ANSWER&gt;&lt;file path&gt;&lt;range a-b/&gt;...&lt;/ANSWER&gt;</c>
/// com os trechos relevantes — sem prosa fora do XML.
/// </summary>
public sealed class InstantContextBuiltinTool(RestrictedExecRunner? runner = null)
    : IBuiltinChatTool
{
    private readonly RestrictedExecRunner _runner =
        runner ?? new RestrictedExecRunner();

    /// <inheritdoc />
    public string Name => "instant_context";

    /// <inheritdoc />
    public string Description =>
        "Collect precise context from the workspace — send read-only "
        + "commands (rg <pattern>, readfile <path> <a> <b>, tree <path> "
        + "<depth>) and get back an XML answer with file+line snippets.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "required": ["commands"],
          "properties": {
            "goal": { "type": "string", "description": "What you are looking for (one line)." },
            "commands": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Restricted commands: 'rg <pattern>', 'readfile <path> <a> <b>', 'tree <path> [depth]' (max 8)."
            }
          }
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;

    /// <inheritdoc />
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var commands = new List<string>();
        if (args.TryGetProperty("commands", out var cmds)
            && cmds.ValueKind == JsonValueKind.Array)
        {
            commands.AddRange(cmds.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.String)
                .Select(c => c.GetString() ?? string.Empty));
        }
        if (commands.Count == 0)
        {
            return Task.FromResult(new BuiltinToolResult(
                "commands é obrigatório (ex.: [\"rg FooService\", "
                + "\"readfile src/A.cs 1 40\", \"tree src 2\"])."));
        }

        var result = _runner.Run(context.WorkspacePath, commands);
        var xml = new StringBuilder("<ANSWER>");
        foreach (var s in result.Snippets)
        {
            xml.Append("<file path=\"")
               .Append(Escape(s.Path)).Append("\">")
               .Append("<range a=\"").Append(s.Start)
               .Append("\" b=\"").Append(s.End).Append("\"/>")
               .Append(Escape(s.Text))
               .Append("</file>");
        }
        xml.Append("</ANSWER>");
        foreach (var e in result.Errors)
        {
            xml.Append('\n').Append("# erro: ")
               .Append(e.Command).Append(" — ").Append(e.Error);
        }
        return Task.FromResult(new BuiltinToolResult(xml.ToString()));
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
