using Hawker.Web.Tray;

namespace Hawker.Tests.Tray;

public sealed class TrayIconImageTests
{
    [Fact]
    public void The_embedded_icon_is_a_png()
    {
        var png = TrayIconImage.ReadPng();

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    public void Renders_four_bytes_per_pixel_with_something_visible(int size)
    {
        var bgra = TrayIconImage.RenderBgra(size);
        var argb = TrayIconImage.RenderArgb(size);

        Assert.Equal(size * size * 4, bgra.Length);
        Assert.Equal(size * size * 4, argb.Length);
        Assert.Contains(bgra.Where((_, i) => i % 4 == 3), alpha => alpha == 255);
        Assert.Contains(argb.Where((_, i) => i % 4 == 0), alpha => alpha == 255);
    }

    [Fact]
    public void Argb_is_bgra_with_each_pixel_reversed()
    {
        var bgra = TrayIconImage.RenderBgra(16);
        var argb = TrayIconImage.RenderArgb(16);

        for (var i = 0; i < bgra.Length; i += 4)
        {
            Assert.Equal(bgra.AsSpan(i, 4).ToArray().Reverse(), argb.AsSpan(i, 4).ToArray());
        }
    }

    [Fact]
    public void The_corners_stay_transparent()
    {
        var bgra = TrayIconImage.RenderBgra(32);

        Assert.Equal(0, bgra[3]);
    }
}
