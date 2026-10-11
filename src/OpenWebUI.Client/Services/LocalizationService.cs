using System.Globalization;
using System.Net.Http.Json;
using Microsoft.JSInterop;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Serviço de localização do cliente: carrega dicionários JSON de
/// <c>wwwroot/i18n/{lang}.json</c>, resolve chaves com fallback para pt-BR
/// e notifica componentes quando o idioma muda (sem reload).
/// </summary>
public sealed class LocalizationService(HttpClient http, IJSRuntime js)
{
    /// <summary>Idioma padrão e fallback de chaves ausentes.</summary>
    public const string DefaultLanguage = "pt-BR";

    /// <summary>Chave do localStorage onde o idioma escolhido persiste.</summary>
    public const string StorageKey = "webui.locale";

    /// <summary>Idiomas padrão caso o manifesto <c>i18n/locales.json</c> não carregue.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> DefaultLanguages =
    [
        ("pt-BR", "Português (Brasil)"),
        ("en-US", "English (US)"),
    ];

    /// <summary>Idiomas disponíveis, carregados do manifesto <c>i18n/locales.json</c>
    /// no boot (fallback para <see cref="DefaultLanguages"/>).</summary>
    public IReadOnlyList<(string Code, string Name)> Languages { get; private set; } =
        DefaultLanguages;

    private Dictionary<string, string> _active = new();
    private Dictionary<string, string> _fallbackEn = new();
    private Dictionary<string, string> _fallbackPt = new();

    /// <summary>Idioma atual (código, ex.: "pt-BR").</summary>
    public string Language { get; private set; } = DefaultLanguage;

    /// <summary>Cultura BCP-47 resolvida do idioma ativo — base para datas/números.</summary>
    public CultureInfo Culture => new(Language);

    /// <summary>Formata data e hora curtas na cultura do idioma ativo.</summary>
    public string FormatDateTime(DateTime value) => value.ToString("g", Culture);

    /// <summary>Formata apenas a data na cultura do idioma ativo.</summary>
    public string FormatDate(DateTime value) => value.ToString("d", Culture);

    /// <summary>Disparado quando o idioma muda — componentes devem re-renderizar.</summary>
    public event Action? Changed;

    /// <summary>Resolve uma chave para a string localizada.</summary>
    public string this[string key] => T(key);

    /// <summary>Resolve uma chave com cadeia de fallback:
    /// idioma ativo → en-US → pt-BR → a própria chave.</summary>
    public string T(string key) =>
        _active.TryGetValue(key, out var value)
        || _fallbackEn.TryGetValue(key, out value)
        || _fallbackPt.TryGetValue(key, out value)
            ? value
            : key;

    /// <summary>Resolve uma chave e interpola placeholders <c>{{nome}}</c>.</summary>
    public string T(string key, params (string Name, string Value)[] args)
    {
        var text = T(key);
        foreach (var (name, value) in args)
        {
            text = text.Replace("{{" + name + "}}", value, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Carrega o idioma persistido (ou padrão) — chamado antes do primeiro render.</summary>
    public async Task InitializeAsync()
    {
        _fallbackPt = await LoadAsync(DefaultLanguage);
        _fallbackEn = await LoadAsync("en-US");
        await LoadManifestAsync();
        string? stored = null;
        try
        {
            stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
        }
        catch (JSException)
        {
            // localStorage indisponível — segue com o padrão.
        }

        await ApplyAsync(stored);
    }

    /// <summary>Troca o idioma, persiste em localStorage e notifica a UI.</summary>
    public async Task SetLanguageAsync(string language)
    {
        await ApplyAsync(language);
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", StorageKey, Language);
        }
        catch (JSException)
        {
            // Sem persistência — troca ainda vale para a sessão.
        }

        Changed?.Invoke();
    }

    private async Task ApplyAsync(string? language)
    {
        var lang = Languages.Any(l => l.Code == language) ? language! : DefaultLanguage;
        _active = lang == DefaultLanguage ? _fallbackPt : await LoadAsync(lang);
        Language = lang;
        try
        {
            // Mantém <html lang> consistente com o idioma ativo (leitores de tela).
            await js.InvokeVoidAsync("openwebui.setLang", lang);
        }
        catch (JSException)
        {
            // Fetch/parse do locale falhou — mantém o fallback anterior.
        }
        catch (InvalidOperationException)
        {
            // JS ainda indisponível durante o prerender.
        }
    }

    /// <summary>Manifesto de locales: atualiza <see cref="Languages"/> dinamicamente;
    /// falha/JSON inválido mantém a lista padrão sem quebrar o boot.</summary>
    private async Task LoadManifestAsync()
    {
        try
        {
            var manifest = await http.GetFromJsonAsync<LocalesManifest>("i18n/locales.json");
            if (manifest?.Locales is { Count: > 0 } locales)
            {
                Languages = locales
                    .Select(l => (l.Code, l.Name))
                    .Where(l => !string.IsNullOrWhiteSpace(l.Code))
                    .ToList();
            }
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex))
        {
            // Locale do browser indisponível — usa fallback.
        }
    }

    private sealed record LocaleEntry(string Code, string Name);

    private sealed record LocalesManifest(List<LocaleEntry> Locales);

    private async Task<Dictionary<string, string>> LoadAsync(string language)
    {
        try
        {
            return await http.GetFromJsonAsync<Dictionary<string, string>>($"i18n/{language}.json")
                ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex))
        {
            return new Dictionary<string, string>();
        }
    }
}
