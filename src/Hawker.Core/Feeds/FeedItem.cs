namespace Hawker.Core.Feeds;

/// <summary>One entry read from a feed, before any full-text extraction.</summary>
public sealed record FeedItem
{
    public int FeedId { get; init; }
    public string FeedName { get; init; } = "";

    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Author { get; init; }

    /// <summary>Null when the feed gave no usable date.</summary>
    public DateTimeOffset? Published { get; init; }

    /// <summary>The item's description/summary as HTML.</summary>
    public string? SummaryHtml { get; init; }

    /// <summary>Full content supplied by the feed (content:encoded or Atom content), as HTML.</summary>
    public string? ContentHtml { get; init; }

    public string? ImageUrl { get; init; }
    public string? CommentsUrl { get; init; }

    /// <summary>Popularity score (e.g. Hacker News points), when the feed exposes one.</summary>
    public int? Score { get; init; }
    public int? CommentCount { get; init; }
}
