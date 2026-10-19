using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Janela deslizante de mensagens do chat (SPEC-20261019-chat-window-perf
/// RF-002/RF-003): mantém apenas a cauda recente renderizada — o resto é
/// re-buscado no servidor conforme o scroll sobe.
/// </summary>
public static class ChatWindow
{
    /// <summary>Mensagens da cauda inicial carregadas ao abrir o chat.</summary>
    public const int InitialTail = 60;

    /// <summary>Tamanho da página buscada a cada sentinel de scroll-up.</summary>
    public const int OlderPageSize = 50;

    /// <summary>Teto de caracteres de conteúdo mantidos na janela ativa.</summary>
    public const int CharCap = 256_000;

    /// <summary>Descarta o topo de <paramref name="messages"/> deixando a cauda de
    /// <paramref name="tail"/> itens. Retorna quantos foram descartados. Nunca abre
    /// a janela no meio de um grupo consecutivo de <c>tool</c> — o feed agrupado
    /// não pode nascer cortado.</summary>
    public static int TrimToTail(List<ChatMessageModel> messages, int tail)
    {
        if (tail <= 0 || messages.Count <= tail)
        {
            return 0;
        }

        var start = messages.Count - tail;
        while (start > 0 && messages[start].Role == "tool" && messages[start - 1].Role == "tool")
        {
            start--;
        }
        messages.RemoveRange(0, start);
        return start;
    }

    /// <summary>Descarta o topo até a janela caber em <paramref name="cap"/>
    /// caracteres de conteúdo. Retorna quantos foram descartados. O primeiro
    /// item remanescente nunca é <c>tool</c> (meio de feed agrupado).</summary>
    public static int TrimToCharCap(List<ChatMessageModel> messages, int cap)
    {
        long total = 0;
        var cut = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            total += messages[i].Content?.Length ?? 0;
            if (total > cap)
            {
                cut = i + 1;
                break;
            }
        }
        if (cut <= 0 || cut >= messages.Count)
        {
            return 0;
        }

        while (cut < messages.Count - 1 && messages[cut].Role == "tool")
        {
            cut++;
        }
        messages.RemoveRange(0, cut);
        return cut;
    }
}
