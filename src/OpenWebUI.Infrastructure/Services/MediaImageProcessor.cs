using SkiaSharp;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// SPEC-20261018-attachments-v2 RF-003: antes de enviar uma imagem ao
/// modelo, arquivos acima de 20 MiB ou com dimensão &gt; 8192 px são
/// redimensionados para no máximo 2048 px no maior lado (mantendo a
/// proporção) e re-encodados como JPEG qualidade 85. O original fica
/// intacto no storage — o derivado existe apenas em memória para a
/// chamada do LLM. Imagens pequenas passam inalteradas.
/// </summary>
public static class MediaImageProcessor
{
    /// <summary>Bytes acima dos quais a imagem é sempre re-encodada (20 MiB).</summary>
    public const long ResizeBytesThreshold = 20L * 1024 * 1024;

    /// <summary>Dimensão acima da qual a imagem é sempre re-encodada.</summary>
    public const int ResizeDimensionThreshold = 8192;

    /// <summary>Maior lado da versão enviada ao modelo.</summary>
    public const int LlmMaxSide = 2048;

    /// <summary>Qualidade JPEG do derivado para o modelo.</summary>
    public const int LlmJpegQuality = 85;

    /// <summary>
    /// Produz os bytes enviados ao modelo: imagens grandes são
    /// redimensionadas/re-encodadas (<c>image/jpeg</c>), as demais voltam
    /// como estão (com o MIME original). Formatos não decodificáveis
    /// pelo Skia passam sem alteração.
    /// </summary>
    /// <param name="bytes">Bytes originais do storage.</param>
    /// <param name="contentType">MIME registrado no upload.</param>
    /// <param name="ct">Cancelamento.</param>
    /// <returns>(dados, mime) para o payload do modelo.</returns>
    public static Task<(byte[] Data, string Mime)> PrepareForLlmAsync(
        byte[] bytes, string contentType, CancellationToken ct = default)
    {
        SKBitmap? decoded;
        try
        {
            decoded = SKBitmap.Decode(bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            decoded = null;
        }
        if (decoded is null)
        {
            // Formato não decodificável — segue com o original.
            return Task.FromResult((bytes, contentType));
        }
        using (decoded)
        {
            var needsResize = bytes.LongLength > ResizeBytesThreshold
                || decoded.Width > ResizeDimensionThreshold
                || decoded.Height > ResizeDimensionThreshold;
            if (!needsResize)
            {
                return Task.FromResult((bytes, contentType));
            }
            var work = decoded;
            if (work.Width > LlmMaxSide || work.Height > LlmMaxSide)
            {
                var scale = (double)LlmMaxSide / Math.Max(work.Width, work.Height);
                var target = new SKImageInfo(
                    (int)Math.Round(work.Width * scale),
                    (int)Math.Round(work.Height * scale));
                work = decoded.Resize(target, SKSamplingOptions.Default);
            }
            try
            {
                using var image = SKImage.FromBitmap(work);
                using var jpeg = image.Encode(
                    SKEncodedImageFormat.Jpeg, LlmJpegQuality);
                return Task.FromResult((jpeg.ToArray(), "image/jpeg"));
            }
            finally
            {
                if (!ReferenceEquals(work, decoded))
                {
                    work.Dispose();
                }
            }
        }
    }
}
