using System.Net.Http.Json;
using Microsoft.JSInterop;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Gerencia o tema aplicado ao documento: modo (system/light/dark) e,
/// desde SPEC-20261018-themes-custom, a paleta de tokens (builtin, instância
/// via WEBUI_THEME_JSON ou JSON enviado pelo usuário). O CSS das variáveis é
/// injetado em <c>&lt;style id="user-theme"&gt;</c> via <c>openwebui.setCustomTheme</c>.
/// </summary>
public class ThemeService(
    IJSRuntime js,
    BrowserStorage storage,
    ApiService? api = null,
    HttpClient? http = null,
    AuthService? auth = null)
{
    private const string ThemeKey = "webui.theme";
    private const string ThemeIdKey = "webui.theme.id"; // cache escrito pelo boot em app.js
    private const string LightTheme = "light";
    private const string DefaultTheme = "default";

    private bool _serverSynced;

    /// <summary>Modo atual: "system", "dark" ou LightTheme (default claro).</summary>
    public string Current { get; private set; } = LightTheme;

    /// <summary>Paleta ativa (DefaultTheme, "midnight", … ou id de tema custom).</summary>
    public string ThemeId { get; private set; } = DefaultTheme;

    /// <summary>Presets builtin carregados de <c>wwwroot/themes/builtin.json</c>.</summary>
    public List<CustomThemeDefinition> Presets { get; private set; } = [];

    /// <summary>Tema de instância (WEBUI_THEME_JSON), quando configurado.</summary>
    public CustomThemeDefinition? Instance { get; private set; }

    /// <summary>Temas customizados enviados pelo usuário autenticado.</summary>
    public List<UserThemeResponse> Customs { get; private set; } = [];

    /// <summary>Disparado quando o tema muda.</summary>
    public event Action? Changed;

    /// <summary>Carrega modo/paleta persistidos, presets builtin e aplica ao documento.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var saved = await storage.GetAsync(ThemeKey);
            Current = saved is LightTheme or "dark" or "system" ? saved : LightTheme;
            var savedId = await storage.GetAsync(ThemeIdKey);
            ThemeId = ThemeValidator.IsValidThemeId(savedId) ? savedId! : DefaultTheme;
            await ApplyModeAsync();
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex))
        {
            // Storage/interop indisponível no boot — segue com o tema default.
        }

        await LoadPresetsAsync();
    }

    /// <summary>
    /// Sincroniza seleção e temas customizados do servidor uma vez por sessão,
    /// quando autenticado. A seleção do servidor prevalece; seleção local
    /// pré-existente é promovida ao servidor.
    /// </summary>
    public async Task SyncFromServerAsync()
    {
        if (_serverSynced || api is null || auth?.IsAuthenticated != true)
        {
            return;
        }

        var catalog = await api.GetThemesAsync();
        if (catalog is null)
        {
            return;
        }

        _serverSynced = true;
        Customs = catalog.Customs;
        Instance = catalog.Instance;

        var serverId = catalog.Active.ThemeId;
        if (!string.Equals(serverId, DefaultTheme, StringComparison.Ordinal) &&
            ThemeValidator.IsValidThemeId(serverId))
        {
            ThemeId = serverId;
            if (catalog.Active.Mode is "system" or "dark" or LightTheme)
            {
                Current = catalog.Active.Mode;
            }

            await ApplyModeAsync();
            await ApplyThemeAsync();
        }
        else if (ThemeId != DefaultTheme)
        {
            // Escolha local anterior ao sync — leva ao servidor.
            _ = api.SetActiveThemeAsync(ThemeId, Current);
            await ApplyThemeAsync();
        }

        Changed?.Invoke();
    }

    /// <summary>Catálogo completo: presets + instância + customs do usuário.</summary>
    public IEnumerable<CustomThemeDefinition> Catalog()
    {
        foreach (var p in Presets)
        {
            yield return p;
        }

        if (Instance is not null)
        {
            yield return Instance;
        }

        foreach (var c in Customs)
        {
            yield return c.Theme;
        }
    }

    /// <summary>Define o modo (system/light/dark) e persiste local + servidor.</summary>
    public async Task SetAsync(string theme)
    {
        Current = theme;
        try
        {
            await storage.SetAsync(ThemeKey, theme);
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex))
        {
            // Persistência falhou (storage negado) — aplica o tema mesmo assim.
        }

        await ApplyModeAsync();
        if (auth?.IsAuthenticated == true && api is not null)
        {
            _ = api.SetActiveThemeAsync(ThemeId, Current);
        }

        Changed?.Invoke();
    }

    /// <summary>Define a paleta (id do tema) e persiste local + servidor.</summary>
    public async Task SetThemeAsync(string themeId)
    {
        ThemeId = themeId;
        await ApplyThemeAsync();
        if (auth?.IsAuthenticated == true && api is not null)
        {
            _ = api.SetActiveThemeAsync(ThemeId, Current);
        }

        Changed?.Invoke();
    }

    /// <summary>Envia um tema JSON customizado ao servidor; retorna o tema ou o erro.</summary>
    public async Task<(UserThemeResponse? Theme, string? Error)> UploadThemeAsync(string json)
    {
        if (api is null)
        {
            return (null, "api indisponível");
        }

        var (theme, error) = await api.CreateThemeAsync(json);
        if (theme is not null)
        {
            Customs.RemoveAll(c => c.ThemeId == theme.ThemeId);
            Customs.Add(theme);
            Changed?.Invoke();
        }

        return (theme, error);
    }

    /// <summary>Remove um tema customizado do usuário; volta ao default se estava ativo.</summary>
    public async Task<bool> DeleteThemeAsync(string id)
    {
        if (api is null)
        {
            return false;
        }

        var victim = Customs.FirstOrDefault(c => c.Id == id);
        var ok = await api.DeleteThemeAsync(id);
        if (ok)
        {
            Customs.RemoveAll(c => c.Id == id);
            if (victim is not null && victim.ThemeId == ThemeId)
            {
                await SetThemeAsync(DefaultTheme);
            }

            Changed?.Invoke();
        }

        return ok;
    }

    private async Task ApplyThemeAsync()
    {
        var theme = Catalog().FirstOrDefault(t => t.Id == ThemeId);
        var css = theme is null || theme.Id == DefaultTheme
            ? ""
            : ThemeCssBuilder.Build(theme);
        await js.InvokeVoidAsync("openwebui.setCustomTheme", theme?.Id ?? DefaultTheme, css);
    }

    private Task ApplyModeAsync() =>
        js.InvokeVoidAsync("openwebui.setTheme", Current).AsTask();

    private async Task LoadPresetsAsync()
    {
        if (http is null)
        {
            return;
        }

        try
        {
            var list = await http.GetFromJsonAsync<List<CustomThemeDefinition>>("themes/builtin.json");
            if (list is not null)
            {
                Presets = list;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // builtin.json ausente/inválido — catálogo fica só com customs.
        }
    }
}

/// <summary>Barramento leve para sincronizar a lista de chats da barra lateral.</summary>
public class ChatListState
{
    /// <summary>Disparado quando a lista de chats deve ser recarregada.</summary>
    public event Action? Changed;

    /// <summary>Solicita a atualização da lista.</summary>
    public void NotifyChanged() => Changed?.Invoke();
}
