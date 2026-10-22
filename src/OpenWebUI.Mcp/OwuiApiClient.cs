using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenWebUI.Mcp;

/// <summary>
/// Cliente HTTP mínimo do OpenWebUI.Api para o servidor MCP: autenticação
/// via API key (<c>Bearer sk-*</c>) e chamadas JSON sobre os endpoints
/// REST existentes — nenhum caminho novo de execução no servidor.
/// </summary>
public sealed class OwuiApiClient
{
    private readonly HttpClient _http;

    /// <summary>Cria o cliente; <paramref name="apiKey"/> vazio/null deixa
    /// o cliente sem Authorization (tools que exigem auth falham fechado).</summary>
    public OwuiApiClient(HttpClient http, string baseUrl, string? apiKey)
    {
        _http = http;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    /// <summary>Se há credencial configurada.</summary>
    public bool HasCredentials =>
        _http.DefaultRequestHeaders.Authorization is not null;

    /// <summary>GET que devolve o corpo JSON como string (ou erro).</summary>
    public async Task<(string? Body, string? Error)> GetAsync(
        string path, CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, path, null, ct);

    /// <summary>POST JSON; <paramref name="body"/> serializado com
    /// camelCase (convenção dos endpoints).</summary>
    public async Task<(string? Body, string? Error)> PostAsync(
        string path, object? body, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, path, body, ct);

    private async Task<(string? Body, string? Error)> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: JsonOptions);
            }
            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode
                ? (text, null)
                : (null, $"HTTP {(int)response.StatusCode}: {Truncate(text)}");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"conexão falhou: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (null, "timeout na chamada à API");
        }
    }

    private static string Truncate(string text) =>
        text.Length > 300 ? text[..300] + "…" : text;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
