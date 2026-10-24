namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Contratos do messaging gateway (SPEC-20261023-messaging-gateway) —
/// surface do client (aba Settings → Integrations → Gateway).
/// </summary>

/// <summary>Canal de gateway do usuário (token do bot nunca volta).</summary>
public sealed record GatewayChannelResponse(
    string Id, string Platform, string SecretRef, string WebhookSecret,
    string ToolsPolicy, string? DefaultModelId, string? PersonaId,
    bool Enabled, long CreatedAt, string WebhookUrl, string? SetWebhookHint);

/// <summary>Payload de criação de canal (botToken vai para o cofre).</summary>
public sealed record GatewayChannelCreatePayload(
    string Platform, string BotToken, string? ToolsPolicy,
    string? DefaultModelId, string? PersonaId);

/// <summary>Payload de atualização de canal.</summary>
public sealed record GatewayChannelUpdatePayload(
    bool? Enabled, string? ToolsPolicy, string? DefaultModelId,
    string? PersonaId, string? BotToken, bool? RotateWebhookSecret);

/// <summary>Peer externo visto num canal do dono.</summary>
public sealed record GatewayPeerResponse(
    string Id, string ChannelId, string ExternalUserId, string ExternalChatId,
    string? ExternalName, bool Paired, long? PairedAt, bool Blocked,
    long CreatedAt);

/// <summary>Payload de atualização de peer (block/unpair).</summary>
public sealed record GatewayPeerUpdatePayload(bool? Blocked, bool? Unpair);

/// <summary>Payload do pareamento por código.</summary>
public sealed record GatewayPairPayload(string Code);

/// <summary>Resposta do pareamento bem-sucedido.</summary>
public sealed record GatewayPairResponse(bool Paired, string Channel);

/// <summary>Delivery de auditoria do gateway (in/out).</summary>
public sealed record GatewayDeliveryResponse(
    string Id, string ChannelId, string? PeerId, string Direction,
    string Status, string? MessageId, string? RunId, string? Excerpt,
    string? Error, long CreatedAt);
