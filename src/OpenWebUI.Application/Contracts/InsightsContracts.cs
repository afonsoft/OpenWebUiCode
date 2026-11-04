namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Contratos do Session Insights v2 (SPEC-20261104-session-insights-v2):
/// classificação heurística por chat, issue timeline e análise LLM sob demanda.
/// </summary>

/// <summary>Issue agregada detectada nas runs do chat.</summary>
public sealed record InsightIssue(
    /// <summary>Rótulo estável (ex.: <c>provider_retry</c>, <c>tool_failure</c>).</summary>
    string Label,
    /// <summary><c>high</c> | <c>medium</c> | <c>low</c>.</summary>
    string Impact,
    /// <summary>Descrição legível da ocorrência.</summary>
    string Description,
    /// <summary>Quantas vezes o sinal apareceu.</summary>
    int Count);

/// <summary>Evento da timeline cor-codificada do chat.</summary>
public sealed record InsightTimelineEvent(
    /// <summary>Epoch seconds do evento.</summary>
    long At,
    /// <summary><c>error</c> (vermelho) | <c>warn</c> (amarelo) |
    /// <c>event</c> (cinza) | <c>value</c> (verde).</summary>
    string Kind,
    /// <summary>Título curto do evento.</summary>
    string Title,
    /// <summary>Referência (runId / tool name), se houver.</summary>
    string? Ref);

/// <summary>Métricas agregadas snapshot do chat.</summary>
public sealed record InsightMetrics(
    long TokensIn,
    long TokensOut,
    decimal CostUsd,
    int Runs,
    int UserMessages);

/// <summary>Item de mudança sugerida pela análise.</summary>
public sealed record InsightChange(string What, string Why);

/// <summary>Action item acionável da análise.</summary>
public sealed record InsightActionItem(
    /// <summary><c>machine</c> | <c>repo</c> | <c>skill</c> | <c>knowledge</c>.</summary>
    string Kind,
    string Text);

/// <summary>Análise gerada por LLM e persistida por chat.</summary>
public sealed record InsightAnalysis(
    string Summary,
    List<string> Issues,
    string? ImprovedPrompt,
    List<InsightChange> Changes,
    List<InsightActionItem> ActionItems,
    List<string> KnowledgeUsed,
    string? ModelId,
    long GeneratedAt);

/// <summary>Resposta do GET /chats/{id}/insights.</summary>
public sealed record ChatInsightResponse(
    string SizeClass,
    string Category,
    InsightMetrics Metrics,
    List<InsightIssue> Issues,
    List<InsightTimelineEvent> Timeline,
    InsightAnalysis? Analysis);

/// <summary>Agregados do admin (GET /admin/insights/summary).</summary>
public sealed record AdminInsightsSummary(
    /// <summary>Distribuição por classe de tamanho <c>{XS:n,…}</c>.</summary>
    Dictionary<string, int> SizeDistribution,
    /// <summary>Breakdown por categoria.</summary>
    List<CategoryCount> ByCategory,
    /// <summary>Top-5 labels de issue agregadas.</summary>
    List<IssueCount> TopIssues,
    /// <summary>Chats unhealthy (L/XL) no período.</summary>
    int UnhealthyChats);

/// <summary>Contagem por categoria.</summary>
public sealed record CategoryCount(string Category, int Count);

/// <summary>Contagem por label de issue.</summary>
public sealed record IssueCount(string Label, string Impact, int Count);
