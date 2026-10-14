using SkiaSharp;

namespace Hawker.Web.Tray;

/// <summary>
/// The tray icon, rendered from the one embedded PNG at whatever size the platform asks for. Pixels are straight
/// (unpremultiplied) alpha, which both Windows icons and StatusNotifierItem pixmaps expect.
/// </summary>
public static class TrayIconImage
{
    private const string ResourceName = "Hawker.Tray.png";

    public static byte[] ReadPng()
    {
        using var stream = typeof(TrayIconImage).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Rows top to bottom, each pixel B, G, R, A: the layout of a 32-bit Windows DIB.</summary>
    public static byte[] RenderBgra(int size) => Render(size);

    /// <summary>Each pixel A, R, G, B: the network-byte-order ARGB32 that a StatusNotifierItem IconPixmap carries.</summary>
    public static byte[] RenderArgb(int size)
    {
        var pixels = Render(size);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]);
        }

        return pixels;
    }

    private static byte[] Render(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        using var source = SKImage.FromEncodedData(ReadPng())
            ?? throw new InvalidOperationException($"{ResourceName} is not a readable image.");
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            // Mipmaps keep thin lines legible when 256 px shrinks to 16.
            canvas.DrawImage(source, new SKRect(0, 0, size, size), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        using var unpremul = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        if (!bitmap.PeekPixels().ReadPixels(unpremul.PeekPixels()))
        {
            throw new InvalidOperationException("Could not convert the tray icon pixels.");
        }

        return unpremul.Bytes;
    }
}
