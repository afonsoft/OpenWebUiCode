using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Cliente do ecossistema de pipelines: descobre pipes em servidores externos
/// e roteia completions `pipeline:{id}` para eles (OpenAI-compatible:
/// `GET {url}/models`, `POST {url}/chat/completions`). Nenhum código do
/// servidor de pipelines é executado localmente — só HTTP.
/// </summary>
public class PipelineClientService(AppDbContext db, IHttpClientFactory httpFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Lista pipes de um servidor (`GET {url}/models`, fallback `GET {url}/`).</summary>
    /// <returns>Pares (pipeId, name); null quando o servidor não responde.</returns>
    public async Task<List<(string Id, string Name)>?> ListPipesAsync(
        PipelineServer server, CancellationToken ct = default)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, server, "models");
            using var response = await NewClient().SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return ParsePipes(json);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Descobre o servidor que hospeda um pipe pelo id.</summary>
    public async Task<PipelineServer?> FindServerForPipeAsync(
        string pipeId, CancellationToken ct = default)
    {
        var servers = await db.PipelineServers.ToListAsync(ct);
        foreach (var server in servers)
        {
            var pipes = await ListPipesAsync(server, ct);
            if (pipes is not null && pipes.Any(p => p.Id == pipeId))
            {
                return server;
            }
        }

        return null;
    }

    /// <summary>
    /// Roteia uma completion ao servidor de pipelines (streaming).
    /// Retorna a resposta upstream ou erro transport-neutral.
    /// </summary>
    public async Task<ProxiedResponse> RouteCompletionAsync(
        PipelineServer server, string body, string? valvesJson, CancellationToken ct = default)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Post, server, "chat/completions");
            var merged = MergeValves(body, valvesJson);
            request.Content = new StringContent(merged, System.Text.Encoding.UTF8, "application/json");
            var response = await NewClient().SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            return new ProxiedResponse((int)response.StatusCode, response, null);
        }
        catch (HttpRequestException ex)
        {
            return new ProxiedResponse(502, null, ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return new ProxiedResponse(502, null, ex.Message);
        }
    }

    /// <summary>Injeta o objeto `valves` no corpo da completion quando presente.</summary>
    private static string MergeValves(string body, string? valvesJson)
    {
        if (string.IsNullOrWhiteSpace(valvesJson) || valvesJson == "{}")
        {
            return body;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = new Dictionary<string, JsonElement>(
                doc.RootElement.EnumerateObject()
                    .Select(p => KeyValuePair.Create(p.Name, p.Value.Clone())));
            root["valves"] = JsonSerializer.Deserialize<JsonElement>(valvesJson);
            return JsonSerializer.Serialize(root);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, PipelineServer server, string path)
    {
        var request = new HttpRequestMessage(
            method, $"{server.Url.TrimEnd('/')}/{path}");
        if (!string.IsNullOrEmpty(server.Key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Key);
        }

        return request;
    }

    private HttpClient NewClient()
    {
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
    }

    /// <summary>Aceita `{"data":[{id,...}]}` (OpenAI) ou `[{id,name}]` direto.</summary>
    private static List<(string Id, string Name)> ParsePipes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            array = data;
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else
        {
            return [];
        }

        var pipes = new List<(string, string)>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                pipes.Add((item.GetString()!, item.GetString()!));
            }
            else if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("id", out var id))
            {
                var name = item.TryGetProperty("name", out var n)
                    ? n.GetString() ?? id.GetString()!
                    : id.GetString()!;
                pipes.Add((id.GetString()!, name));
            }
        }

        return pipes;
    }
}
