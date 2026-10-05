using Hawker.Core.Domain;
using Hawker.Core.Feeds;
using Hawker.Core.Selection;

namespace Hawker.Tests.Selection;

public class ArticleSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<int, FeedRules> NoRules = [];
    private static readonly HashSet<string> NothingSeen = [];

    private static FeedItem Item(string slug, double hoursAgo, int feedId = 1, int? score = null) => new()
    {
        FeedId = feedId,
        Title = slug,
        Url = $"https://feed{feedId}.example.com/{slug}",
        Published = Now.AddHours(-hoursAgo),
        Score = score,
    };

    private static SelectionOptions Options(int count = 10, SortMode sort = SortMode.MostRecent, double lookbackHours = 24) =>
        new(count, sort, TimeSpan.FromHours(lookbackHours), Now);

    private static List<string> Titles(SelectionResult result) => result.Articles.Select(a => a.Item.Title).ToList();

    [Theory]
    [InlineData(24, new[] { "1h", "20h" })]
    [InlineData(48, new[] { "1h", "20h", "30h", "47h" })]
    [InlineData(168, new[] { "1h", "20h", "30h", "47h", "100h", "160h" })]
    public void Look_back_range_limits_how_old_articles_can_be(double lookbackHours, string[] expected)
    {
        var items = new[] { 1, 20, 30, 47, 100, 160, 200 }.Select(h => Item($"{h}h", h));

        var result = ArticleSelector.Select(items, Options(lookbackHours: lookbackHours), NoRules, NothingSeen);

        Assert.Equal(expected, Titles(result));
    }

    [Fact]
    public void Most_recent_takes_exactly_n_newest_first()
    {
        var items = Enumerable.Range(1, 20).Select(h => Item($"{h}h", h)).Reverse();

        var result = ArticleSelector.Select(items, Options(count: 5), NoRules, NothingSeen);

        Assert.Equal(["1h", "2h", "3h", "4h", "5h"], Titles(result));
        Assert.DoesNotContain(result.Notes, n => n.StartsWith("Only"));
    }

    [Fact]
    public void Short_pool_returns_what_it_has_and_notes_the_shortfall()
    {
        var items = new[] { Item("a", 1), Item("b", 2), Item("old", 30) };

        var result = ArticleSelector.Select(items, Options(count: 10), NoRules, NothingSeen);

        Assert.Equal(["a", "b"], Titles(result));
        Assert.Contains("Only 2 of 10 requested articles were available within the last 24 hours.", result.Notes);
        Assert.Contains("Skipped 1 item older than the look-back range.", result.Notes);
    }

    [Fact]
    public void Per_feed_cap_is_backfilled_from_other_feeds()
    {
        var busy = Enumerable.Range(1, 6).Select(h => Item($"busy{h}", h, feedId: 1));
        var quiet = new[] { Item("quiet10", 10, feedId: 2), Item("quiet11", 11, feedId: 2) };
        var rules = new Dictionary<int, FeedRules> { [1] = new(MaxItemsPerRun: 2, MinScore: null) };

        var result = ArticleSelector.Select(busy.Concat(quiet), Options(count: 4), rules, NothingSeen);

        Assert.Equal(["busy1", "busy2", "quiet10", "quiet11"], Titles(result));
        Assert.Contains(result.Notes, n => n.Contains("per-run cap"));
    }

    [Fact]
    public void Already_printed_articles_are_skipped()
    {
        var items = new[] { Item("a", 1), Item("b", 2), Item("c", 3) };
        var seen = new HashSet<string> { UrlNormalizer.Hash(items[0].Url) };

        var result = ArticleSelector.Select(items, Options(count: 2), NoRules, seen);

        Assert.Equal(["b", "c"], Titles(result));
    }

    [Fact]
    public void Same_story_from_two_feeds_appears_once()
    {
        var fromHn = Item("story", 2, feedId: 1, score: 300) with { Url = "https://site.example.com/story" };
        var fromSite = Item("story", 1, feedId: 2) with { Url = "https://www.site.example.com/story/?utm_source=rss" };

        var result = ArticleSelector.Select([fromSite, fromHn], Options(), NoRules, NothingSeen);

        var article = Assert.Single(result.Articles);
        Assert.Equal(300, article.Item.Score);
    }

    [Fact]
    public void Min_score_drops_low_scoring_items_but_not_unscored_ones()
    {
        var items = new[]
        {
            Item("low", 1, score: 40),
            Item("high", 2, score: 150),
            Item("unscored", 3),
        };
        var rules = new Dictionary<int, FeedRules> { [1] = new(MaxItemsPerRun: null, MinScore: 100) };

        var result = ArticleSelector.Select(items, Options(), rules, NothingSeen);

        Assert.Equal(["high", "unscored"], Titles(result));
    }

    [Fact]
    public void Undated_items_count_as_new_and_future_dates_are_clamped()
    {
        var undated = Item("undated", 0) with { Published = null };
        var future = Item("future", -48);
        var recent = Item("recent", 1);

        var result = ArticleSelector.Select([recent, undated, future], Options(lookbackHours: 12), NoRules, NothingSeen);

        Assert.Equal(3, result.Articles.Count);
        Assert.All(result.Articles.Where(a => a.Item.Title != "recent"), a => Assert.Equal(Now, a.EffectivePublished));
    }

    [Fact]
    public void Most_popular_uses_raw_scores_when_every_item_has_one()
    {
        var items = new[]
        {
            Item("hn26", 1, feedId: 1, score: 26),
            Item("hn219", 20, feedId: 1, score: 219),
            Item("best165", 5, feedId: 2, score: 165),
            Item("hn72", 3, feedId: 1, score: 72),
        };

        var result = ArticleSelector.Select(items, Options(count: 3, sort: SortMode.MostPopular), NoRules, NothingSeen);

        Assert.Equal(["hn219", "best165", "hn72"], Titles(result));
    }

    [Fact]
    public void Most_popular_breaks_score_ties_by_recency()
    {
        var items = new[] { Item("older", 5, score: 100), Item("newer", 1, score: 100) };

        var result = ArticleSelector.Select(items, Options(sort: SortMode.MostPopular), NoRules, NothingSeen);

        Assert.Equal(["newer", "older"], Titles(result));
    }

    [Fact]
    public void Most_popular_with_mixed_feeds_ranks_by_percentile_within_each_feed()
    {
        // Hacker News (scored) alongside a news site with no scores. Raw points would let HN take every slot;
        // percentiles give each feed's best stories a fair chance.
        var hn = new[]
        {
            Item("hn500", 10, feedId: 1, score: 500),
            Item("hn300", 1, feedId: 1, score: 300),
            Item("hn50", 2, feedId: 1, score: 50),
            Item("hn10", 3, feedId: 1, score: 10),
        };
        var news = new[]
        {
            Item("news-newest", 1, feedId: 2),
            Item("news-2nd", 4, feedId: 2),
            Item("news-3rd", 8, feedId: 2),
            Item("news-oldest", 12, feedId: 2),
        };

        var result = ArticleSelector.Select(hn.Concat(news), Options(count: 4, sort: SortMode.MostPopular), NoRules, NothingSeen);

        // Top of each feed first (percentile 1.0, tie broken by recency), then the second of each (0.75).
        Assert.Equal(["news-newest", "hn500", "hn300", "news-2nd"], Titles(result));
    }

    [Fact]
    public void Most_popular_puts_unscored_items_of_a_scored_feed_last_within_that_feed()
    {
        var items = new[]
        {
            Item("scored", 5, feedId: 1, score: 10),
            Item("missing", 1, feedId: 1),
            Item("other", 1, feedId: 2),
        };

        var result = ArticleSelector.Select(items, Options(count: 2, sort: SortMode.MostPopular), NoRules, NothingSeen);

        Assert.Equal(["other", "scored"], Titles(result));
    }

    [Theory]
    [InlineData(12, "12 hours")]
    [InlineData(24, "24 hours")]
    [InlineData(48, "2 days")]
    [InlineData(72, "3 days")]
    [InlineData(168, "week")]
    [InlineData(336, "2 weeks")]
    public void Describes_look_back_ranges_in_words(double hours, string expected)
    {
        Assert.Equal(expected, ArticleSelector.Describe(TimeSpan.FromHours(hours)));
    }
}
