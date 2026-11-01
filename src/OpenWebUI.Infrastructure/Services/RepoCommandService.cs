using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Resultado da expansão de um command (SPEC-20261016-commands-v2).</summary>
/// <param name="Prompt">Texto final (template + args + shell blocks).</param>
/// <param name="Agent">Agent definition referenciado pelo frontmatter.</param>
/// <param name="Model">Override de modelo declarado pelo command.</param>
/// <param name="Mode"><c>plan|build|chat</c> — do frontmatter <c>mode</c> ou
/// <c>agent</c> quando o valor é um modo e não um agent definition.</param>
/// <param name="Subagent"><c>subagent:true</c> → executar como run filha.</param>
/// <param name="Entry">Entrada descoberta (catálogo/caminho).</param>
public sealed record CommandExpansionResult(
    string Prompt, string? Agent, string? Model, string? Mode,
    bool Subagent, RepoEntry Entry);

/// <summary>
/// Resolve e expande commands do repositório/global no servidor
/// (SPEC-20261016-commands-v2 RF-001..RF-006): interpola
/// <c>$ARGUMENTS</c>/<c>$N</c>, avalia <c>!`shell`</c> somente com
/// <c>RepoCommands:AllowShellExpansion=true</c> (default off — nunca sem
/// opt-in), valida <c>agent:</c> contra <see cref="AgentDefinitionService"/>
/// (caindo em mode quando o valor é <c>plan|build|chat</c>) e
/// <c>model:</c> contra os modelos listados do usuário.
/// </summary>
public sealed class RepoCommandService(
    SkillDiscoveryService skills,
    AgentDefinitionService agents,
    ProviderService providers,
    IConfiguration cfg,
    McpClientService? mcp = null,
    AppDbContext? db = null)
{
    /// <summary>Flag opt-in que habilita <c>!`shell`</c> (RF-006).</summary>
    public bool ShellExpansionEnabled =>
        cfg.GetValue("RepoCommands:AllowShellExpansion", false);

    /// <summary>
    /// Expande o command <paramref name="name"/> com <paramref name="args"/>.
    /// Devolve (result, null) ou (null, erro-para-400). <paramref name="name"/>
    /// desconhecido devolve <c>(null, null)</c> — o chamador mapeia para 404.
    /// </summary>
    public async Task<(CommandExpansionResult? Result, string? Error)> ExpandAsync(
        string userId, string? workdir, string name, string? args,
        CancellationToken ct)
    {
        var entry = skills.Scan(workdir).Commands
            .FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            // RF-003 (SPEC-20261018-agent-polish): `{server}:{prompt}`
            // resolve um prompt MCP — não exige repo bound.
            return await ExpandMcpPromptAsync(userId, name, args, ct);
        }

        var prompt = await CommandExpansion.ExpandAsync(
            entry.Body, args ?? string.Empty,
            ShellExpansionEnabled, workdir, RunShellAsync, ct);

        string? agent = entry.Agent;
        string? mode = entry.Mode;
        if (agent is not null
            && await agents.FindAsync(userId, workdir, agent, ct) is null)
        {
            // Open Question do SPEC: valor desconhecido de `agent:` cai em
            // mode quando é plan|build|chat — senão é erro claro.
            if (IsMode(agent))
            {
                mode ??= agent.ToLowerInvariant();
                agent = null;
            }
            else
            {
                return (null, $"Command '{name}': agent '{agent}' não encontrado.");
            }
        }
        if (mode is not null && !IsMode(mode))
        {
            return (null, $"Command '{name}': mode '{mode}' inválido (plan|build|chat).");
        }
        mode = mode?.ToLowerInvariant();

        var model = entry.Model;
        if (model is not null
            && !(await providers.ListModelsAsync(ct))
                .Any(m => m.Id.Equals(model, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, $"Command '{name}': model '{model}' indisponível.");
        }

        return (new CommandExpansionResult(
            prompt, agent, model, mode, entry.Subtask, entry), null);
    }

    /// <summary>
    /// Expande `<c>{server}:{prompt}</c>` via <c>prompts/get</c> (RF-003):
    /// tokens posicionais de <paramref name="args"/> caem sobre os nomes
    /// de argumento declarados pelo prompt. Server/prompt ausente →
    /// (null, null) para o chamador mapear 404; falha no get → erro 400.
    /// </summary>
    private async Task<(CommandExpansionResult? Result, string? Error)> ExpandMcpPromptAsync(
        string userId, string name, string? args, CancellationToken ct)
    {
        if (mcp is null || db is null)
        {
            return (null, null);
        }
        var colon = name.IndexOf(':');
        if (colon <= 0 || colon == name.Length - 1)
        {
            return (null, null);
        }
        var serverName = name[..colon];
        var promptName = name[(colon + 1)..];

        var server = await db.McpServers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Name == serverName && s.Enabled
                && (s.UserId == null || s.UserId == userId), ct);
        if (server is null)
        {
            return (null, null);
        }

        var prompts = await mcp.ListPromptsAsync(server, ct);
        var prompt = prompts.FirstOrDefault(p =>
            p.Name.Equals(promptName, StringComparison.OrdinalIgnoreCase));
        if (prompt is null)
        {
            return (null, null);
        }

        var tokens = (args ?? "").Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var argMap = new Dictionary<string, object?>();
        for (var i = 0; i < prompt.Arguments.Count && i < tokens.Length; i++)
        {
            argMap[prompt.Arguments[i]] = tokens[i];
        }

        var text = await mcp.GetPromptAsync(server, prompt.Name, argMap, ct);
        if (text is null)
        {
            return (null,
                $"Command '{name}': falha ao resolver o prompt MCP no servidor.");
        }
        var synthetic = new RepoEntry(
            name, prompt.Description ?? "", $"mcp://{server.Id}/{prompt.Name}",
            text, Scope: "mcp");
        return (new CommandExpansionResult(
            text, null, null, null, false, synthetic), null);
    }

    /// <summary><paramref name="value"/> é um modo literal plan|build|chat?</summary>
    private static bool IsMode(string value) =>
        value.Equals(PermissionRuleset.PlanMode, StringComparison.OrdinalIgnoreCase)
        || value.Equals(PermissionRuleset.BuildMode, StringComparison.OrdinalIgnoreCase)
        || value.Equals(PermissionRuleset.ChatMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>Runner de <c>!`shell`</c> — 30s, 8KB, saída mascarada.</summary>
    private static async Task<string> RunShellAsync(
        string cmd, string? workdir, CancellationToken ct)
    {
        var outcome = await ChatProcessRunner.RunAsync(
            cmd, workdir ?? Directory.GetCurrentDirectory(),
            CommandExpansion.ShellTimeout,
            new ChatProcessRunner.RunOptions(
                MaxOutputChars: CommandExpansion.ShellOutputMaxBytes), ct);
        var text = outcome.TimedOut
            ? outcome.Output + "\n[timed out]"
            : outcome.Output;
        return text.Trim();
    }
}
