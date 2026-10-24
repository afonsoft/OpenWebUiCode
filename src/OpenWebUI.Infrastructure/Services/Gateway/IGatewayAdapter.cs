using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.Services.Gateway;

/// <summary>Mensagem normalizada de um update inbound da plataforma.</summary>
/// <param name="ExternalChatId">Chat/conversa na plataforma (ex.: chat.id).</param>
/// <param name="ExternalUserId">Usuário remetente na plataforma (ex.: from.id).</param>
/// <param name="ExternalName">Nome/username de exibição, quando presente.</param>
/// <param name="Text">Texto da mensagem (v1: texto; anexos viram link).</param>
/// <param name="MessageId">Id da mensagem/update na plataforma (dedup/auditoria).</param>
public sealed record GatewayInboundMessage(
    string ExternalChatId, string ExternalUserId, string? ExternalName,
    string Text, string? MessageId);

/// <summary>Resultado de um envio para a plataforma.</summary>
/// <param name="Ok">True quando todos os chunks foram aceitos.</param>
/// <param name="Error">Erro resumido (≤200 chars), quando falho.</param>
public sealed record GatewaySendResult(bool Ok, string? Error);

/// <summary>
/// Contrato de adapter de plataforma do messaging gateway
/// (SPEC-20261023-messaging-gateway A): verificação de assinatura,
/// parse do update inbound e envio de resposta. v1 implementa
/// <c>telegram</c>; Slack/Discord seguem o mesmo contrato.
/// </summary>
public interface IGatewayAdapter
{
    /// <summary>Plataforma atendida (<see cref="GatewayPlatforms"/>).</summary>
    string Platform { get; }

    /// <summary>Header HTTP que carrega o segredo de verificação.</summary>
    string SignatureHeader { get; }

    /// <summary>
    /// Valida a assinatura/segredo da chamada inbound — executada antes de
    /// qualquer processamento (RF-001). Comparação em tempo constante.
    /// </summary>
    /// <param name="channel">Canal resolvido pelo token do endpoint.</param>
    /// <param name="headerValue">Valor do header <see cref="SignatureHeader"/>.</param>
    /// <param name="rawBody">Corpo cru (para plataformas com HMAC do corpo).</param>
    bool VerifySignature(GatewayChannel channel, string? headerValue, string rawBody);

    /// <summary>
    /// Traduz o corpo do update da plataforma em
    /// <see cref="GatewayInboundMessage"/>; null = update não é mensagem de
    /// texto de DM (filtrado, sem side-effect).
    /// </summary>
    GatewayInboundMessage? ParseInbound(string rawBody);

    /// <summary>
    /// Envia texto ao peer pela API da plataforma — chunked no limite dela
    /// (Telegram ≤4000) com fallback para texto puro quando o markup falha.
    /// </summary>
    Task<GatewaySendResult> SendReplyAsync(
        GatewayChannel channel, GatewayPeer peer, string text,
        string botToken, CancellationToken ct);
}
