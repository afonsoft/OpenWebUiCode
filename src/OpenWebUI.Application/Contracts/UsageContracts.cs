namespace OpenWebUI.Application.Contracts;

/// <summary>Totais de uso de um chat (soma dos registros de run).</summary>
/// <param name="Runs">Quantidade de runs com uso registrado.</param>
/// <param name="PromptTokens">Tokens de entrada somados.</param>
/// <param name="CompletionTokens">Tokens de saída somados.</param>
/// <param name="ReasoningTokens">Tokens de raciocínio somados.</param>
/// <param name="CostUsd">Custo total em USD (só de runs com preço conhecido).</param>
public sealed record ChatUsageTotals(
    int Runs, long PromptTokens, long CompletionTokens,
    long ReasoningTokens, double CostUsd);

/// <summary>Uso agregado de um modelo dentro do chat.</summary>
/// <param name="ModelId">Modelo efetivo da run (null → "unknown").</param>
/// <param name="Runs">Quantidade de runs com o modelo.</param>
/// <param name="Tokens">Prompt + completion + reasoning.</param>
/// <param name="CostUsd">Custo somado das runs com o modelo.</param>
public sealed record ChatUsageModelRow(
    string ModelId, int Runs, long PromptTokens, long CompletionTokens,
    long ReasoningTokens, long Tokens, double CostUsd);

/// <summary>Registro de uma run do chat (ponto do gráfico de uso).</summary>
/// <param name="CreatedAt">Gravação (epoch seconds).</param>
public sealed record ChatUsageRunRow(
    string RunId, string? ModelId, int Steps, long PromptTokens,
    long CompletionTokens, long ReasoningTokens, double? CostUsd, long CreatedAt);

/// <summary>Uso por chat: totais, por modelo e série por run (RF info-panel).</summary>
public sealed record ChatUsageResponse(
    string ChatId, ChatUsageTotals Totals,
    IReadOnlyList<ChatUsageModelRow> Models,
    IReadOnlyList<ChatUsageRunRow> Runs);
