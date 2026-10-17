namespace OpenWebUI.Application.Contracts;

/// <summary>Persona de agente acionável por canal (SPEC-20261016-agent-personas-channels RF-001).</summary>
public sealed record AgentPersonaResponse(
    string Id,
    string Name,
    string SystemPrompt,
    string ModelId,
    string GuardrailLevel,
    IReadOnlyList<string>? Tools,
    bool Enabled,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Payload de criação/atualização de persona.</summary>
public sealed record AgentPersonaUpsertRequest(
    string? Name,
    string? SystemPrompt,
    string? ModelId,
    string? GuardrailLevel,
    IReadOnlyList<string>? Tools,
    bool? Enabled);

/// <summary>Mensagem de canal para a persona (RF-002).</summary>
/// <param name="Message">Texto recebido do canal (obrigatório).</param>
/// <param name="ChatKey">Agrupador lógico da sessão (default "default").</param>
public sealed record PersonaMessageRequest(string? Message, string? ChatKey);

/// <summary>Resposta síncrona do dispatch (200) ou aceite assíncrono (202).</summary>
/// <param name="Reply">Texto do turno quando concluído dentro do wait.</param>
/// <param name="SessionId">Chat subjacente da sessão (persona × chatKey).</param>
/// <param name="RunId">Run que produz/produziu a resposta.</param>
/// <param name="Status">completed | running (quando 202/timeout).</param>
public sealed record PersonaMessageResponse(
    string? Reply, string SessionId, string RunId, string Status);

/// <summary>Turno auditável da persona (RF-004).</summary>
public sealed record AgentTurnResponse(
    string Id,
    long Timestamp,
    string ChatKey,
    string Channel,
    string InText,
    string? OutText,
    string Status);
