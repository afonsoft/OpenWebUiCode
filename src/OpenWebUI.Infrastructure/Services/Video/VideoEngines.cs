using System.Text.Json;
using System.Text.Json.Nodes;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Infrastructure.Services.Video;

/// <summary>
/// Motor OpenAI-compatible de vídeo (<c>sora-*</c>, POST /videos →
/// polling /videos/{id} → GET /videos/{id}/content).
/// </summary>
public sealed class OpenAiVideoEngine(IHttpClientFactory httpClientFactory)
    : MediaEngineBase(httpClientFactory), IVideoEngine
{
    /// <inheritdoc />
    public string Name => "openai";

    /// <inheritdoc />
    public async Task<IReadOnlyList<VideoResult>> GenerateAsync(
        ImagesConfig config, string prompt, int? seconds, string? size, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = config.Model,
            ["prompt"] = prompt,
        };
        if (seconds is { } s)
        {
            payload["seconds"] = Math.Clamp(s, 1, 60).ToString();
        }
        if (!string.IsNullOrWhiteSpace(size))
        {
            payload["size"] = size;
        }

        var created = await PostJsonAsync(config, $"{Base(config)}/videos", payload, ct);
        var id = created?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Provider de vídeo não retornou id.");

        using var timeout = Timeout(config, ct);
        while (!timeout.Token.IsCancellationRequested)
        {
            await Task.Delay(3000, ct);
            var job = await GetJsonAsync(config, $"{Base(config)}/videos/{id}", ct);
            var status = job?["status"]?.GetValue<string>();
            switch (status)
            {
                case "completed":
                    var bytes = await GetBytesAsync(
                        $"{Base(config)}/videos/{id}/content", config, ct);
                    return [new VideoResult(bytes, "mp4")];
                case "failed":
                    var error = job!["error"]?["message"]?.GetValue<string>()
                        ?? job["error"]?.ToJsonString() ?? "desconhecido";
                    throw new InvalidOperationException(
                        $"Geração de vídeo falhou no provider: {error}");
                default:
                    continue; // queued/in_progress → continua o polling
            }
        }
        throw new TaskCanceledException("Provider de vídeo excedeu o tempo limite.");
    }

    /// <inheritdoc />
    public async Task<(bool Ok, string Detail)> TestAsync(
        ImagesConfig config, CancellationToken ct)
    {
        try
        {
            await GetJsonAsync(config, $"{Base(config)}/models", ct);
            return (true, "ok");
        }
        catch (HttpRequestException ex) { return (false, ex.Message); }
        catch (JsonException ex) { return (false, ex.Message); }
        catch (InvalidOperationException ex) { return (false, ex.Message); }
        catch (TaskCanceledException ex) { return (false, ex.Message); }
    }
}

/// <summary>
/// Motor ComfyUI de vídeo: mesmo workflow-template do motor de imagem
/// (<c>{prompt}</c>, <c>{seed}</c>, <c>{steps}</c>), mas coleta saídas de
/// <c>videos</c>/<c>gifs</c>/<c>images</c> filtradas por extensão de
/// vídeo (mp4, webm, mov, webp, gif — ex.: VideoCombine/AnimateDiff).
/// </summary>
public sealed class ComfyUiVideoEngine(IHttpClientFactory httpClientFactory)
    : MediaEngineBase(httpClientFactory), IVideoEngine
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "webm", "mov", "webp", "gif", "mkv",
    };

    /// <inheritdoc />
    public string Name => "comfyui";

    /// <inheritdoc />
    public async Task<IReadOnlyList<VideoResult>> GenerateAsync(
        ImagesConfig config, string prompt, int? seconds, string? size, CancellationToken ct)
    {
        var template = Param(config, "workflow")
            ?? throw new InvalidOperationException("ComfyUI requer 'workflow' em EngineParams.");
        var seed = Random.Shared.NextInt64(0, long.MaxValue);
        var steps = int.TryParse(Param(config, "steps"), out var s) ? s : 20;
        var workflow = template
            .Replace("{prompt}", prompt.Replace("\"", "\\\""), StringComparison.Ordinal)
            .Replace("{seed}", seed.ToString(), StringComparison.Ordinal)
            .Replace("{steps}", steps.ToString(), StringComparison.Ordinal)
            .Replace("{frames}", (seconds is { } sec ? Math.Clamp(sec * 16, 8, 1000) : 33)
                .ToString());

        var payload = new JsonObject
        {
            ["prompt"] = JsonNode.Parse(workflow)?.AsObject()
                ?? throw new InvalidOperationException("Workflow ComfyUI inválido."),
            ["client_id"] = Guid.NewGuid().ToString(),
        };
        var submitted = await PostJsonAsync(config, $"{Base(config)}/prompt", payload, ct);
        var promptId = submitted?["prompt_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("ComfyUI não retornou prompt_id.");

        var videos = new List<VideoResult>();
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
                await Task.Delay(2000, ct);
                continue;
            }

            foreach (var node in outputs)
            {
                foreach (var key in new[] { "videos", "gifs", "images" })
                {
                    foreach (var file in node.Value?[key]?.AsArray() ?? [])
                    {
                        var filename = file!["filename"]?.GetValue<string>();
                        if (filename is null)
                        {
                            continue;
                        }
                        var ext = Path.GetExtension(filename).TrimStart('.');
                        if (!VideoExtensions.Contains(ext))
                        {
                            continue;
                        }
                        var subfolder = file["subfolder"]?.GetValue<string>() ?? "";
                        var type = file["type"]?.GetValue<string>() ?? "output";
                        var viewUrl = $"{Base(config)}/view?filename={Uri.EscapeDataString(filename)}" +
                            $"&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
                        videos.Add(new VideoResult(await GetBytesAsync(viewUrl, config, ct), ext));
                    }
                }
            }
            return videos;
        }
        throw new TaskCanceledException("ComfyUI excedeu o tempo limite.");
    }

    /// <inheritdoc />
    public async Task<(bool Ok, string Detail)> TestAsync(
        ImagesConfig config, CancellationToken ct)
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

/// <summary>Resolve o motor de vídeo configurado por nome.</summary>
public sealed class VideoEngineFactory(IHttpClientFactory httpClientFactory)
{
    private static readonly string[] KnownEngines = ["openai", "comfyui"];

    /// <summary>Nomes dos motores disponíveis.</summary>
    public static IReadOnlyList<string> Engines => KnownEngines;

    /// <summary>Retorna o motor; nome desconhecido → <see cref="InvalidOperationException"/>.</summary>
    public IVideoEngine Resolve(string? engine) => engine?.ToLowerInvariant() switch
    {
        null or "" or "openai" => new OpenAiVideoEngine(httpClientFactory),
        "comfyui" => new ComfyUiVideoEngine(httpClientFactory),
        var other => throw new InvalidOperationException($"Motor de vídeo '{other}' não suportado."),
    };
}
