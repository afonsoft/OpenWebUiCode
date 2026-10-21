namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Estimativa de tokens por heurística (SPEC-20261020-context-snapshots
/// RF-001): <c>ceil(chars/4)</c>, compartilhada entre a compactação
/// automática e os snapshots de contexto. Não é contagem real de tokens —
/// é a aproximação conservadora já usada para limites de janela.
/// </summary>
internal static class ContextTokens
{
    /// <summary>Estima tokens a partir do total de caracteres.</summary>
    public static int Estimate(long chars) => (int)((chars + 3) / 4);
}
