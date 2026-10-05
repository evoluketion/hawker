using Hawker.Core.Feeds;

namespace Hawker.Tests.Feeds;

public class FeedParserTests
{
    private static IReadOnlyList<FeedItem> ParseFixture(string name, string feedUrl)
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        return FeedParser.Parse(stream, new Uri(feedUrl), feedId: 7, feedName: "Fixture");
    }

    [Fact]
    public void Parses_hnrss_items_with_external_article_links()
    {
        var items = ParseFixture("hnrss-frontpage.xml", "https://hnrss.org/frontpage");

        Assert.Equal(5, items.Count);
        var first = items[0];
        Assert.Equal("Apple and a Hacker's Future", first.Title);
        Assert.Equal("https://stratechery.com/2026/apple-and-a-hackers-future/", first.Url);
        Assert.Equal("https://news.ycombinator.com/item?id=49962857", first.CommentsUrl);
        Assert.Equal("maguay", first.Author);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 10, 5, 2, TimeSpan.Zero), first.Published);
        Assert.All(items, i => Assert.Equal(7, i.FeedId));
        Assert.All(items, i => Assert.Equal("Fixture", i.FeedName));
    }

    [Fact]
    public void Parses_rss_content_images_and_relative_links()
    {
        var items = ParseFixture("news-rss.xml", "https://news.example.com/rss");

        Assert.Equal(["Council approves new bridge", "Weather turns cold", "Thumbnail only story", "Undated story"], items.Select(i => i.Title));

        var bridge = items[0];
        Assert.Equal("Jo Reporter", bridge.Author);
        Assert.Equal("https://cdn.example.com/bridge.jpg", bridge.ImageUrl);
        Assert.Contains("Work starts in spring", bridge.ContentHtml);
        Assert.Equal("<p>The council voted 7–2 in favour.</p>", bridge.SummaryHtml);

        var weather = items[1];
        Assert.Equal("https://news.example.com/weather/cold", weather.Url);
        // Untyped media:content (the Guardian's style) wins over the enclosure, widest first.
        Assert.Equal("https://cdn.example.com/frost-460.jpg", weather.ImageUrl);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 7, 0, 0, TimeSpan.FromHours(-4)), weather.Published);

        var thumb = items[2];
        Assert.Equal("https://news.example.com/thumb", thumb.Url);
        Assert.Equal("https://cdn.example.com/thumb.jpg", thumb.ImageUrl);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 22, 15, 0, TimeSpan.FromHours(1)), thumb.Published);

        Assert.Null(items[3].Published);
    }

    [Fact]
    public void Parses_atom_entries()
    {
        var items = ParseFixture("blog-atom.xml", "https://blog.example.org/feed.atom");

        Assert.Equal(2, items.Count);
        var post = items[0];
        Assert.Equal("Writing fast parsers & more", post.Title);
        Assert.Equal("https://blog.example.org/posts/parsers", post.Url);
        Assert.Equal("https://blog.example.org/posts/parsers#comments", post.CommentsUrl);
        Assert.Equal("https://blog.example.org/img/parsers.jpg", post.ImageUrl);
        Assert.Equal("Sam Author", post.Author);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 18, 45, 0, TimeSpan.FromHours(2)), post.Published);
        Assert.Contains("<strong>body</strong>", post.ContentHtml);

        var updatedOnly = items[1];
        Assert.Equal("https://blog.example.org/posts/updated", updatedOnly.Url);
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), updatedOnly.Published);
    }

    [Fact]
    public void Parses_rss1_rdf_items()
    {
        var item = Assert.Single(ParseFixture("legacy-rdf.xml", "https://old.example.net/index.rdf"));

        Assert.Equal("RDF item", item.Title);
        Assert.Equal("https://old.example.net/a", item.Url);
        Assert.Equal("Old Hand", item.Author);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero), item.Published);
    }

    [Fact]
    public void Rejects_documents_that_are_not_feeds()
    {
        using var stream = new MemoryStream("<html><body>Not a feed</body></html>"u8.ToArray());

        Assert.Throws<FormatException>(() => FeedParser.Parse(stream, new Uri("https://example.com/")));
    }

    [Theory]
    [InlineData("Mon, 05 Oct 2026 10:35:59 +0000", "2026-10-05T10:35:59+00:00")]
    [InlineData("Mon, 05 Oct 2026 10:35:59 GMT", "2026-10-05T10:35:59+00:00")]
    [InlineData("Mon, 5 Oct 2026 10:35:59 PST", "2026-10-05T10:35:59-08:00")]
    [InlineData("05 Oct 2026 10:35 +0530", "2026-10-05T10:35:00+05:30")]
    [InlineData("Monday, 05 October 2026 10:35:59 +0000", "2026-10-05T10:35:59+00:00")]
    [InlineData("Mon,  05 Oct 2026  10:35:59 Z", "2026-10-05T10:35:59+00:00")]
    [InlineData("Mon, 05 Oct 2026 10:35:59", "2026-10-05T10:35:59+00:00")]
    [InlineData("2026-10-05T10:35:59Z", "2026-10-05T10:35:59+00:00")]
    [InlineData("2026-10-05T12:35:59+02:00", "2026-10-05T12:35:59+02:00")]
    public void Parses_real_world_date_formats(string input, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected), FeedParser.ParseDate(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void Returns_null_for_unusable_dates(string? input)
    {
        Assert.Null(FeedParser.ParseDate(input));
    }
}
