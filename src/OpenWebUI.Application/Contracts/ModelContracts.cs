namespace OpenWebUI.Application.Contracts;

/// <summary>Descrição de um modelo disponível para chat.</summary>
/// <param name="Id">Identificador usado nas requisições de completion.</param>
/// <param name="Name">Nome amigável exibido na UI.</param>
/// <param name="Provider">Provedor de origem: "ollama" ou "openai".</param>
/// <param name="OwnedBy">Dono reportado pelo provedor, quando disponível.</param>
/// <param name="Capabilities">Capacidades resolvidas (SPEC-20261013-model-roles):
/// <c>"vision"</c>, <c>"tools"</c>, <c>"thinking"</c>; null quando desconhecidas.</param>
public sealed record ModelInfo(
    string Id, string Name, string Provider, string? OwnedBy,
    IReadOnlyList<string>? Capabilities = null);

/// <summary>Lista agregada de modelos, no formato OpenAI (<c>{ data: [...] }</c>).</summary>
/// <param name="Data">Modelos de todos os provedores configurados.</param>
public sealed record ModelListResponse(IReadOnlyList<ModelInfo> Data);

/// <summary>Modelo do catálogo completo com a flag de visibilidade do
/// usuário chamador (GET /api/v1/models/all — aba Models das settings).</summary>
/// <param name="Enabled">False quando o usuário desabilitou <c>{provider}:{id}</c>.</param>
public sealed record CatalogModel(
    string Id, string Name, string Provider, string? OwnedBy, bool Enabled,
    IReadOnlyList<string>? Capabilities = null);

/// <summary>Resposta de GET /api/v1/models/all.</summary>
/// <param name="Data">Todos os modelos conhecidos, com flag por usuário.</param>
public sealed record CatalogModelListResponse(IReadOnlyList<CatalogModel> Data);

/// <summary>Papéis de modelo do usuário (SPEC-20261013-model-roles):
/// papel → cadeia de ids em ordem de fallback.</summary>
/// <param name="Roles">Mapa <c>{tasks|memory|advisor: [id,...]}</c> do usuário.</param>
/// <param name="Global">
/// Cadeias globais do admin (SPEC-20261014-config-layers RF-004) —
/// usadas quando o usuário não tem override do papel. A UI mostra como
/// provenance "herdado do admin".
/// </param>
public sealed record ModelRolesResponse(
    IReadOnlyDictionary<string, List<string>> Roles,
    IReadOnlyDictionary<string, List<string>>? Global = null);

/// <summary>Escrita dos papéis (PUT /api/v1/configs/user/model-roles):
/// substitui o mapa inteiro; cadeia vazia remove o papel.</summary>
/// <param name="Roles">Mapa papel → ids; null = limpa tudo.</param>
public sealed record UpdateModelRolesRequest(Dictionary<string, List<string>>? Roles);

/// <summary>
/// Imagem resolvida de um anexo para envio multimodal ao provider
/// (SPEC-20261013-chat-media-attachments RF-006) — serializada por provider:
/// Ollama <c>images[]</c>, OpenAI <c>image_url</c> data-url, Anthropic bloco
/// <c>image</c> base64.
/// </summary>
/// <param name="MimeType">MIME da imagem (image/*).</param>
/// <param name="Base64Data">Bytes do arquivo em base64.</param>
public sealed record ChatImageData(string MimeType, string Base64Data);

/// <summary>Mensagem enviada ao endpoint de chat completion.</summary>
/// <param name="Role">Papel: system, user ou assistant.</param>
/// <param name="Content">Conteúdo textual.</param>
/// <param name="FileIds">
/// Ids dos anexos desta mensagem (interno — resolução preguiçosa de
/// imagens na pipeline; vem de <c>ChatMessage.FilesJson</c> e não é
/// serializado ao provider).
/// </param>
/// <param name="Images">Imagens resolvidas pela pipeline para envio multimodal.</param>
public sealed record ChatCompletionMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    string? ToolCallsJson = null,
    IReadOnlyList<string>? FileIds = null,
    IReadOnlyList<ChatImageData>? Images = null);

/// <summary>Requisição de chat completion compatível com OpenAI.</summary>
/// <param name="Model">Identificador do modelo.</param>
/// <param name="Messages">Histórico de mensagens.</param>
/// <param name="Stream">Se a resposta deve ser transmitida via SSE.</param>
/// <param name="Connection">Provedor preferencial ("ollama"/"openai"), opcional.</param>
/// <param name="FileIds">Ids de arquivos cujo conteúdo entra como contexto (opcional).</param>
/// <param name="Params">Parâmetros de geração (temperature, top_p, max_tokens), opcional.</param>
public sealed record ChatCompletionRequest(
    string Model,
    IReadOnlyList<ChatCompletionMessage> Messages,
    bool Stream = true,
    string? Connection = null,
    IReadOnlyList<string>? FileIds = null,
    IReadOnlyDictionary<string, object>? Params = null,
    IReadOnlyList<string>? ToolIds = null,
    IReadOnlyList<System.Text.Json.JsonElement>? Tools = null,
    bool? WebSearch = null,
    /// <summary>Caminhos do workdir mencionados via chips @path (SPEC-20261009-ide-mentions-tests).</summary>
    IReadOnlyList<string>? MentionPaths = null);
