namespace OpenWebUI.Application.Contracts;

/// <summary>Versão anterior de uma mensagem (edição/regeneração preserva o conteúdo).</summary>
/// <param name="Content">Conteúdo da versão.</param>
/// <param name="Model">Modelo da versão (somente assistant).</param>
/// <param name="Timestamp">Instante da versão (epoch seconds).</param>
public sealed record ChatMessageVersionModel(string Content, string? Model, long Timestamp);

/// <summary>
/// Metadados de um anexo da mensagem (SPEC-20261013-chat-media-attachments
/// RF-003): nunca contém path de storage nem bytes — só o que a UI precisa
/// para renderizar a mídia via <c>GET /api/v1/files/{id}/content</c>.
/// </summary>
/// <param name="Id">Id do arquivo (<c>FileEntry</c>).</param>
/// <param name="Name">Nome original do arquivo.</param>
/// <param name="ContentType">MIME reportado no upload.</param>
/// <param name="Size">Tamanho em bytes.</param>
/// <param name="Type">Classe derivada do ContentType: <c>image</c> | <c>video</c> | <c>file</c>.</param>
public sealed record ChatMessageFileModel(
    string Id, string Name, string? ContentType, long Size, string Type)
{
    /// <summary>Deriva <paramref name="Type"/> do ContentType (image/* → image; video/* → video; resto → file).</summary>
    public static ChatMessageFileModel Of(
        string id, string name, string? contentType, long size) => new(
        id, name, contentType, size,
        contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
            ? "image"
            : contentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true
                ? "video"
                : "file");
}

/// <summary>Uma mensagem dentro de um chat (formato OpenAI simplificado).</summary>
/// <param name="Id">Identificador estável da mensagem.</param>
/// <param name="Role">Papel: system, user ou assistant.</param>
/// <param name="Content">Conteúdo textual (Markdown).</param>
/// <param name="Model">Modelo que gerou a mensagem (somente assistant).</param>
/// <param name="Timestamp">Instante de criação em UTC (epoch seconds).</param>
/// <param name="Versions">Versões anteriores da mensagem (edições/regenerações).</param>
/// <param name="Files">Anexos da mensagem (RF-003) — imagens/vídeos renderizam inline.</param>
public sealed record ChatMessageModel(
    string Id,
    string Role,
    string Content,
    string? Model,
    long Timestamp,
    IReadOnlyList<ChatMessageVersionModel>? Versions = null,
    string? ToolCallId = null,
    string? ToolCallsJson = null,
    IReadOnlyList<ChatMessageFileModel>? Files = null,
    string? ParentId = null);

/// <summary>Resumo de um chat para listagem na barra lateral.</summary>
/// <param name="Id">Identificador do chat.</param>
/// <param name="Title">Título exibido.</param>
/// <param name="Pinned">Se está fixado no topo.</param>
/// <param name="FolderId">Pasta que contém o chat, se houver.</param>
/// <param name="Tags">Tags associadas.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
/// <param name="Awaiting">Run mais recente bloqueada em aprovação/pergunta do dono.</param>
public sealed record ChatSummaryResponse(
    string Id,
    string Title,
    bool Pinned,
    string? FolderId,
    IReadOnlyList<string> Tags,
    long CreatedAt,
    long UpdatedAt,
    bool Awaiting = false,
    string? ParentChatId = null,
    int ChildrenCount = 0);

/// <summary>Resumo de um chat filho (delegado) para a árvore pai↔filho.</summary>
/// <param name="Id">Identificador do chat filho.</param>
/// <param name="Title">Título exibido.</param>
/// <param name="LastRunStatus">Status da run mais recente (null = sem runs).</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
public sealed record ChatChildSummaryResponse(
    string Id,
    string Title,
    string? LastRunStatus,
    long CreatedAt);

/// <summary>Binding workspace↔repo resolvido para um chat (SPEC-20261010-chat-repo-binding).</summary>
/// <param name="Binding">Repo efetivo (null quando nenhum binding em nenhum nível).</param>
/// <param name="Source">Origem do binding: <c>chat</c> (repo próprio), <c>user</c> (global do usuário) ou <c>none</c>.</param>
public sealed record ChatWorkspaceRepoResponse(
    WorkspaceRepoResponse? Binding,
    string Source);

/// <summary>Contagem de chats aguardando ação do usuário (D2 attention-inbox).</summary>
/// <param name="Count">Chats do usuário com aprovação/pergunta pendente.</param>
public sealed record AttentionCountResponse(int Count);

/// <summary>Chat completo com histórico linear de mensagens.</summary>
/// <param name="Id">Identificador do chat.</param>
/// <param name="Title">Título exibido.</param>
/// <param name="Models">Modelos associados ao chat.</param>
/// <param name="Messages">Mensagens em ordem cronológica.</param>
/// <param name="Pinned">Se está fixado.</param>
/// <param name="Archived">Se está arquivado.</param>
/// <param name="Tags">Tags associadas.</param>
/// <param name="FolderId">Pasta que contém o chat.</param>
/// <param name="ShareId">Id público de compartilhamento, quando ativo.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
public sealed record ChatResponse(
    string Id,
    string Title,
    IReadOnlyList<string> Models,
    IReadOnlyList<ChatMessageModel> Messages,
    bool Pinned,
    bool Archived,
    IReadOnlyList<string> Tags,
    string? FolderId,
    string? ShareId,
    IReadOnlyList<string> ToolIds,
    long CreatedAt,
    long UpdatedAt,
    string ApprovalPreset = "approve-mutations",
    string Mode = "chat",
    string? ParentChatId = null,
    string? ParentTitle = null,
    bool AdvisorEnabled = false,
    string? ActiveLeafId = null,
    int BranchCount = 0,
    bool MessagesHasMore = false);

