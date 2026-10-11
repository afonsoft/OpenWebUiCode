using Microsoft.JSInterop;

namespace OpenWebUI.Client.Services;

/// <summary>Gerencia o tema claro/escuro aplicado ao documento.</summary>
public class ThemeService(IJSRuntime js, BrowserStorage storage)
{
    private const string ThemeKey = "webui.theme";

    /// <summary>Tema atual: "system", "dark" ou "light" (default claro).</summary>
    public string Current { get; private set; } = "light";

    /// <summary>Disparado quando o tema muda.</summary>
    public event Action? Changed;

    /// <summary>Carrega o tema persistido e o aplica ao documento.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var saved = await storage.GetAsync(ThemeKey);
            Current = saved is "light" or "dark" or "system" ? saved : "light";
            await ApplyAsync();
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex))
        {
            // Storage/interop indisponível no boot — segue com o tema default.
        }
    }

    /// <summary>Alterna entre claro e escuro.</summary>
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
        await ApplyAsync();
        Changed?.Invoke();
    }

    private Task ApplyAsync() =>
        js.InvokeVoidAsync("openwebui.setTheme", Current).AsTask();
}

/// <summary>Barramento leve para sincronizar a lista de chats da barra lateral.</summary>
public class ChatListState
{
    /// <summary>Disparado quando a lista de chats deve ser recarregada.</summary>
    public event Action? Changed;

    /// <summary>Solicita a atualização da lista.</summary>
    public void NotifyChanged() => Changed?.Invoke();
}
