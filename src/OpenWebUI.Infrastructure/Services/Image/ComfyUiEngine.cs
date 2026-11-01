using System.Text.Json;
using System.Text.Json.Nodes;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services.Image;

/// <summary>
/// Motor ComfyUI: envia o workflow template (com placeholders {prompt},{seed},{steps})
/// para <c>/prompt</c>, faz polling em <c>/history/{id}</c> e baixa as imagens via <c>/view</c>.
/// </summary>
public sealed class ComfyUiEngine(IHttpClientFactory httpClientFactory) : ImageEngineBase(httpClientFactory)
{
    /// <inheritdoc />
    public override string Name => "comfyui";

    /// <inheritdoc />
    public override async Task<IReadOnlyList<byte[]>> GenerateAsync(
        ImagesConfig config, string prompt, int n, string? size, CancellationToken ct)
    {
        var template = Param(config, "workflow")
            ?? throw new InvalidOperationException("ComfyUI requer 'workflow' em EngineParams.");
        var seed = Random.Shared.NextInt64(0, long.MaxValue);
        var steps = int.TryParse(Param(config, "steps"), out var s) ? s : 20;
        var workflow = template
            .Replace("{prompt}", prompt.Replace("\"", "\\\""), StringComparison.Ordinal)
            .Replace("{seed}", seed.ToString(), StringComparison.Ordinal)
            .Replace("{steps}", steps.ToString(), StringComparison.Ordinal);

        var payload = new JsonObject
        {
            ["prompt"] = JsonNode.Parse(workflow)?.AsObject()
                ?? throw new InvalidOperationException("Workflow ComfyUI inválido."),
            ["client_id"] = Guid.NewGuid().ToString(),
        };
        var submitted = await PostJsonAsync(config, $"{Base(config)}/prompt", payload, ct);
        var promptId = submitted?["prompt_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("ComfyUI não retornou prompt_id.");

        var images = new List<byte[]>();
        using var timeout = Timeout(config, ct);
        while (!timeout.Token.IsCancellationRequested)
        {
            var history = await GetJsonAsync(config, $"{Base(config)}/history/{promptId}", ct);
            var entry = history?[promptId];
            var outputs = entry?["outputs"]?.AsObject();
            if (outputs is null)
            {
                var status = entry?["status"]?["status_str"]?.GetValue<string>();
                if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("ComfyUI falhou ao executar o workflow.");
                }
                await Task.Delay(1000, ct);
                continue;
            }

            await CollectImagesAsync(outputs, config, images, n, ct);
            return images;
        }
        throw new TaskCanceledException("ComfyUI excedeu o tempo limite.");
    }

    /// <summary>Baixa as imagens dos nós de output até completar <paramref name="n"/>.</summary>
    private async Task CollectImagesAsync(
        JsonObject outputs, ImagesConfig config,
        List<byte[]> images, int n, CancellationToken ct)
    {
        foreach (var node in outputs)
        {
            foreach (var image in node.Value?["images"]?.AsArray() ?? [])
            {
                var filename = image!["filename"]?.GetValue<string>();
                if (filename is null)
                {
                    continue;
                }
                var subfolder = image["subfolder"]?.GetValue<string>() ?? "";
                var type = image["type"]?.GetValue<string>() ?? "output";
                var viewUrl = $"{Base(config)}/view?filename={Uri.EscapeDataString(filename)}" +
                    $"&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
                images.Add(await GetBytesAsync(viewUrl, config, ct));
                if (images.Count >= n)
                {
                    return;
                }
            }
        }
    }

    /// <inheritdoc />
    public override async Task<(bool Ok, string Detail)> TestAsync(ImagesConfig config, CancellationToken ct)
    {
        try
        {
            await GetJsonAsync(config, $"{Base(config)}/system_stats", ct);
            return (true, "ok");
        }
        catch (HttpRequestException ex) { return (false, ex.Message); }
        catch (JsonException ex) { return (false, ex.Message); }
        catch (InvalidOperationException ex) { return (false, ex.Message); }
        catch (TaskCanceledException ex) { return (false, ex.Message); }
    }
}
