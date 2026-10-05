using Hawker.Core.Feeds;

namespace Hawker.Tests.Feeds;

public class LiveBlogDetectorTests
{
    private static FeedItem Item(string title, string url = "https://news.example.com/story") => new() { Title = title, Url = url };

    [Theory]
    [InlineData("Spain’s Sánchez calls snap election amid national housing crisis – Europe live")]
    [InlineData("Trump heads to Nebraska – US politics live")]
    [InlineData("Premier League: Arsenal v Chelsea - live")]
    [InlineData("LIVE: Storm Amy batters the coast")]
    [InlineData("Live updates: Election results")]
    [InlineData("Budget 2026 – as it happened")]
    public void Recognises_live_titles(string title)
    {
        Assert.True(LiveBlogDetector.IsLiveBlog(Item(title)));
    }

    [Theory]
    [InlineData("https://www.theguardian.com/world/live/2026/oct/05/europe-live")]
    [InlineData("https://www.bbc.co.uk/news/live/c1234567")]
    [InlineData("https://www.example.com/news/live-updates-storm")]
    public void Recognises_live_urls(string url)
    {
        Assert.True(LiveBlogDetector.IsLiveBlog(Item("Plain headline", url)));
    }

    [Theory]
    [InlineData("Lab worker ‘possibly’ dies of plague in Siberia", "https://www.theguardian.com/world/2026/oct/05/plague")]
    [InlineData("How to live well on a budget", "https://news.example.com/living/budget")]
    [InlineData("Live music venues face closure", "https://news.example.com/culture/live-music")]
    [InlineData("Where the wild things live", "https://news.example.com/nature/delivery-liver")]
    public void Ignores_ordinary_stories(string title, string url)
    {
        Assert.False(LiveBlogDetector.IsLiveBlog(Item(title, url)));
    }
}
