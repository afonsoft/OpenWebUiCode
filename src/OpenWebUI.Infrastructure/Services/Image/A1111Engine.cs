using System.Text.Json;
using System.Text.Json.Nodes;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services.Image;

/// <summary>Motor AUTOMATIC1111 (Stable Diffusion WebUI — /sdapi/v1/txt2img e img2img).</summary>
public sealed class A1111Engine(IHttpClientFactory httpClientFactory) : ImageEngineBase(httpClientFactory)
{
    private const string StepsKey = "steps";
    /// <inheritdoc />
    public override string Name => "a1111";

    /// <inheritdoc />
    public override bool SupportsEdit => true;

    /// <inheritdoc />
    public override async Task<IReadOnlyList<byte[]>> GenerateAsync(
        ImagesConfig config, string prompt, int n, string? size, CancellationToken ct)
    {
        var (w, h) = ParseSize(string.IsNullOrWhiteSpace(size) ? config.Size : size, 512);
        var payload = new JsonObject
        {
            ["prompt"] = prompt,
            ["batch_size"] = n,
            ["width"] = w,
            ["height"] = h,
            [StepsKey] = int.TryParse(Param(config, StepsKey), out var steps) ? steps : 20,
            ["cfg_scale"] = double.TryParse(Param(config, "cfg_scale"), out var cfg) ? cfg : 7.0,
            ["seed"] = -1,
        };
        var negative = Param(config, "negative_prompt");
        if (!string.IsNullOrEmpty(negative))
        {
            payload["negative_prompt"] = negative;
        }

        var json = await PostJsonAsync(config, $"{Base(config)}/sdapi/v1/txt2img", payload, ct);
        return DecodeImages(json);
    }

    /// <inheritdoc />
    public override async Task<byte[]> EditAsync(
        ImagesConfig config, byte[] sourceImage, string prompt, string? size, CancellationToken ct)
    {
        var (w, h) = ParseSize(string.IsNullOrWhiteSpace(size) ? config.Size : size, 512);
        var payload = new JsonObject
        {
            ["prompt"] = prompt,
            ["init_images"] = new JsonArray(Convert.ToBase64String(sourceImage)),
            ["denoising_strength"] = double.TryParse(Param(config, "denoising"), out var d) ? d : 0.75,
            ["width"] = w,
            ["height"] = h,
            [StepsKey] = int.TryParse(Param(config, StepsKey), out var steps) ? steps : 20,
        };
        var json = await PostJsonAsync(config, $"{Base(config)}/sdapi/v1/img2img", payload, ct);
        var images = DecodeImages(json);
        return images.Count > 0
            ? images[0]
            : throw new InvalidOperationException("Resposta img2img sem imagens.");
    }

    /// <inheritdoc />
    public override async Task<(bool Ok, string Detail)> TestAsync(ImagesConfig config, CancellationToken ct)
    {
        try
        {
            await GetJsonAsync(config, $"{Base(config)}/sdapi/v1/options", ct);
            return (true, "ok");
        }
        catch (HttpRequestException ex) { return (false, ex.Message); }
        catch (JsonException ex) { return (false, ex.Message); }
        catch (InvalidOperationException ex) { return (false, ex.Message); }
        catch (TaskCanceledException ex) { return (false, ex.Message); }
    }

    private static List<byte[]> DecodeImages(JsonNode? json)
    {
        return (json?["images"]?.AsArray() ?? [])
            .Select(item => item?.GetValue<string>())
            .Where(b64 => !string.IsNullOrEmpty(b64))
            .Select(b64 => Convert.FromBase64String(b64!))
            .ToList();
    }
}
