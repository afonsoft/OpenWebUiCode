namespace OpenWebUI.Application.Contracts;

/// <summary>Configuração de retrieval persistida (admin).</summary>
/// <param name="Engine">Engine de web search: none | auto | searxng | duckduckgo | tavily | brave | google_pse | jina | exa | kagi | perplexity. <c>auto</c> faz failover entre providers com credencial (SPEC-20261016-websearch-failover).</param>
/// <param name="SearxngBaseUrl">URL base do SearXNG (quando engine=searxng).</param>
/// <param name="TopK">Top-K do retrieval.</param>
/// <param name="ChunkSize">Tamanho do chunk.</param>
/// <param name="ChunkOverlap">Sobreposição entre chunks.</param>
/// <param name="Hybrid">Ativa busca híbrida BM25+vetorial.</param>
/// <param name="HybridWeight">Peso do componente vetorial (0..1).</param>
/// <param name="Rerank">Ativa reranking do top-2k.</param>
/// <param name="RerankEngine">Engine de rerank: local | external.</param>
public sealed record RetrievalConfig(
    string Engine,
    string? SearxngBaseUrl,
    string? BraveApiKey,
    string? TavilyApiKey,
    int TopK,
    int ChunkSize,
    int ChunkOverlap,
    bool Hybrid,
    double HybridWeight,
    bool Rerank,
    string? GooglePseApiKey = null,
    string? GooglePseEngineId = null,
    string? GooglePseBaseUrl = null,
    string? JinaApiKey = null,
    string? JinaBaseUrl = null,
    string? ExaApiKey = null,
    string? ExaBaseUrl = null,
    string? KagiApiKey = null,
    string? KagiBaseUrl = null,
    string? PerplexityApiKey = null,
    string? PerplexityBaseUrl = null,
    string RerankEngine = "local",
    string? RerankExternalUrl = null,
    string? RerankExternalApiKey = null)
{
    /// <summary>Config padrão — web search ligada via DuckDuckGo (não exige chave).</summary>
    public static readonly RetrievalConfig Default = new(
        "duckduckgo", null, null, null, 5, 1000, 100, false, 0.5, false);

    /// <summary>Mascara as chaves para respostas GET.</summary>
    public RetrievalConfig Masked() => this with
    {
        BraveApiKey = string.IsNullOrEmpty(BraveApiKey) ? null : "********",
        TavilyApiKey = string.IsNullOrEmpty(TavilyApiKey) ? null : "********",
        GooglePseApiKey = string.IsNullOrEmpty(GooglePseApiKey) ? null : "********",
        JinaApiKey = string.IsNullOrEmpty(JinaApiKey) ? null : "********",
        ExaApiKey = string.IsNullOrEmpty(ExaApiKey) ? null : "********",
        KagiApiKey = string.IsNullOrEmpty(KagiApiKey) ? null : "********",
        PerplexityApiKey = string.IsNullOrEmpty(PerplexityApiKey) ? null : "********",
        RerankExternalApiKey = string.IsNullOrEmpty(RerankExternalApiKey) ? null : "********",
    };
}

/// <summary>Processamento de URL pública.</summary>
public sealed record ProcessUrlRequest(string Url, string? CollectionId);

/// <summary>Processamento de texto direto.</summary>
public sealed record ProcessTextRequest(string Content, string? Name, string? CollectionId);

/// <summary>(Re)indexação de arquivo já enviado.</summary>
public sealed record ProcessFileRequest(string FileId, string? CollectionId);

/// <summary>Transcrição de vídeo do YouTube.</summary>
public sealed record ProcessYoutubeRequest(string Url, string? CollectionId);

/// <summary>Busca web via engine configurada.</summary>
/// <param name="ProcessResults">Quando true, indexa os snippets retornados.</param>
public sealed record WebSearchRequest(string Query, int? Count, bool ProcessResults);

/// <summary>Resultado de web search.</summary>
public sealed record WebSearchResult(string Title, string Url, string Snippet);

/// <summary>Resposta de processamento de fonte.</summary>
public sealed record ProcessResponse(string FileId, string Filename, int Chunks);
