using System.Net.Http.Json;
using Microsoft.JSInterop;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>Gerencia autenticação do usuário: login, cadastro, sessão e token JWT.</summary>
public class AuthService(
    HttpClient http, BrowserStorage storage, LocalizationService l10n,
    IJSRuntime? js = null)
{
    private const string TokenKey = "webui.token";
    private const string UserKey = "webui.user";

    /// <summary>Usuário autenticado na sessão atual, se houver.</summary>
    public UserResponse? CurrentUser { get; private set; }

    /// <summary>Indica se há uma sessão autenticada.</summary>
    public bool IsAuthenticated => CurrentUser is not null && _token is not null;

    /// <summary>Disparado quando o estado de autenticação muda.</summary>
    public event Action? Changed;

    private string? _token;
    private bool _initialized;

    /// <summary>Restaura a sessão persistida no navegador.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        string? userJson = null;
        try
        {
            _token = await storage.GetAsync(TokenKey);
            userJson = await storage.GetAsync(UserKey);
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex))
        {
            // Storage/interop indisponível no boot — segue como deslogado.
        }
        if (_token is not null && userJson is not null)
        {
            try
            {
                CurrentUser = System.Text.Json.JsonSerializer.Deserialize<UserResponse>(
                    userJson, JsonOptions);
            }
            catch (System.Text.Json.JsonException)
            {
                CurrentUser = null;
            }
        }
    }

    /// <summary>Autentica com e-mail e senha.</summary>
    /// <returns>Null em caso de sucesso; mensagem de erro caso contrário.</returns>
    public async Task<string?> SignInAsync(string email, string password)
    {
        var response = await http.PostAsJsonAsync(
            "/api/v1/auths/signin", new SignInRequest(email, password), JsonOptions);
        return await PersistOrErrorAsync(response);
    }

    /// <summary>Cadastra um novo usuário.</summary>
    /// <returns>Null em caso de sucesso; mensagem de erro caso contrário.</returns>
    public async Task<string?> SignUpAsync(string name, string email, string password)
    {
        var response = await http.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password), JsonOptions);
        return await PersistOrErrorAsync(response);
    }

    /// <summary>Atualiza os dados do usuário autenticado a partir da API.</summary>
    public async Task RefreshUserAsync()
    {
        var user = await SendGetAsync<UserResponse>("/api/v1/auths/");
        if (user is not null)
        {
            CurrentUser = user;
            await SetStorageSafeAsync(UserKey, System.Text.Json.JsonSerializer.Serialize(user, JsonOptions));
            Changed?.Invoke();
        }
    }

    /// <summary>Encerra a sessão local.</summary>
    public async Task SignOutAsync()
    {
        _token = null;
        CurrentUser = null;
        await RemoveStorageSafeAsync(TokenKey);
        await RemoveStorageSafeAsync(UserKey);
        // SPEC-20261013-chat-media-attachments RF-002: solta os object URLs
        // de mídia cacheados — são autorizados pelo token encerrado.
        try
        {
            if (js is not null)
            {
                await js.InvokeVoidAsync("openwebui.media.revokeAll");
            }
        }
        catch (Exception)
        {
            // Interop indisponível no shutdown — o cache morre com a página.
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Consome o token do callback OAuth (?oauth_token=... na URL atual),
    /// persistindo a sessão. Retorna erro OAuth, se presente.
    /// </summary>
    public async Task<string?> CompleteOAuthLoginAsync(string uri)
    {
        var query = uri.Contains('?', StringComparison.Ordinal)
            ? uri[(uri.IndexOf('?', StringComparison.Ordinal) + 1)..]
            : string.Empty;
        string? token = null;
        string? error = null;
        foreach (var parts in query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                     .Select(pair => pair.Split('=', 2)))
        {
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            if (key == "oauth_token")
            {
                token = value;
            }
            else if (key == "oauth_error")
            {
                error = value;
            }
        }

        if (error is not null)
        {
            return l10n[error == "pending" ? "auth.error_pending" : "auth.error_provider"];
        }

        if (token is null)
        {
            return null;
        }

        _token = token;
        await SetStorageSafeAsync(TokenKey, token);
        await RefreshUserAsync();
        return IsAuthenticated ? null : l10n["auth.error_load_user"];
    }

    /// <summary>Cria uma requisição autenticada com o token da sessão.</summary>
    public HttpRequestMessage CreateRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        if (_token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
        }

        return request;
    }

    /// <summary>Token da sessão atual, se houver.</summary>
    public string? Token => _token;

    private async Task<T?> SendGetAsync<T>(string uri)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<T>(JsonOptions)
                : default;
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex))
        {
            // Falha transitória (offline/timeout/parse) — como resposta vazia.
            return default;
        }
    }

    private async Task<string?> PersistOrErrorAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(
                    await response.Content.ReadAsStringAsync());
                // error_code do backend → chave errors.{code} localizada; senão detail cru.
                if (node?["error_code"]?.GetValue<string>() is { } code)
                {
                    var translated = l10n[$"errors.{code}"];
                    if (translated != $"errors.{code}")
                    {
                        return translated;
                    }
                }
                return node?["detail"]?.GetValue<string>() ?? l10n["auth.error_failed"];
            }
            catch (System.Text.Json.JsonException)
            {
                return l10n["auth.error_failed"];
            }
        }

        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        if (auth is null)
        {
            return l10n["auth.error_invalid"];
        }

        _token = auth.Token;
        CurrentUser = auth.User;
        await SetStorageSafeAsync(TokenKey, auth.Token);
        await SetStorageSafeAsync(UserKey, System.Text.Json.JsonSerializer.Serialize(auth.User, JsonOptions));
        Changed?.Invoke();
        return null;
    }

    /// <summary>
    /// Persiste no storage sem propagar: browser pode negar localStorage
    /// (modo privado, cookies bloqueados) — a sessão em memória segue válida.
    /// </summary>
    private async Task SetStorageSafeAsync(string key, string value)
    {
        try
        {
            await storage.SetAsync(key, value);
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex) || ex is InvalidOperationException)
        {
            await Console.Error.WriteLineAsync($"[AuthService] persistência indisponível: {ex.Message}");
        }
    }

    /// <summary>Remove do storage sem propagar (ver <see cref="SetStorageSafeAsync"/>).</summary>
    private async Task RemoveStorageSafeAsync(string key)
    {
        try
        {
            await storage.RemoveAsync(key);
        }
        catch (Exception ex) when (ClientErrors.IsTransient(ex) || ex is InvalidOperationException)
        {
            await Console.Error.WriteLineAsync($"[AuthService] persistência indisponível: {ex.Message}");
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);
}
