using System.Text.Json;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:generate_video</c> — gera vídeo via motor configurado
/// (OpenAI-compatible ou ComfyUI) e renderiza inline no transcript
/// (SPEC-20261007-chat-agent-parity RF-019). Reutiliza o
/// <see cref="VideoGenerationService"/> — a resposta carrega
/// <c>{"videoPath": "/api/v1/files/{id}/content"}</c> que o cliente vira
/// <c>&lt;video controls&gt;</c> no card de tool. Off por padrão (admin
/// habilita em <c>video.config</c>).
/// </summary>
public sealed class GenerateVideoBuiltinTool(VideoGenerationService videos) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "generate_video";

    /// <inheritdoc />
    public string Description =>
        "ALWAYS call to create or generate a video clip.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "prompt": { "type": "string", "description": "Description of the desired video." },
            "seconds": { "type": "integer", "description": "Duration in seconds (1-60)." },
            "size": { "type": "string", "description": "Resolution, e.g. 1280x720." }
          },
          "required": ["prompt"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var prompt = args.TryGetProperty("prompt", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new BuiltinToolResult("Parâmetro 'prompt' é obrigatório.");
        }

        int? seconds = args.TryGetProperty("seconds", out var sEl)
                       && sEl.TryGetInt32(out var sv) ? Math.Clamp(sv, 1, 60) : null;
        var size = args.TryGetProperty("size", out var s) ? s.GetString() : null;

        List<FileEntry> files;
        try
        {
            files = await videos.GenerateAsync(
                prompt, seconds, size, context.UserId, context.UploadDir, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new BuiltinToolResult($"Geração de vídeo falhou: {ex.Message}");
        }

        var videos_ = files.Select(f => new
        {
            fileId = f.Id,
            videoPath = $"/api/v1/files/{f.Id}/content",
            filename = f.Filename,
        }).ToList();

        var text = files.Count == 1
            ? $"Vídeo gerado: /api/v1/files/{files[0].Id}/content"
            : $"{files.Count} vídeos gerados: "
              + string.Join(", ", files.Select(f => $"/api/v1/files/{f.Id}/content"));

        return new BuiltinToolResult(
            text,
            new { videoPath = $"/api/v1/files/{files[0].Id}/content", videos = videos_ });
    }
}
