namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Item do catálogo de agentes declarativos
/// (SPEC-20261014-agent-definitions RF-004).
/// </summary>
/// <param name="Name">Id kebab-case do agente.</param>
/// <param name="Description">Uma linha — o que o modelo vê no agent_catalog.</param>
/// <param name="Model">Id explícito, <c>role:</c> ou null (herda do caller).</param>
/// <param name="Source">Provenance: <c>builtin|admin|user|workspace</c>.</param>
/// <param name="Tools">Allowlist de tools (null = herda filtrado a Read; ["*"] = integral).</param>
/// <param name="Spawns">Agentes que ele pode delegar.</param>
/// <param name="MaxDepth">Teto de profundidade de delegação abaixo dele.</param>
/// <param name="Blocking">Default de wait no delegate.</param>
public sealed record AgentDefinitionResponse(
    string Name,
    string Description,
    string? Model,
    string Source,
    IReadOnlyList<string>? Tools,
    IReadOnlyList<string> Spawns,
    int MaxDepth,
    bool Blocking);

/// <summary>Resposta de GET /api/v1/agents — catálogo + flags de workspace.</summary>
/// <param name="Agents">Catálogo efetivo ordenado por nome.</param>
/// <param name="WorkspaceOptIn">Opt-in do usuário a agentes do workdir.</param>
/// <param name="WorkspaceAgentsPresent">O workdir atual tem .openwebui/agents/ (dica para a UI oferecer o opt-in).</param>
public sealed record AgentCatalogResponse(
    IReadOnlyList<AgentDefinitionResponse> Agents,
    bool WorkspaceOptIn,
    bool WorkspaceAgentsPresent);
