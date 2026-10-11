using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Receptor de <c>run.completed</c> (SPEC-20261007-chat-notifications):
/// garante a conexão realtime uma vez autenticado e dispara, por preferência
/// do usuário (chaves em <c>SettingsJson</c>):
///   • toast in-app (<see cref="ToastService"/>) — <c>notify_chat_done</c>;
///   • Notification API quando a aba está oculta — <c>notify_browser</c>;
///   • Web Push (aba fechada) — <c>notify_push</c> reconciliado via
///     <c>SyncPushAsync</c>.
/// A permissão do navegador só é pedida no gesto de ligar o toggle — nunca
/// no boot. Tudo é best-effort: falha de hub/JS não afeta o chat.
/// </summary>
public sealed class ChatNotificationsService : IAsyncDisposable
{
    /// <summary>Preferência: toast in-app ao fim da run (default ligado).</summary>
    public const string PrefInApp = "notify_chat_done";

    /// <summary>Preferência: Notification API com a aba oculta (default desligado).</summary>
    public const string PrefBrowser = "notify_browser";

    /// <summary>Preferência: Web Push com a aba fechada (default desligado).</summary>
    public const string PrefPush = "notify_push";

    private readonly RealtimeService _realtime;
    private readonly NavigationManager _nav;
    private readonly IJSRuntime _js;
    private readonly ApiService _api;
    private readonly AuthService _auth;
    private readonly ToastService _toast;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;

    public ChatNotificationsService(
        RealtimeService realtime,
        NavigationManager nav,
        IJSRuntime js,
        ApiService api,
        AuthService auth,
        ToastService toast)
    {
        _realtime = realtime;
        _nav = nav;
        _js = js;
        _api = api;
        _auth = auth;
        _toast = toast;
    }

    /// <summary>Conecta e assina o evento — idempotente, chamar após autenticar.</summary>
    public async Task EnsureStartedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_started || !_auth.IsAuthenticated)
            {
                return;
            }

            _started = true;
            _realtime.OnRunCompleted += HandleRunCompleted;
            try
            {
                await _realtime.ConnectAsync(_nav.BaseUri, _auth.Token);
            }
            catch (Exception ex) when (ClientErrors.IsTransient(ex))
            {
                // API indisponível: hub reconecta sozinho (WithAutomaticReconnect).
                await Console.Error.WriteLineAsync($"[ChatNotifications] connect falhou: {ex.Message}");
            }
            _ = SyncPushAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lê uma preferência de notificação (default quando ausente).</summary>
    /// <param name="pref">Chave (<see cref="PrefInApp"/>/…).</param>
    /// <param name="defaultValue">Default da preferência.</param>
    public async Task<bool> GetPrefAsync(string pref, bool defaultValue)
    {
        try
        {
            var settings = await _api.GetUserSettingsAsync();
            if (settings is not null
                && settings.TryGetValue(pref, out var raw)
                && raw is System.Text.Json.JsonElement el
                && el.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            {
                return el.GetBoolean();
            }
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex))
        {
            // Sem rede — default vale.
        }

        return defaultValue;
    }

    /// <summary>
    /// Grava uma preferência e aplica efeitos colaterais: ligar
    /// <see cref="PrefBrowser"/> pede a permissão do navegador (gesto do
    /// usuário); mudar <see cref="PrefPush"/> reconcilia a subscription.
    /// </summary>
    /// <param name="pref">Chave da preferência.</param>
    /// <param name="value">Novo valor.</param>
    /// <returns>Estado da permissão do navegador após a mudança.</returns>
    public async Task<string> SetPrefAsync(string pref, bool value)
    {
        var settings = await _api.GetUserSettingsAsync() ?? [];
        settings[pref] = value;
        await _api.UpdateUserSettingsAsync(settings);

        var permission = await BrowserPermissionAsync();
        if (pref == PrefBrowser && value && permission == "default")
        {
            permission = await _js.InvokeAsync<string>("openwebui.notify.ensurePermission");
        }
        else if (pref == PrefPush)
        {
            await SyncPushAsync();
        }

        return permission;
    }

    /// <summary>Estado atual de Notification.permission no navegador.</summary>
    public async Task<string> BrowserPermissionAsync()
    {
        try
        {
            return await _js.InvokeAsync<string>("openwebui.notify.permission");
        }
        catch (JSException)
        {
            return "unsupported";
        }
    }

    /// <summary>
    /// Reconcilia a subscription Web Push com a preferência: subscribed quando
    /// <see cref="PrefPush"/> ligada, unsubscribed quando desligada.
    /// Idempotente — seguro chamar a cada toggle/start.
    /// </summary>
    public async Task SyncPushAsync()
    {
        try
        {
            var enabled = await GetPrefAsync(PrefPush, defaultValue: false);
            var api = _nav.ToAbsoluteUri("/api/v1/notifications/push/subscriptions").ToString();
            var endpoint = await _js.InvokeAsync<string?>("openwebui.notify.isPushSubscribed");

            if (enabled && endpoint is null)
            {
                var key = await _api.GetVapidPublicKeyAsync();
                if (key is not { Length: > 0 })
                {
                    return;
                }

                await _js.InvokeAsync<object>("openwebui.notify.subscribePush", key, api);
            }
            else if (!enabled && endpoint is not null)
            {
                await _js.InvokeAsync<object>("openwebui.notify.unsubscribePush", api);
            }
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex))
        {
            // Push indisponível (SW/permission) — notificações seguem sem ele.
        }
        catch (Exception ex)
        {
            // _ = SyncPushAsync() é fire-and-forget: escape = banner de erro.
            await Console.Error.WriteLineAsync($"[ChatNotifications] sync push falhou: {ex.Message}");
        }
    }

    private async void HandleRunCompleted(RealtimeService.RunCompletedInfo info)
    {
        try
        {
            var label = info.Title is { Length: > 0 } ? info.Title : "Conversa";
            var (type, text) = info.Status switch
            {
                "completed" => (ToastType.Success, $"{label}: resposta concluída"),
                "stopped" => (ToastType.Info, $"{label}: resposta interrompida"),
                "interrupted" => (ToastType.Warning,
                    $"{label}: interrompida (servidor reiniciou — abra para retomar)"),
                _ => (ToastType.Error,
                    $"{label}: falhou{(info.Error is { Length: > 0 } e ? $" — {e}" : "")}"),
            };
            var url = $"/c/{Uri.EscapeDataString(info.ChatId)}";

            if (await GetPrefAsync(PrefInApp, defaultValue: true))
            {
                _toast.Show(text, type, url);
            }

            if (await GetPrefAsync(PrefBrowser, defaultValue: false)
                && await _js.InvokeAsync<bool>("openwebui.notify.isHidden"))
            {
                var body = info.Snippet ?? text;
                await _js.InvokeAsync<bool>(
                    "openwebui.notify.notify", "Open WebUI", body, url, $"chat-run-{info.RunId}");
            }
        }
        catch (Exception)
        {
            // Fan-out de notificação nunca afeta a aplicação.
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _realtime.OnRunCompleted -= HandleRunCompleted;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

}
