using System.Text.Json;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:generate_image</c> — gera imagem via provider configurado e
/// renderiza inline no transcript (SPEC-20261007-chat-agent-tools RF-001).
/// Reutiliza o <see cref="ImageGenerationService"/> (config por admin em
/// Imagens) e o armazenamento de files — a resposta carrega
/// <c>{"imagePath": "/api/v1/files/{id}/content"}</c> que o cliente vira
/// <c>&lt;img&gt;</c> no card de tool.
/// </summary>
public sealed class GenerateImageBuiltinTool(ImageGenerationService images) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "generate_image";

    /// <inheritdoc />
    public string Description =>
        "ALWAYS call to generate/draw any image, picture or art.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "prompt": { "type": "string", "description": "Description of the desired image." },
            "n": { "type": "integer", "description": "Number of images (1-4).", "default": 1 },
            "size": { "type": "string", "description": "Size, e.g. 512x512, 1024x1024." }
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

        var n = args.TryGetProperty("n", out var nEl) && nEl.TryGetInt32(out var nv) ? nv : 1;
        var size = args.TryGetProperty("size", out var s) ? s.GetString() : null;

        List<FileEntry> files;
        try
        {
            files = await images.GenerateAsync(
                prompt, n, size, context.UserId, context.UploadDir, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new BuiltinToolResult($"Geração de imagem falhou: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new BuiltinToolResult($"Geração de imagem falhou: {ex.Message}");
        }

        if (files.Count == 0)
        {
            return new BuiltinToolResult("O provider não retornou imagens.");
        }

        var images_ = files.Select(f => new
        {
            fileId = f.Id,
            imagePath = $"/api/v1/files/{f.Id}/content",
            filename = f.Filename,
        }).ToList();

        var text = files.Count == 1
            ? $"Imagem gerada: /api/v1/files/{files[0].Id}/content"
            : $"{files.Count} imagens geradas: "
              + string.Join(", ", files.Select(f => $"/api/v1/files/{f.Id}/content"));

        // Primeira imagem no campo de topo para o renderer simples do cliente.
        return new BuiltinToolResult(
            text,
            new { imagePath = $"/api/v1/files/{files[0].Id}/content", images = images_ });
    }
}
