using Microsoft.Playwright;

namespace OpenWebUI.Infrastructure.ChatTools.Browser;

/// <summary>
/// Contrato das sessões de browser por chat consumido pelas tools
/// (SPEC-20261017-browser-visual-reasoning). A interface existe para
/// testabilidade: as tools são exercitadas com <see cref="IPage"/> mockado
/// sem lançar Chromium real.
/// </summary>
public interface IBrowserSessions
{
    /// <summary>Máximo de ações (navigate/act) por run.</summary>
    int MaxActionsPerRun { get; }

    /// <summary>OCR habilitado (<c>BrowserTools:Ocr</c>).</summary>
    bool OcrEnabled { get; }

    /// <summary>Consome uma ação do budget da run; false quando esgotado.</summary>
    bool TryConsumeAction(string runKey);

    /// <summary>Página viva do chat (cria contexto isolado sob demanda).</summary>
    Task<IPage> GetPageAsync(string chatKey, CancellationToken ct);

    /// <summary>Fecha e descarta o contexto do chat.</summary>
    Task CloseChatAsync(string chatKey);

    /// <summary>Descarta o contexto quebrado do chat — a próxima chamada recria.</summary>
    Task ResetChatAsync(string chatKey);
}