/// <summary>Página de mensagens do caminho ativo (SPEC-20261019-chat-window-perf RF-001).</summary>
/// <param name="Messages">Janela em ordem cronológica (anterior a <c>before</c> ou a cauda).</param>
/// <param name="HasMore">Há mensagens mais antigas além da janela.</param>
/// <param name="Total">Total de mensagens no caminho ativo.</param>
public sealed record ChatMessagesPage(
    IReadOnlyList<ChatMessageModel> Messages,
    bool HasMore,
    int Total);

/// <summary>Requisição para criar ou atualizar um chat.</summary>
/// <param name="Title">Título do chat.</param>
/// <param name="Models">Modelos selecionados.</param>
/// <param name="Messages">Histórico completo de mensagens.</param>
/// <param name="ToolIds">Ids das tools habilitadas neste chat (opcional).</param>
public sealed record ChatUpsertRequest(
    string Title,
    IReadOnlyList<string> Models,
    IReadOnlyList<ChatMessageModel> Messages,
    IReadOnlyList<string>? ToolIds = null);

/// <summary>Atualização parcial de metadados do chat (título/tags/pasta).</summary>
/// <param name="Title">Novo título (opcional).</param>
/// <param name="Tags">Novas tags (opcional).</param>
/// <param name="FolderId">Nova pasta (opcional; string vazia remove da pasta).</param>
public sealed record ChatMetaUpdateRequest(
    string? Title,
    IReadOnlyList<string>? Tags,
    string? FolderId);

/// <summary>Atualização do conteúdo de uma mensagem existente.</summary>
/// <param name="Content">Novo conteúdo da mensagem.</param>
public sealed record MessageUpdateRequest(string Content);

/// <summary>Corpo de endpoints que retornam somente sucesso/estado.</summary>
/// <param name="Success">Indica se a operação foi concluída.</param>
public sealed record StatusResponse(bool Success);

/// <summary>Resumo de um chat para a lista administrativa (inclui o dono).</summary>
/// <param name="Id">Identificador do chat.</param>
/// <param name="Title">Título exibido.</param>
/// <param name="UserId">Id do dono do chat.</param>
/// <param name="UserName">Nome do dono.</param>
/// <param name="UserEmail">E-mail do dono.</param>
/// <param name="MessageCount">Total de mensagens.</param>
/// <param name="Archived">Se está arquivado.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="UpdatedAt">Última atualização (epoch seconds).</param>
public sealed record AdminChatSummaryResponse(
    string Id,
    string Title,
    string UserId,
    string UserName,
    string UserEmail,
    int MessageCount,
    bool Archived,
    long CreatedAt,
    long UpdatedAt);

/// <summary>
/// Leaf divergente de um chat (SPEC-20261013-chat-branching RF-003):
/// ponta de um ramo na árvore de mensagens.
/// </summary>
/// <param name="LeafId">Id da mensagem-leaf.</param>
/// <param name="Depth">Profundidade do ramo (arestas até a raiz).</param>
/// <param name="MessageCount">Mensagens no caminho raiz→leaf.</param>
/// <param name="Preview">Início do conteúdo do leaf.</param>
/// <param name="Summary">Resumo gerado ao abandonar o ramo (RF-005), quando há.</param>
/// <param name="UpdatedAt">Instante do leaf (epoch seconds).</param>
/// <param name="IsActive">Se é o leaf ativo do chat.</param>
public sealed record BranchLeafResponse(
    string LeafId,
    int Depth,
    int MessageCount,
    string Preview,
    string? Summary,
    long UpdatedAt,
    bool IsActive);

/// <summary>Pedido de ativação de leaf (POST /branches/activate, RF-004).</summary>
/// <param name="LeafId">Mensagem que vira a nova ponta do caminho ativo.</param>
public sealed record ActivateBranchRequest(string LeafId);

/// <summary>Página da listagem administrativa de chats.</summary>
/// <param name="Items">Chats da página.</param>
/// <param name="Total">Total de chats que satisfazem o filtro.</param>
public sealed record AdminChatListResponse(
    IReadOnlyList<AdminChatSummaryResponse> Items,
    int Total);

/// <summary>
/// Item do inbox de atenção (SPEC-20261022-ade-surfaces RF-C1). Itens
/// persistidos (<c>run:{id}</c>, <c>term:{id}</c>) convivem com itens
/// sintéticos de aprovação (<c>chat:{id}</c> — chat com run esperando
/// decisão), estes últimos sem <c>seen</c>: saem quando o usuário age.
/// </summary>
/// <param name="Key">Chave estável da origem.</param>
/// <param name="Kind"><c>run_completed</c> | <c>run_failed</c> |
/// <c>terminal</c> | <c>approval</c>.</param>
/// <param name="Title">Título curto exibido no inbox.</param>
/// <param name="ChatId">Chat relacionado, quando houver (deep-link).</param>
/// <param name="CreatedAt">Quando foi armado (epoch seconds; 0 nos itens
/// sintéticos de aprovação, que não têm timestamp persistido).</param>
public sealed record AttentionItemResponse(
    string Key,
    string Kind,
    string Title,
    string? ChatId,
    long CreatedAt);
