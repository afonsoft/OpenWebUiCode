using Microsoft.Extensions.Configuration;
using OpenWebUI.Infrastructure.ChatTools;

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
    IConfiguration cfg)
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
            return (null, null);
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
            CommandExpansion.ShellOutputMaxBytes, ct: ct);
        var text = outcome.TimedOut
            ? outcome.Output + "\n[timed out]"
            : outcome.Output;
        return text.Trim();
    }
}
