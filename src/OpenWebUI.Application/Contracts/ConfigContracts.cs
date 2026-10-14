namespace OpenWebUI.Application.Contracts;

/// <summary>Tipos de provedor reconhecidos nas conexões tipadas.</summary>
public static class ProviderTypes
{
    /// <summary>Ollama local/remoto.</summary>
    public const string Ollama = "ollama";

    /// <summary>API compatível com OpenAI.</summary>
    public const string OpenAi = "openai";

    /// <summary>API nativa da Anthropic (Messages API).</summary>
    public const string Anthropic = "anthropic";

    /// <summary>Google AI Studio (Gemini API).</summary>
    public const string Google = "google";

    /// <summary>Todos os tipos aceitos no campo <see cref="ProviderConnection.Type"/>.</summary>
    public static readonly IReadOnlyList<string> All = [Ollama, OpenAi, Anthropic, Google];

    /// <summary>Tipos que exigem API key (o Ollama não usa e o OpenAI é opcional).</summary>
    public static bool KeyRequired(string type) =>
        type is Anthropic or Google;

    /// <summary>Endpoint oficial pré-preenchido por tipo (editável para proxies/gateways).</summary>
    public static string DefaultBaseUrl(string type) => type switch
    {
        Anthropic => "https://api.anthropic.com",
        Google => "https://generativelanguage.googleapis.com/v1beta",
        OpenAi => "https://api.openai.com/v1",
        Ollama => "http://localhost:11434",
        _ => string.Empty,
    };
}

/// <summary>Conexão tipada de provider (Anthropic, Google AI Studio, …).</summary>
/// <param name="Type">Tipo do provedor — ver <see cref="ProviderTypes"/>.</param>
/// <param name="BaseUrl">URL base do endpoint (oficial pré-preenchida, editável).</param>
/// <param name="ApiKey">Chave de API da conexão (obrigatória para anthropic/google).</param>
/// <param name="Name">Nome de exibição opcional.</param>
public sealed record ProviderConnection(
    string Type,
    string BaseUrl,
    string? ApiKey = null,
    string? Name = null);

/// <summary>Visão pública de uma conexão tipada: chave nunca é exposta.</summary>
/// <param name="Type">Tipo do provedor.</param>
/// <param name="BaseUrl">URL base configurada.</param>
/// <param name="KeyConfigured">Indica se existe chave configurada.</param>
/// <param name="Name">Nome de exibição opcional.</param>
public sealed record ProviderConnectionResponse(
    string Type,
    string BaseUrl,
    bool KeyConfigured,
    string? Name = null);

/// <summary>Configuração de conexões com provedores de IA (espelha Admin Settings → Connections).</summary>
/// <param name="OllamaBaseUrls">URLs base de servidores Ollama.</param>
/// <param name="OpenAiBaseUrls">URLs base de APIs compatíveis com OpenAI.</param>
/// <param name="OpenAiApiKeys">Chaves de API correspondentes às URLs OpenAI.</param>
/// <param name="OllamaNames">Nomes de exibição das conexões Ollama (opcional).</param>
/// <param name="OpenAiNames">Nomes de exibição das conexões OpenAI (opcional).</param>
/// <param name="Providers">Conexões tipadas adicionais (anthropic, google, …).</param>
public sealed record ConnectionsConfig(
    IReadOnlyList<string> OllamaBaseUrls,
    IReadOnlyList<string> OpenAiBaseUrls,
    IReadOnlyList<string> OpenAiApiKeys,
    IReadOnlyList<string>? OllamaNames = null,
    IReadOnlyList<string>? OpenAiNames = null,
    IReadOnlyList<ProviderConnection>? Providers = null)
{
    /// <summary>Configuração padrão apontando para um Ollama local.</summary>
    public static ConnectionsConfig Default { get; } = new(
        OllamaBaseUrls: ["http://localhost:11434"],
        OpenAiBaseUrls: [],
        OpenAiApiKeys: []);

    /// <summary>Conexões tipadas (nunca null — lista vazia quando ausente).</summary>
    public IReadOnlyList<ProviderConnection> ProvidersOrEmpty =>
        Providers ?? (IReadOnlyList<ProviderConnection>)[];
}

/// <summary>Visão pública das conexões: chaves de API são omitidas.</summary>
/// <param name="OllamaBaseUrls">URLs base de servidores Ollama.</param>
/// <param name="OpenAiBaseUrls">URLs base de APIs compatíveis com OpenAI.</param>
/// <param name="OpenAiKeyConfigured">Indica se existe chave configurada por URL.</param>
/// <param name="Providers">Conexões tipadas sem as chaves.</param>
public sealed record ConnectionsConfigResponse(
    IReadOnlyList<string> OllamaBaseUrls,
    IReadOnlyList<string> OpenAiBaseUrls,
    IReadOnlyList<bool> OpenAiKeyConfigured,
    IReadOnlyList<string>? OllamaNames = null,
    IReadOnlyList<string>? OpenAiNames = null,
    IReadOnlyList<ProviderConnectionResponse>? Providers = null);

