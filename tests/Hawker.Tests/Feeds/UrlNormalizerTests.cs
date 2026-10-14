using Hawker.Core.Feeds;

namespace Hawker.Tests.Feeds;

public class UrlNormalizerTests
{
    [Theory]
    [InlineData("https://www.Example.com/news/story/", "example.com/news/story")]
    [InlineData("http://example.com/news/story#comments", "example.com/news/story")]
    [InlineData("https://example.com/story?utm_source=rss&utm_medium=feed&id=5", "example.com/story?id=5")]
    [InlineData("https://example.com/story?b=2&a=1", "example.com/story?a=1&b=2")]
    [InlineData("https://example.com:8443/x", "example.com:8443/x")]
    [InlineData("https://example.com/", "example.com")]
    public void Normalizes_equivalent_urls(string input, string expected)
    {
        Assert.Equal(expected, UrlNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("https://www.example.com/story?utm_source=rss&utm_medium=feed", "https://www.example.com/story")]
    [InlineData("https://example.com/story?b=2&fbclid=abc&a=1", "https://example.com/story?b=2&a=1")]
    [InlineData("https://example.com/story?UTM_Campaign=x&id=5#comments", "https://example.com/story?id=5#comments")]
    [InlineData("https://example.com/story?id=5", "https://example.com/story?id=5")]
    [InlineData("https://example.com/story", "https://example.com/story")]
    [InlineData("not a url?utm_source=rss", "not a url?utm_source=rss")]
    public void Strips_only_tracking_parameters(string input, string expected)
    {
        Assert.Equal(expected, UrlNormalizer.StripTracking(input));
    }

    [Fact]
    public void Equivalent_urls_share_a_hash()
    {
        Assert.Equal(
            UrlNormalizer.Hash("https://www.example.com/story/?utm_campaign=x"),
            UrlNormalizer.Hash("http://example.com/story"));
        Assert.NotEqual(UrlNormalizer.Hash("https://example.com/a"), UrlNormalizer.Hash("https://example.com/b"));
        Assert.Equal(64, UrlNormalizer.Hash("https://example.com/a").Length);
    }
}
