using System.Text.Json;

namespace OpenWebUI.Infrastructure.ChatTools.Browser;

/// <summary>Elemento interativo indexado da observação (SPEC RF-003).</summary>
public sealed record BrowserElement(
    int Index, string Tag, string Role, string Name, string Href, string Type);

/// <summary>
/// Snapshot de elementos interativos + healing de seletor
/// (SPEC-20261017-browser-visual-reasoning RF-003/RF-004). O script JS carimba
/// <c>data-owui-el="{i}"</c> nos elementos visíveis — <c>browser_act</c> usa
/// o índice em vez de seletor CSS, e o atributo deixa o alvo estável entre
/// observação e ação.
/// </summary>
public static class BrowserElements
{
    /// <summary>Máximo de elementos listados na observação.</summary>
    public const int MaxElements = 120;

    /// <summary>CSS que localiza o elemento estampado pelo índice.</summary>
    public static string SelectorForIndex(int index) => $"[data-owui-el=\"{index}\"]";

    /// <summary>
    /// Script de extração: filtra interativos visíveis, estampa
    /// <c>data-owui-el</c> e devolve tag/role/name/href/type por índice.
    /// </summary>
    public const string ExtractScript = """
        (max) => {
          document.querySelectorAll('[data-owui-el]').forEach(e => e.removeAttribute('data-owui-el'));
          const sel = 'a[href],button,input,select,textarea,summary,' +
                      '[role="button"],[role="link"],[role="textbox"],[role="checkbox"],' +
                      '[role="tab"],[role="menuitem"],[onclick],[tabindex]';
          const els = [...document.querySelectorAll(sel)].filter(e => {
            const r = e.getBoundingClientRect();
            const s = getComputedStyle(e);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none';
          });
          return els.slice(0, max).map((e, i) => {
            e.setAttribute('data-owui-el', String(i));
            const name = (e.getAttribute('aria-label') || e.innerText ||
                          e.getAttribute('placeholder') || e.getAttribute('title') ||
                          e.value || '').trim().slice(0, 80);
            return {
              i, tag: e.tagName.toLowerCase(),
              role: e.getAttribute('role') || '',
              name,
              href: e.getAttribute('href') || '',
              type: e.getAttribute('type') || '',
            };
          });
        }
        """;

    /// <summary>Parse do resultado do <see cref="ExtractScript"/>.</summary>
    public static List<BrowserElement> Parse(JsonElement json)
    {
        var list = new List<BrowserElement>();
        if (json.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (var item in json.EnumerateArray())
        {
            list.Add(new BrowserElement(
                item.TryGetProperty("i", out var i) ? i.GetInt32() : list.Count,
                item.TryGetProperty("tag", out var t) ? t.GetString() ?? "" : "",
                item.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "",
                item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                item.TryGetProperty("href", out var h) ? h.GetString() ?? "" : "",
                item.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : ""));
        }
        return list;
    }

    /// <summary>
    /// Lista compacta para o modelo: <c>[3] button "Login"</c>.
    /// </summary>
    public static string Format(IReadOnlyList<BrowserElement> elements) =>
        elements.Count == 0
            ? "(nenhum elemento interativo visível)"
            : string.Join(" ", elements.Select(e =>
                $"[{e.Index}] {e.Tag}{(e.Role.Length > 0 ? "/" + e.Role : "")} \"{e.Name}\""));

    /// <summary>
    /// Rankeia candidatos para o self-healing (RF-004c): overlap de tokens
    /// entre a query (texto/seletor falho) e nome/tag/href do elemento.
    /// </summary>
    public static List<BrowserElement> Rank(
        IReadOnlyList<BrowserElement> candidates, string? query, int take = 5)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0)
        {
            return [];
        }
        return candidates
            .Select(e => (e, Score(e, tokens)))
            .Where(x => x.Item2 > 0)
            .OrderByDescending(x => x.Item2)
            .ThenBy(x => x.e.Index)
            .Take(take)
            .Select(x => x.e)
            .ToList();
    }

    private static int Score(BrowserElement e, HashSet<string> tokens)
    {
        var hay = Tokenize($"{e.Name} {e.Tag} {e.Role} {e.Href}");
        var score = 0;
        foreach (var t in tokens)
        {
            if (hay.Contains(t))
            {
                score += 2;
            }
            else if (hay.Any(h => h.StartsWith(t, StringComparison.Ordinal)
                               || t.StartsWith(h, StringComparison.Ordinal)))
            {
                score += 1;
            }
        }
        return score;
    }

    private static HashSet<string> Tokenize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.ToLowerInvariant()
                  .Split([' ', '\t', '\n', '.', ',', ':', ';', '/', '#', '[', ']', '"', '\''],
                      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Where(t => t.Length > 1)
                  .ToHashSet(StringComparer.Ordinal);
}
