using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Hawker.Core.Articles;

public sealed record ImageOptions
{
    public bool Greyscale { get; init; }

    /// <summary>Longest side after resizing. 1400px is ~300dpi across a 4.7in (12cm) wide lead image.</summary>
    public int MaxDimension { get; init; } = 1400;

    /// <summary>Anything narrower is an icon, avatar or tracking pixel rather than a news photo.</summary>
    public int MinWidth { get; init; } = 200;
    public int MinHeight { get; init; } = 100;

    /// <summary>Wider than this ratio is a banner or divider.</summary>
    public double MaxAspectRatio { get; init; } = 4;

    public long MaxDownloadBytes { get; init; } = 15 * 1024 * 1024;
    public int JpegQuality { get; init; } = 80;
}

/// <summary>Downloads an image and turns it into an embedded, print-sized JPEG so PDF rendering needs no network.</summary>
public sealed class ImageProcessor(HttpClient http, ILogger<ImageProcessor> logger)
{
    /// <returns>The image with <see cref="ArticleImage.DataUri"/> set, or null if it could not be used.</returns>
    public async Task<ArticleImage?> ProcessAsync(ArticleImage image, ImageOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await DownloadAsync(image.SourceUrl, options.MaxDownloadBytes, cancellationToken);
            if (bytes is null)
            {
                return null;
            }

            return Process(image, bytes, options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Skipping image {Url}", image.SourceUrl);
            return null;
        }
    }

    public static ArticleImage? Process(ArticleImage image, byte[] bytes, ImageOptions options)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return null;
        }

        // Animated GIFs and WebPs decode their first frame.
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var source = new SKBitmap(info);
        var result = codec.GetPixels(info, source.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }

        var rotated = codec.EncodedOrigin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom
            or SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightBottom;
        var (width, height) = rotated ? (info.Height, info.Width) : (info.Width, info.Height);

        if (width < options.MinWidth || height < options.MinHeight
            || (double)Math.Max(width, height) / Math.Min(width, height) > options.MaxAspectRatio)
        {
            return null;
        }

        var scale = Math.Min(1.0, (double)options.MaxDimension / Math.Max(width, height));
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

        using var surface = SKSurface.Create(new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;

        // JPEG has no transparency; flatten onto paper white rather than black.
        canvas.Clear(SKColors.White);
        ApplyOrientation(canvas, codec.EncodedOrigin, targetWidth, targetHeight);

        using var paint = new SKPaint();
        if (options.Greyscale)
        {
            paint.ColorFilter = SKColorFilter.CreateColorMatrix(GreyscaleMatrix);
        }

        var drawWidth = rotated ? targetHeight : targetWidth;
        var drawHeight = rotated ? targetWidth : targetHeight;
        using var sourceImage = SKImage.FromBitmap(source);
        canvas.DrawImage(sourceImage, new SKRect(0, 0, drawWidth, drawHeight), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var jpeg = snapshot.Encode(SKEncodedImageFormat.Jpeg, options.JpegQuality);

        return image with
        {
            DataUri = $"data:image/jpeg;base64,{Convert.ToBase64String(jpeg.AsSpan())}",
            Width = targetWidth,
            Height = targetHeight,
        };
    }

    // Rec. 709 luminance weights.
    private static readonly float[] GreyscaleMatrix =
    [
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0, 0, 0, 1, 0,
    ];

    /// <summary>Rotates the canvas so camera photos stored sideways (EXIF orientation) print upright.</summary>
    private static void ApplyOrientation(SKCanvas canvas, SKEncodedOrigin origin, int width, int height)
    {
        switch (origin)
        {
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, height);
                canvas.RotateDegrees(270);
                break;
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(width, height);
                canvas.RotateDegrees(270);
                canvas.Scale(1, -1);
                break;
        }
    }

    private async Task<byte[]?> DownloadAsync(string url, long maxBytes, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("Image {Url} returned {Status}", url, (int)response.StatusCode);
            return null;
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
