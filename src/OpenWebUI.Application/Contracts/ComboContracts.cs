namespace OpenWebUI.Application.Contracts;

/// <summary>Item de um combo na criação/atualização (ordenado por Position).</summary>
public sealed record ComboItemRequest(string ModelId, int? Position = null);

/// <summary>
/// Criação/atualização de combo (SPEC-20261016-model-combos RF-005).
/// <paramref name="Global"/> (admin) cria combo sem dono, visível a todos.
/// </summary>
public sealed record ComboUpsertRequest(
    string Name,
    string? Strategy,
    List<ComboItemRequest> Items,
    int? RequestsPerMinute = null,
    int? TokensPerMinute = null,
    bool? Enabled = null,
    bool? Global = null);

/// <summary>Combo exibido na listagem do usuário.</summary>
public sealed record ComboResponse(
    string Id,
    string Name,
    string? UserId,
    string Strategy,
    int? RequestsPerMinute,
    int? TokensPerMinute,
    bool Enabled,
    List<string> Items,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Cooldown ativo reportado em <c>/status</c>.</summary>
public sealed record ComboCooldownResponse(string ModelId, long Until);

/// <summary>Status do combo: modelo efetivo atual, cooldowns e janela de 60s.</summary>
public sealed record ComboStatusResponse(
    string ComboId,
    string? EffectiveModel,
    List<ComboCooldownResponse> Cooldowns,
    int WindowRequests,
    long WindowTokens);
