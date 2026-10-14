namespace Hawker.Core.Domain;

public class Feed
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Caps how many items this feed may contribute to one edition. Null means no cap.</summary>
    public int? MaxItemsPerRun { get; set; }

    /// <summary>Fetch the linked page and extract the full article rather than relying on the RSS summary.</summary>
    public bool FetchFullText { get; set; } = true;

    /// <summary>Leave out rolling live-update pages, which extract as long runs of timestamped fragments.</summary>
    public bool SkipLiveBlogs { get; set; } = true;

    /// <summary>Leave out stories whose page is clearly not an article: product and landing pages, tools, repositories.</summary>
    public bool SkipNonArticlePages { get; set; }

    public ScoreSource ScoreSource { get; set; } = ScoreSource.Auto;

    /// <summary>Used when <see cref="ScoreSource"/> is Regex. Must contain a named group "score"; "comments" is optional.</summary>
    public string? ScoreRegex { get; set; }

    /// <summary>Items with a score below this are skipped. Ignored for items without a score.</summary>
    public int? MinScore { get; set; }

    public List<Edition> Editions { get; set; } = [];
}

public enum ScoreSource
{
    /// <summary>Detect from the feed URL (e.g. hnrss.org), otherwise none.</summary>
    Auto,
    None,
    Hnrss,
    Regex,
}
