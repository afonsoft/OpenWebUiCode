using System.Text;
using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:agent_catalog</c> — lista os agentes declarativos
/// visíveis ao usuário atual (SPEC-20261014-agent-definitions RF-004):
/// nome, descrição, modelo e provenance em texto compacto para o modelo
/// decidir a quem delegar via <c>builtin:delegate_task(agent:…)</c>.
/// Somente leitura — não executa nada.
/// </summary>
public sealed class AgentCatalogBuiltinTool(
    AgentDefinitionService agents, PermissionRuleEvaluator rules)
    : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "agent_catalog";

    /// <inheritdoc />
    public string Description =>
        "List available declarative agents (name, description, model) "
            + "that delegate_task can invoke via the 'agent' parameter.";

    /// <inheritdoc />
    public string ParametersJson => """
        { "type": "object", "properties": {} }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;


    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var catalog = await agents.ListAsync(context.UserId, context.WorkspacePath, ct);
        if (catalog.Count == 0)
        {
            return new BuiltinToolResult(
                "Nenhum agente disponível (defina *.agent.md em data/agents "
                    + "ou data/users/<id>/agents).");
        }

        var header = "Agentes disponíveis (delegate via agent: nome):\n";
        var sb = new StringBuilder(header);
        foreach (var def in catalog.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            // SPEC-20261124-denied-agents-hidden (opencode dev): agente
            // com deny em regra do usuário é omitido do catálogo — o
            // modelo nunca vê o que não pode invocar (o deny no gate
            // continua enforcement, a omissão é UX/economia de turno).
            var effect = await rules.EvaluateAsync(context.UserId,
                "subagent", [$"delegate_task:agent:{def.Name}"], ct);
            if (string.Equals(effect, "deny", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            sb.Append("- ").Append(def.Name)
                .Append(" [").Append(def.Source).Append(']')
                .Append(def.Model is { } m ? $" model={m}" : " model=(herda do caller)")
                .Append(def.Spawns.Count > 0 ? $" spawns=[{string.Join(",", def.Spawns)}]" : string.Empty)
                .Append(" — ").Append(def.Description).Append('\n');
        }
        return sb.Length == header.Length
            ? new BuiltinToolResult(
                "Nenhum agente delegável — todos os agentes do catálogo "
                    + "estão negados por regra de permissão.")
            : new BuiltinToolResult(sb.ToString().TrimEnd());
    }
}
