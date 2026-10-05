using Hawker.Core.Domain;
using Hawker.Core.Feeds;

namespace Hawker.Core.Selection;

public sealed record SelectionOptions(int ArticleCount, SortMode SortMode, TimeSpan Lookback, DateTimeOffset Now)
{
    public static SelectionOptions For(Edition edition, DateTimeOffset now) =>
        new(edition.ArticleCount, edition.SortMode, TimeSpan.FromHours(edition.LookbackHours), now);
}

/// <summary>Per-feed rules applied during selection.</summary>
public sealed record FeedRules(int? MaxItemsPerRun, int? MinScore, bool SkipLiveBlogs = false)
{
    public static FeedRules For(Feed feed) => new(feed.MaxItemsPerRun, feed.MinScore, feed.SkipLiveBlogs);
}

public sealed record SelectedArticle(FeedItem Item, string UrlHash, DateTimeOffset EffectivePublished);

public sealed record SelectionResult(IReadOnlyList<SelectedArticle> Articles, IReadOnlyList<string> Notes);

/// <summary>
/// Picks the articles for one edition: drops anything outside the look-back range, live blogs (where the feed asks),
/// anything below a feed's minimum score or already printed, removes duplicates, ranks by recency or popularity,
/// then takes the top N while honouring per-feed caps.
/// </summary>
public static class ArticleSelector
{
    public static SelectionResult Select(
        IEnumerable<FeedItem> items,
        SelectionOptions options,
        IReadOnlyDictionary<int, FeedRules> feedRules,
        IReadOnlySet<string> seenHashes)
    {
        var notes = new List<string>();
        var cutoff = options.Now - options.Lookback;
        var rulesFor = (int feedId) => feedRules.GetValueOrDefault(feedId) ?? new FeedRules(null, null);

        var all = items.Select(i => new SelectedArticle(
                i,
                UrlNormalizer.Hash(i.Url),
                // Undated items count as published now; future-dated ones are clamped so they cannot jump the queue.
                i.Published is { } p && p <= options.Now ? p : options.Now))
            .ToList();

        var inRange = all.Where(a => a.EffectivePublished >= cutoff).ToList();
        var notLive = inRange.Where(a => !rulesFor(a.Item.FeedId).SkipLiveBlogs || !LiveBlogDetector.IsLiveBlog(a.Item)).ToList();
        var aboveMin = notLive.Where(a => rulesFor(a.Item.FeedId).MinScore is not { } min || a.Item.Score is not { } s || s >= min).ToList();
        var unseen = aboveMin.Where(a => !seenHashes.Contains(a.UrlHash)).ToList();

        // The same story can arrive from two feeds; keep the copy with the higher score.
        var pool = unseen
            .GroupBy(a => a.UrlHash)
            .Select(g => g.OrderByDescending(a => a.Item.Score ?? int.MinValue).First())
            .ToList();

        AddNote(notes, all.Count - inRange.Count, "older than the look-back range");
        if (inRange.Count - notLive.Count is var live and > 0)
        {
            notes.Add($"Skipped {live} live blog{(live == 1 ? "" : "s")}.");
        }
        AddNote(notes, notLive.Count - aboveMin.Count, "below their feed's minimum score");
        AddNote(notes, aboveMin.Count - unseen.Count, "already printed in this edition");
        AddNote(notes, unseen.Count - pool.Count, "that duplicated another feed's story");

        var ranked = options.SortMode == SortMode.MostPopular ? RankByPopularity(pool) : RankByRecency(pool);

        var perFeed = new Dictionary<int, int>();
        var selected = new List<SelectedArticle>();
        var capped = 0;
        foreach (var article in ranked)
        {
            if (selected.Count == options.ArticleCount)
            {
                break;
            }

            var taken = perFeed.GetValueOrDefault(article.Item.FeedId);
            if (rulesFor(article.Item.FeedId).MaxItemsPerRun is { } cap && taken >= cap)
            {
                capped++;
                continue;
            }

            perFeed[article.Item.FeedId] = taken + 1;
            selected.Add(article);
        }

        AddNote(notes, capped, "because their feed hit its per-run cap");
        if (selected.Count < options.ArticleCount)
        {
            notes.Add($"Only {selected.Count} of {options.ArticleCount} requested articles were available within the last {Describe(options.Lookback)}.");
        }

        return new SelectionResult(selected, notes);
    }

    private static IEnumerable<SelectedArticle> RankByRecency(IEnumerable<SelectedArticle> pool) =>
        pool.OrderByDescending(a => a.EffectivePublished);

    private static IEnumerable<SelectedArticle> RankByPopularity(List<SelectedArticle> pool)
    {
        // When everything has a score, the scores are compared directly.
        if (pool.All(a => a.Item.Score.HasValue))
        {
            return pool.OrderByDescending(a => a.Item.Score).ThenByDescending(a => a.EffectivePublished);
        }

        // Otherwise raw scores are not comparable across feeds, so each item is given its percentile within its own feed:
        // by score where the feed has scores, by recency where it does not.
        var percentiles = new Dictionary<SelectedArticle, double>();
        foreach (var feed in pool.GroupBy(a => a.Item.FeedId))
        {
            var ordered = feed
                .OrderByDescending(a => a.Item.Score.HasValue)
                .ThenByDescending(a => a.Item.Score)
                .ThenByDescending(a => a.EffectivePublished)
                .ToList();
            for (var rank = 0; rank < ordered.Count; rank++)
            {
                percentiles[ordered[rank]] = (double)(ordered.Count - rank) / ordered.Count;
            }
        }

        return pool.OrderByDescending(a => percentiles[a]).ThenByDescending(a => a.EffectivePublished);
    }

    private static void AddNote(List<string> notes, int count, string reason)
    {
        if (count > 0)
        {
            notes.Add($"Skipped {count} item{(count == 1 ? "" : "s")} {reason}.");
        }
    }

    public static string Describe(TimeSpan span) => span.TotalHours switch
    {
        var h when h % 168 == 0 => h == 168 ? "week" : $"{h / 168:0} weeks",
        var h when h % 24 == 0 => h == 24 ? "24 hours" : $"{h / 24:0} days",
        var h => $"{h:0} hours",
    };
}