/// <summary>
/// Modelos detectados por capacidade nas conexões cadastradas — alimenta os
/// combos de configuração (imagens, vídeo, TTS/STT, embeddings).
/// </summary>
/// <param name="Image">Candidatos a geração de imagem.</param>
/// <param name="Video">Candidatos a geração de vídeo.</param>
/// <param name="Tts">Candidatos a síntese de voz.</param>
/// <param name="Stt">Candidatos a transcrição.</param>
/// <param name="Embed">Candidatos a embeddings (RAG).</param>
/// <param name="BaseUrl">Base URL da conexão analisada.</param>
/// <param name="DetectedAt">Epoch seconds da última detecção.</param>
public sealed record DetectedCapabilities(
    IReadOnlyList<string> Image,
    IReadOnlyList<string> Video,
    IReadOnlyList<string> Tts,
    IReadOnlyList<string> Stt,
    IReadOnlyList<string> Embed,
    string? BaseUrl,
    long DetectedAt);

/// <summary>Resposta de versão da API.</summary>
/// <param name="Version">Versão do backend .NET.</param>
public sealed record VersionResponse(string Version);

/// <summary>Resposta de verificação de atualizações.</summary>
/// <param name="Current">Versão atual.</param>
/// <param name="Latest">Última versão conhecida.</param>
public sealed record VersionUpdateResponse(string Current, string Latest);

/// <summary>Configuração pública da aplicação consumida pelo frontend antes do login.</summary>
/// <param name="Status">Indica que o backend está no ar.</param>
/// <param name="Name">Nome da instância.</param>
/// <param name="Version">Versão do backend.</param>
/// <param name="DefaultLocale">Localidade padrão.</param>
/// <param name="Features">Flags de funcionalidades habilitadas.</param>
/// <param name="DefaultPromptSuggestions">Sugestões de prompt para chat vazio.</param>
public sealed record AppConfigResponse(
    bool Status,
    string Name,
    string Version,
    string DefaultLocale,
    AppFeatures Features,
    IReadOnlyList<string> DefaultModels,
    IReadOnlyList<PromptSuggestion> DefaultPromptSuggestions,
    IReadOnlyList<string> OAuthProviders);

/// <summary>Flags de funcionalidades expostas pelo /api/config.</summary>
/// <param name="Auth">Se autenticação está ativa.</param>
/// <param name="EnableSignup">Cadastro aberto.</param>
/// <param name="EnableLoginForm">Formulário de login habilitado.</param>
/// <param name="EnableApiKeys">Chaves de API habilitadas.</param>
/// <param name="EnableMessageRating">Avaliação de respostas habilitada.</param>
/// <param name="EnableFolders">Pastas habilitadas.</param>
/// <param name="EnableMemories">Memórias habilitadas.</param>
/// <param name="EnableNotes">Notas habilitadas.</param>
/// <param name="EnableChannels">Canais habilitados (não implementado).</param>
/// <param name="EnableWebSearch">Busca web habilitada (engine de retrieval configurada).</param>
/// <param name="EnableImageGeneration">Geração de imagens habilitada.</param>
/// <param name="EnableVideoGeneration">Geração de vídeo habilitada.</param>
/// <param name="EnableCodeExecution">Execução de código habilitada (não implementado).</param>
/// <param name="EnableCommunitySharing">Compartilhamento comunitário habilitado.</param>
public sealed record AppFeatures(
    bool Auth,
    bool EnableSignup,
    bool EnableLoginForm,
    bool EnableApiKeys,
    bool EnableMessageRating,
    bool EnableFolders,
    bool EnableMemories,
    bool EnableNotes,
    bool EnableChannels,
    bool EnableWebSearch,
    bool EnableImageGeneration,
    bool EnableCodeExecution,
    bool EnableCommunitySharing,
    bool EnableVideoGeneration = false,
    bool EnableTextToSpeech = false,
    bool EnableSpeechToText = false);

/// <summary>Sugestão de prompt exibida no estado vazio do chat.</summary>
/// <param name="Title">Rótulo da sugestão.</param>
/// <param name="Content">Texto inserido no input ao clicar.</param>
public sealed class PromptSuggestion
{
    /// <summary>Cria uma sugestão vazia (para binding).</summary>
    public PromptSuggestion() { }

    /// <summary>Cria uma sugestão com título e conteúdo.</summary>
    public PromptSuggestion(string title, string content)
    {
        Title = title;
        Content = content;
    }

    /// <summary>Rótulo da sugestão.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Texto inserido no input ao clicar.</summary>
    public string Content { get; set; } = string.Empty;
}

/// <summary>Configuração de modelos padrão e sugestões de prompt.</summary>
/// <param name="DefaultModels">Ids de modelos selecionados por padrão (ordenados).</param>
/// <param name="PromptSuggestions">Sugestões exibidas no chat vazio.</param>
public sealed record ModelsConfig(
    List<string> DefaultModels,
    List<PromptSuggestion> PromptSuggestions)
{
    /// <summary>Configuração vazia (sem defaults).</summary>
    public static readonly ModelsConfig Empty = new([], []);
}

