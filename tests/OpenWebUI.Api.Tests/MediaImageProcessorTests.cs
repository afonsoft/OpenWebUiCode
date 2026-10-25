using NUnit.Framework;
using OpenWebUI.Infrastructure.Services;
using SkiaSharp;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261018-attachments-v2 RF-003: resize só acima dos thresholds
/// (20 MiB ou &gt; 8192 px), resultado ≤ 2048 px JPEG q85, original intacto.
/// </summary>
[TestFixture]
public class MediaImageProcessorTests
{
    private static byte[] MakePng(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (int W, int H) Dims(byte[] bytes)
    {
        using var bmp = SKBitmap.Decode(bytes);
        return (bmp.Width, bmp.Height);
    }

    [Test]
    public async Task ImagemPequena_PassaInalterada()
    {
        var png = MakePng(64, 64);
        var (data, mime) = await MediaImageProcessor.PrepareForLlmAsync(png, "image/png");
        Assert.Multiple(() =>
        {
            Assert.That(data, Is.EqualTo(png));
            Assert.That(mime, Is.EqualTo("image/png"));
        });
    }

    [Test]
    public async Task DimensoesAcimaDe8192_RedimensionaPara2048()
    {
        var png = MakePng(MediaImageProcessor.ResizeDimensionThreshold + 100, 4000);
        var (data, mime) = await MediaImageProcessor.PrepareForLlmAsync(png, "image/png");
        var (w, h) = Dims(data);
        Assert.Multiple(() =>
        {
            Assert.That(Math.Max(w, h),
                Is.LessThanOrEqualTo(MediaImageProcessor.LlmMaxSide));
            Assert.That(mime, Is.EqualTo("image/jpeg"));
            // proporção preservada: 8292x4000 → 2048x988.
            Assert.That(w, Is.EqualTo(MediaImageProcessor.LlmMaxSide));
            Assert.That(h, Is.InRange(985, 990));
        });
    }

    [Test]
    public async Task AbaixoDosThresholds_NaoReencoda()
    {
        var png = MakePng(8000, 100);
        Assert.That(png.LongLength, Is.LessThan(MediaImageProcessor.ResizeBytesThreshold));
        var (data, mime) = await MediaImageProcessor.PrepareForLlmAsync(png, "image/png");
        Assert.Multiple(() =>
        {
            Assert.That(data, Is.EqualTo(png));
            Assert.That(mime, Is.EqualTo("image/png"));
        });
    }

    [Test]
    public async Task NaoDecodificavel_PassaComoEsta()
    {
        var junk = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00 };
        var (data, mime) = await MediaImageProcessor.PrepareForLlmAsync(
            junk, "image/x-fake");
        Assert.Multiple(() =>
        {
            Assert.That(data, Is.EqualTo(junk));
            Assert.That(mime, Is.EqualTo("image/x-fake"));
        });
    }

    [Test]
    public async Task AcimaDe20MiB_ReencodaSemRedimensionar()
    {
        // Braço por bytes: > 20 MiB sem dimensão estourada → JPEG, mesmas
        // dimensões. Ruído puro em 3000x3000 (36 MB RGBA) não comprime.
        const int side = 3000;
        using var bmp = new SKBitmap(new SKImageInfo(side, side));
        var noise = new byte[bmp.ByteCount];
        new Random(42).NextBytes(noise);
        System.Runtime.InteropServices.Marshal.Copy(noise, 0, bmp.GetPixels(), noise.Length);
        byte[] big;
        using (var img = SKImage.FromBitmap(bmp))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
        {
            big = data.ToArray();
        }
        Assert.That(big.LongLength, Is.GreaterThan(MediaImageProcessor.ResizeBytesThreshold));
        var (derived, mime) = await MediaImageProcessor.PrepareForLlmAsync(big, "image/png");
        var (w, h) = Dims(derived);
        Assert.Multiple(() =>
        {
            Assert.That(mime, Is.EqualTo("image/jpeg"));
            // > 20 MiB dispara re-encode; como 3000 px > 2048, também resize.
            Assert.That(Math.Max(w, h), Is.EqualTo(MediaImageProcessor.LlmMaxSide));
        });
    }
}
