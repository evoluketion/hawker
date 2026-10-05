using Hawker.Core.Domain;
using Hawker.Core.Feeds;

namespace Hawker.Tests.Feeds;

public class PopularityExtractorTests
{
    private static FeedItem Item(string? summary, string title = "Story") =>
        new() { Title = title, Url = "https://example.com/story", SummaryHtml = summary };

    [Fact]
    public void Hnrss_fixture_yields_points_and_comment_counts()
    {
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hnrss-frontpage.xml"));
        var items = FeedParser.Parse(stream, new Uri("https://hnrss.org/frontpage"))
            .Select(HnrssPopularityExtractor.Instance.Apply)
            .ToList();

        Assert.Equal([26, 165, 72, 70, 219], items.Select(i => i.Score));
        Assert.Equal([9, 109, 50, 37, 188], items.Select(i => i.CommentCount));

        // Link posts carry nothing but the metadata block, so the summary is removed entirely.
        Assert.All(items, i => Assert.Null(i.SummaryHtml));
    }

    [Fact]
    public void Hnrss_self_post_keeps_its_text_but_loses_the_metadata()
    {
        var summary = """
            <p>I built a thing and would love feedback.</p>
            <hr>
            <p>Comments URL: <a href="https://news.ycombinator.com/item?id=1">https://news.ycombinator.com/item?id=1</a></p>
            <p>Points: 42</p>
            <p># Comments: 7</p>
            """;

        var item = HnrssPopularityExtractor.Instance.Apply(Item(summary, "Ask HN: Thoughts?"));

        Assert.Equal(42, item.Score);
        Assert.Equal(7, item.CommentCount);
        Assert.Equal("<p>I built a thing and would love feedback.</p>", item.SummaryHtml);
    }

    [Fact]
    public void Hnrss_item_without_metadata_is_unchanged()
    {
        var item = Item("<p>Just text.</p>");

        Assert.Equal(item, HnrssPopularityExtractor.Instance.Apply(item));
    }

    [Theory]
    [InlineData("https://hnrss.org/frontpage", ScoreSource.Auto, ScoreSource.Hnrss)]
    [InlineData("https://hnrss.org/newest?points=100", ScoreSource.Auto, ScoreSource.Hnrss)]
    [InlineData("https://feeds.bbci.co.uk/news/rss.xml", ScoreSource.Auto, ScoreSource.None)]
    [InlineData("https://hnrss.org/frontpage", ScoreSource.None, ScoreSource.None)]
    [InlineData("https://example.com/rss", ScoreSource.Regex, ScoreSource.Regex)]
    public void Auto_detects_hnrss_from_the_url(string url, ScoreSource configured, ScoreSource expected)
    {
        var feed = new Feed { Name = "F", Url = url, ScoreSource = configured };

        Assert.Equal(expected, PopularityExtractor.ResolveSource(feed));
    }

    [Fact]
    public void Regex_extractor_reads_score_and_comments_groups()
    {
        var extractor = new RegexPopularityExtractor(@"(?<score>[\d,]+)\s+votes.*?(?<comments>\d+)\s+replies");

        var item = extractor.Apply(Item("<span>1,204 votes</span> · <span>88 replies</span>"));

        Assert.Equal(1204, item.Score);
        Assert.Equal(88, item.CommentCount);
    }

    [Fact]
    public void Regex_extractor_leaves_unmatched_items_alone()
    {
        var item = Item("no numbers here");

        Assert.Equal(item, new RegexPopularityExtractor(@"(?<score>\d+) votes").Apply(item));
    }

    [Fact]
    public void Regex_without_a_score_group_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new RegexPopularityExtractor(@"(\d+) votes"));
    }

    [Fact]
    public void Regex_feed_without_a_pattern_falls_back_to_no_scores()
    {
        var feed = new Feed { Name = "F", Url = "https://example.com/rss", ScoreSource = ScoreSource.Regex };

        Assert.IsType<NoPopularityExtractor>(PopularityExtractor.For(feed));
    }
}
