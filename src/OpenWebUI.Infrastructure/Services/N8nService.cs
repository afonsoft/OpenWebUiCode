using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Cliente da integração n8n (SPEC-20261007-chat-agent-parity RF-020):
/// base URL + API key configuradas por admin em kv
/// (<c>n8n.base_url</c>/<c>n8n.api_key</c>) ou env
/// <c>N8N__BaseUrl</c>/<c>N8N__ApiKey</c>. Lista workflows via API pública
/// (<c>GET /api/v1/workflows</c>) e dispara execuções por id
/// (<c>POST /api/v1/workflows/{id}/execute</c>, n8n ≥ 1.x recente) ou por
/// webhook de produção (<c>POST /webhook/{path}</c>, sem API key).
/// </summary>
public sealed class N8nService(IHttpClientFactory httpClientFactory,
    ConfigService config, IConfiguration configuration)
{
    /// <summary>Nome do HttpClient tipado por configuração.</summary>
    public const string HttpClientName = "n8n";

    /// <summary>Chaves kv de configuração.</summary>
    public const string BaseUrlKey = "n8n.base_url";

    /// <summary>Chave kv da API key.</summary>
    public const string ApiKeyKey = "n8n.api_key";

    /// <summary>Caps de leitura.</summary>
    private const int MaxWorkflows = 200;
    private const int MaxBodyChars = 1500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Objeto JSON vazio imutável para payload ausente.</summary>
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement;

    /// <summary>Base URL configurada (kv ou env), normalizada sem barra final.</summary>
    public async Task<string?> GetBaseUrlAsync(CancellationToken ct = default)
    {
        var url = await config.GetAsync<string?>(BaseUrlKey, null, ct)
                  ?? configuration["N8N:BaseUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return url.Trim().TrimEnd('/');
    }

    /// <summary>API key configurada (kv ou env).</summary>
    public async Task<string?> GetApiKeyAsync(CancellationToken ct = default)
    {
        var key = await config.GetAsync<string?>(ApiKeyKey, null, ct)
                  ?? configuration["N8N:ApiKey"];
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    /// <summary>True quando há base URL configurada.</summary>
    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default) =>
        await GetBaseUrlAsync(ct) is not null;

    /// <summary>Lista workflows do n8n (exige API key).</summary>
    public async Task<IReadOnlyList<N8nWorkflowItem>> ListWorkflowsAsync(CancellationToken ct)
    {
        var baseUrl = await GetBaseUrlAsync(ct)
            ?? throw new InvalidOperationException("n8n não configurado (n8n.base_url).");
        var apiKey = await GetApiKeyAsync(ct)
            ?? throw new InvalidOperationException(
                "n8n sem API key — configure n8n.api_key para usar a API pública.");

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}/api/v1/workflows?limit={MaxWorkflows}");
        request.Headers.Add("X-N8N-API-KEY", apiKey);
        using var response = await Client().SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"n8n respondeu {(int)response.StatusCode}: {Truncate(body, 300)}");
        }

        var items = ParseWorkflows(body);
        return items;
    }

    /// <summary>Parse tolerante: <c>{"data":[…]}</c> ou array raiz.</summary>
    private static List<N8nWorkflowItem> ParseWorkflows(string body)
    {
        var items = new List<N8nWorkflowItem>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.TryGetProperty("data", out var d)
                ? d : doc.RootElement;
            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var wf in data.EnumerateArray())
                {
                    var id = wf.TryGetProperty("id", out var i) ? i.ToString() : null;
                    var name = wf.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (id is null || name is null)
                    {
                        continue;
                    }

                    var active = wf.TryGetProperty("active", out var a)
                                 && a.ValueKind == JsonValueKind.True;
                    items.Add(new N8nWorkflowItem(id, name, active));
                }
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Resposta do n8n não é JSON válido.");
        }

        return items;
    }

    /// <summary>
    /// Dispara execução: <paramref name="webhookPath"/> usa o webhook de
    /// produção do workflow (recomendado — não exige API key);
    /// <paramref name="workflowId"/> chama a API pública
    /// (<c>POST /api/v1/workflows/{id}/execute</c>, exige API key e n8n
    /// recente). Exatamente um dos dois é obrigatório.
    /// </summary>
    public async Task<N8nTriggerResult> TriggerAsync(
        string? workflowId, string? webhookPath, JsonElement? payload, CancellationToken ct)
    {
        var baseUrl = await GetBaseUrlAsync(ct)
            ?? throw new InvalidOperationException("n8n não configurado (n8n.base_url).");

        HttpRequestMessage request;
        if (!string.IsNullOrWhiteSpace(webhookPath))
        {
            var path = webhookPath.Trim().TrimStart('/');
            request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/webhook/{path}")
            {
                Content = JsonContent(payload ?? EmptyObject),
            };
        }
        else if (!string.IsNullOrWhiteSpace(workflowId))
        {
            var apiKey = await GetApiKeyAsync(ct)
                ?? throw new InvalidOperationException(
                    "Trigger por workflow_id exige n8n.api_key (alternativa: webhook_path).");
            request = new HttpRequestMessage(HttpMethod.Post,
                $"{baseUrl}/api/v1/workflows/{Uri.EscapeDataString(workflowId)}/execute")
            {
                Content = JsonContent(new
                {
                    inputData = payload is { } p ? p : EmptyObject,
                }),
            };
            request.Headers.Add("X-N8N-API-KEY", apiKey);
        }
        else
        {
            throw new InvalidOperationException(
                "Informe workflow_id ou webhook_path para disparar o n8n.");
        }

        using (request)
        {
            using var response = await Client().SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return new N8nTriggerResult(
                response.IsSuccessStatusCode,
                (int)response.StatusCode,
                Truncate(body, MaxBodyChars));
        }
    }

    private HttpClient Client() => httpClientFactory.CreateClient(HttpClientName);

    private static StringContent JsonContent(JsonElement element) =>
        new(element.GetRawText(), new MediaTypeHeaderValue("application/json"));

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value, JsonOptions),
            new MediaTypeHeaderValue("application/json"));

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

/// <summary>Workflow resumido do n8n.</summary>
public sealed record N8nWorkflowItem(string Id, string Name, bool Active);

/// <summary>Resultado de um disparo n8n (status HTTP + corpo resumido).</summary>
public sealed record N8nTriggerResult(bool Success, int StatusCode, string Body);