/// <summary>Resposta de um banner.</summary>
public sealed record BannerResponse(
    string Id, string Type, string Title, string Content, bool Dismissible, long Timestamp);

/// <summary>Criação/atualização de banner.</summary>
/// <param name="Type">info | warning | error | success.</param>
/// <param name="Title">Título.</param>
/// <param name="Content">Conteúdo.</param>
/// <param name="Dismissible">Se pode ser dispensado (default true).</param>
public sealed record BannerRequest(string Type, string Title, string Content, bool? Dismissible = null);

/// <summary>Toggle booleano genérico de feature.</summary>
/// <param name="Enabled">Se a feature está habilitada.</param>
public sealed record FeatureToggle(bool Enabled);

/// <summary>Visão das chaves de provider do usuário (BYOK, SPEC-20261014-config-layers RF-005).</summary>
/// <param name="Enabled">Se o admin habilitou chaves por usuário (<c>features.userProviderKeys</c>).</param>
/// <param name="Providers">Provedores com chave configurada — valores nunca retornam.</param>
public sealed record UserProviderKeysResponse(bool Enabled, IReadOnlyList<string> Providers);

/// <summary>Grava a chave BYOK do usuário para um provider.</summary>
/// <param name="Key">Chave de API (obrigatória; nunca volta em GET).</param>
public sealed record UserProviderKeyRequest(string? Key);

/// <summary>
/// Preferências de auto-sync de memória (SPEC-20261014-memory-pipeline
/// RF-004). Null em PUT remove o override da camada (volta a herdar).
/// </summary>
/// <param name="Enabled">Liga/desliga consolidação e injeção do usuário.</param>
/// <param name="IntervalHours">Cadência entre syncs (1–168h).</param>
/// <param name="MaxAgeDays">Janela de chats elegíveis (1–365d).</param>
public sealed record MemorySyncPrefs(bool? Enabled, int? IntervalHours, int? MaxAgeDays);

/// <summary>GET /configs/user/memory-sync — valores efetivos + camadas cruas.</summary>
/// <param name="Enabled">Efetivo (já dobra a negativa absoluta do admin).</param>
/// <param name="IntervalHours">Cadência efetiva em horas.</param>
/// <param name="MaxAgeDays">Janela efetiva em dias.</param>
/// <param name="AdminDisabled">Admin desligou globalmente — override do usuário não reabilita.</param>
/// <param name="Global">Valores crus da camada global (null = sem override admin).</param>
/// <param name="User">Valores crus do override do usuário (null = herda).</param>
public sealed record MemorySyncResponse(
    bool Enabled, int IntervalHours, int MaxAgeDays,
    bool AdminDisabled, MemorySyncPrefs Global, MemorySyncPrefs User);

/// <summary>Configuração de cadastro.</summary>
/// <param name="EnableSignup">Cadastro aberto.</param>
/// <param name="DefaultUserRole">Papel de novos usuários (pending/user/admin).</param>
public sealed record SignupConfig(bool EnableSignup, string DefaultUserRole);

/// <summary>Configuração de execução de código.</summary>
/// <param name="Engines">Engines habilitados (pyodide, jupyter).</param>
/// <param name="DirectConnections">Se conexões diretas do cliente estão habilitadas.</param>
public sealed record CodeExecutionConfig(List<string> Engines, bool DirectConnections);

/// <summary>Configuração de expiração do JWT.</summary>
/// <param name="ExpiresIn">Duração textual (ex.: "7d", "24h", "30m") ou segundos.</param>
public sealed record JwtExpiryConfig(string ExpiresIn);

/// <summary>Configuração de rate limiting e lockout de login.</summary>
/// <param name="Enabled">Se o limite de requisições por usuário está ativo.</param>
/// <param name="PermitLimit">Requisições permitidas por janela em endpoints limitados.</param>
/// <param name="WindowSeconds">Tamanho da janela deslizante em segundos.</param>
/// <param name="LoginMaxFailures">Falhas de login antes do bloqueio.</param>
/// <param name="LoginLockoutSeconds">Duração do bloqueio de login em segundos.</param>
public sealed record RateLimitConfig(
    bool Enabled,
    int PermitLimit,
    int WindowSeconds,
    int LoginMaxFailures,
    int LoginLockoutSeconds)
{
    /// <summary>Defaults: limite desligado (compat), 60 req/min, 5 falhas → 5 min de bloqueio.</summary>
    public static readonly RateLimitConfig Default = new(false, 60, 60, 5, 300);
}

/// <summary>Pedido de reset do bloqueio de login de um e-mail (admin).</summary>
/// <param name="Email">E-mail cujo lockout deve ser limpo.</param>
public sealed record LoginLockoutResetRequest(string Email);
