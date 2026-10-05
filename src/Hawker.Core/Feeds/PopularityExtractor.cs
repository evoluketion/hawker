using System.Globalization;
using System.Text.RegularExpressions;
using Hawker.Core.Domain;

namespace Hawker.Core.Feeds;

/// <summary>Reads a popularity score (points, votes) and comment count out of a feed item.</summary>
public interface IPopularityExtractor
{
    FeedItem Apply(FeedItem item);
}

public static class PopularityExtractor
{
    public static IPopularityExtractor For(Feed feed) => ResolveSource(feed) switch
    {
        ScoreSource.Hnrss => HnrssPopularityExtractor.Instance,
        ScoreSource.Regex when !string.IsNullOrWhiteSpace(feed.ScoreRegex) => new RegexPopularityExtractor(feed.ScoreRegex),
        _ => NoPopularityExtractor.Instance,
    };

    public static ScoreSource ResolveSource(Feed feed)
    {
        if (feed.ScoreSource != ScoreSource.Auto)
        {
            return feed.ScoreSource;
        }

        return Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri) && uri.Host.EndsWith("hnrss.org", StringComparison.OrdinalIgnoreCase)
            ? ScoreSource.Hnrss
            : ScoreSource.None;
    }
}

public sealed class NoPopularityExtractor : IPopularityExtractor
{
    public static readonly NoPopularityExtractor Instance = new();

    public FeedItem Apply(FeedItem item) => item;
}

/// <summary>
/// hnrss.org appends a block of &lt;p&gt; lines to each description: Article URL, Comments URL, Points and # Comments.
/// The numbers are lifted into Score/CommentCount and the block is removed so it never reaches the printed page.
/// </summary>
public sealed partial class HnrssPopularityExtractor : IPopularityExtractor
{
    public static readonly HnrssPopularityExtractor Instance = new();

    public FeedItem Apply(FeedItem item)
    {
        if (item.SummaryHtml is null)
        {
            return item;
        }

        var points = Points().Match(item.SummaryHtml);
        var comments = Comments().Match(item.SummaryHtml);

        var summary = MetadataLine().Replace(item.SummaryHtml, "");
        summary = TrailingRule().Replace(summary, "").Trim();

        return item with
        {
            Score = points.Success ? int.Parse(points.Groups[1].Value, CultureInfo.InvariantCulture) : item.Score,
            CommentCount = comments.Success ? int.Parse(comments.Groups[1].Value, CultureInfo.InvariantCulture) : item.CommentCount,
            SummaryHtml = summary.Length == 0 ? null : summary,
        };
    }

    [GeneratedRegex(@"<p>\s*Points:\s*(\d+)\s*</p>", RegexOptions.IgnoreCase)]
    private static partial Regex Points();

    [GeneratedRegex(@"<p>\s*#\s*Comments:\s*(\d+)\s*</p>", RegexOptions.IgnoreCase)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<p>\s*(Article URL|Comments URL|Points|#\s*Comments):.*?</p>\s*", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetadataLine();

    // Self posts (Ask HN) put an <hr> between the post text and the metadata block.
    [GeneratedRegex(@"<hr\s*/?>\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingRule();
}

/// <summary>A user-supplied pattern run over the item's description. Needs a named group "score"; "comments" is optional.</summary>
public sealed class RegexPopularityExtractor : IPopularityExtractor
{
    private readonly Regex _regex;

    public RegexPopularityExtractor(string pattern)
    {
        _regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
        if (!_regex.GetGroupNames().Contains("score"))
        {
            throw new ArgumentException("The score pattern must contain a named group called 'score', e.g. (?<score>\\d+).", nameof(pattern));
        }
    }

    public FeedItem Apply(FeedItem item)
    {
        var text = $"{item.Title}\n{item.SummaryHtml}\n{item.ContentHtml}";
        Match match;
        try
        {
            match = _regex.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return item;
        }

        if (!match.Success)
        {
            return item;
        }

        return item with
        {
            Score = ParseNumber(match.Groups["score"]) ?? item.Score,
            CommentCount = ParseNumber(match.Groups["comments"]) ?? item.CommentCount,
        };
    }

    private static int? ParseNumber(Group group) =>
        group.Success && int.TryParse(group.Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
