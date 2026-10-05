namespace Hawker.Core.Articles;

/// <summary>
/// An article ready for layout. Every piece of text here comes from the source feed or page; nothing is generated.
/// </summary>
public sealed record Article
{
    /// <summary>Stable within one edition run ("a1", "a2", …); the layout editor refers to articles by this.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }
    public required string Url { get; init; }
    public required string UrlHash { get; init; }
    public required string Source { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset? Published { get; init; }
    public int? Score { get; init; }
    public int? CommentCount { get; init; }
    public string? CommentsUrl { get; init; }

    public IReadOnlyList<ArticleBlock> Blocks { get; init; } = [];

    /// <summary>The image chosen to sit at the top of the story, if any.</summary>
    public ArticleImage? LeadImage { get; init; }

    /// <summary>
    /// Possible lead images (the feed's and the page's), before download. Preparation keeps the largest that works,
    /// because feeds often supply only a small thumbnail.
    /// </summary>
    public IReadOnlyList<ArticleImage> LeadImageCandidates { get; init; } = [];

    /// <summary>Images that appear inside the body, referenced by <see cref="ArticleBlock.ImageId"/>.</summary>
    public IReadOnlyList<ArticleImage> BodyImages { get; init; } = [];

    public BodySource BodySource { get; init; }

    public int WordCount => Blocks.Sum(b => b.WordCount);
    public int ParagraphCount => Blocks.Count(b => b.Kind == BlockKind.Paragraph);
}

public enum BodySource
{
    /// <summary>No body text at all; the story prints as a headline brief.</summary>
    None,
    FeedSummary,
    FeedContent,
    ExtractedPage,
}

public enum BlockKind
{
    Paragraph,
    Heading,
    Quote,
    List,
    Figure,
    Preformatted,
}

/// <summary>One top-level piece of an article body, as sanitised HTML.</summary>
public sealed record ArticleBlock(BlockKind Kind, string Html, int WordCount, string? ImageId = null);

public sealed record ArticleImage
{
    public required string Id { get; init; }
    public required string SourceUrl { get; init; }
    public string? Caption { get; init; }

    /// <summary>Set once the image has been downloaded and processed; null means it is not printable.</summary>
    public string? DataUri { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}
