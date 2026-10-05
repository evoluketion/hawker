using Hawker.Core.Articles;
using SkiaSharp;

namespace Hawker.Tests.Articles;

public class ImageProcessorTests
{
    private static readonly ArticleImage Source = new() { Id = "a1-lead", SourceUrl = "https://cdn.example.com/x.png" };

    internal static byte[] MakeImage(int width, int height, SKColor color, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static SKBitmap Decode(ArticleImage image)
    {
        var base64 = image.DataUri!["data:image/jpeg;base64,".Length..];
        return SKBitmap.Decode(Convert.FromBase64String(base64));
    }

    [Fact]
    public void Large_images_are_resized_to_fit_and_embedded_as_jpeg()
    {
        var result = ImageProcessor.Process(Source, MakeImage(3000, 2000, SKColors.SteelBlue), new ImageOptions());

        Assert.NotNull(result);
        Assert.StartsWith("data:image/jpeg;base64,", result.DataUri);
        Assert.Equal((1400, 933), (result.Width, result.Height));
        using var decoded = Decode(result);
        Assert.Equal((1400, 933), (decoded.Width, decoded.Height));
    }

    [Fact]
    public void Small_images_are_not_enlarged()
    {
        var result = ImageProcessor.Process(Source, MakeImage(640, 480, SKColors.SteelBlue), new ImageOptions());

        Assert.Equal((640, 480), (result!.Width, result.Height));
    }

    [Fact]
    public void Greyscale_option_removes_colour()
    {
        var result = ImageProcessor.Process(Source, MakeImage(400, 300, new SKColor(200, 40, 40)), new ImageOptions { Greyscale = true });

        using var decoded = Decode(result!);
        var pixel = decoded.GetPixel(200, 150);
        Assert.InRange(Math.Abs(pixel.Red - pixel.Green), 0, 3);
        Assert.InRange(Math.Abs(pixel.Green - pixel.Blue), 0, 3);
    }

    [Fact]
    public void Transparent_areas_print_white()
    {
        var result = ImageProcessor.Process(Source, MakeImage(400, 300, SKColors.Transparent), new ImageOptions());

        using var decoded = Decode(result!);
        Assert.True(decoded.GetPixel(10, 10).Red > 245);
    }

    [Theory]
    [InlineData(150, 150)]   // icon / avatar
    [InlineData(1, 1)]       // tracking pixel
    [InlineData(1200, 150)]  // banner
    [InlineData(400, 80)]    // too short
    public void Icons_pixels_and_banners_are_rejected(int width, int height)
    {
        Assert.Null(ImageProcessor.Process(Source, MakeImage(width, height, SKColors.Gray), new ImageOptions()));
    }

    [Fact]
    public void Undecodable_bytes_are_rejected()
    {
        Assert.Null(ImageProcessor.Process(Source, "<html>not an image</html>"u8.ToArray(), new ImageOptions()));
    }

    [Fact]
    public void Webp_and_jpeg_sources_are_accepted()
    {
        Assert.NotNull(ImageProcessor.Process(Source, MakeImage(400, 300, SKColors.Gray, SKEncodedImageFormat.Webp), new ImageOptions()));
        Assert.NotNull(ImageProcessor.Process(Source, MakeImage(400, 300, SKColors.Gray, SKEncodedImageFormat.Jpeg), new ImageOptions()));
    }
}
