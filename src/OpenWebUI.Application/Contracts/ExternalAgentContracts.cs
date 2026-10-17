namespace OpenWebUI.Application.Contracts;

/// <summary>Status de um agente externo (CLI detectado) conforme GET /api/v1/external-agents/detect.</summary>
/// <param name="Id">Identificador estável do agente (ex.: "opencode").</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Installed">Se o binário foi encontrado no PATH.</param>
/// <param name="Version">Versão detectada (quando instalado).</param>
/// <param name="SkillInstalled">Se a skill-ponte openwebui-cli está instalada no agente.</param>
/// <param name="Selected">Se o usuário selecionou este agente como executor externo.</param>
public sealed record ExternalAgentStatusResponse(
    string Id,
    string Name,
    bool Installed,
    string? Version,
    bool SkillInstalled,
    bool Selected);

/// <summary>Toggle de seleção de um agente externo (PUT /{id}/selected).</summary>
public sealed record ExternalAgentSelectRequest(bool Enabled);
