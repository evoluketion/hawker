using Hawker.Core.Articles;
using Hawker.Core.Feeds;
using Hawker.Core.Selection;

namespace Hawker.Tests.Articles;

public class ArticleBuilderTests
{
    private static SelectedArticle Selected(FeedItem item) =>
        new(item, UrlNormalizer.Hash(item.Url), item.Published ?? DateTimeOffset.UnixEpoch);

    private static FeedItem Item(string? summary = null, string? content = null, string? imageUrl = null, string? author = null) => new()
    {
        FeedId = 1,
        FeedName = "Example News",
        Title = "Bridge approved",
        Url = "https://news.example.com/bridge",
        SummaryHtml = summary,
        ContentHtml = content,
        ImageUrl = imageUrl,
        Author = author,
    };

    private static string Words(int count) => string.Join(' ', Enumerable.Repeat("word", count));

    [Fact]
    public void Uses_the_extracted_page_when_it_is_the_fullest_text()
    {
        var page = new ExtractedPage($"<p>{Words(300)}</p><p>{Words(200)}</p>", "By Jo Reporter", null, IsReadable: true);

        var article = ArticleBuilder.Build("a1", Selected(Item(summary: Words(30))), page);

        Assert.Equal(BodySource.ExtractedPage, article.BodySource);
        Assert.Equal(500, article.WordCount);
        Assert.Equal(2, article.ParagraphCount);
        Assert.Equal("Jo Reporter", article.Author);
        Assert.Equal("Example News", article.Source);
    }

    [Fact]
    public void Falls_back_to_feed_content_when_the_page_is_a_paywall_teaser()
    {
        var page = new ExtractedPage($"<p>{Words(40)}</p>", null, null, IsReadable: true);

        var article = ArticleBuilder.Build("a1", Selected(Item(summary: Words(20), content: $"<p>{Words(400)}</p>")), page);

        Assert.Equal(BodySource.FeedContent, article.BodySource);
        Assert.Equal(400, article.WordCount);
    }

    [Fact]
    public void Prefers_the_page_when_lengths_tie()
    {
        var page = new ExtractedPage($"<p>{Words(50)}</p>", null, null, IsReadable: true);

        var article = ArticleBuilder.Build("a1", Selected(Item(content: $"<p>{Words(50)}</p>")), page);

        Assert.Equal(BodySource.ExtractedPage, article.BodySource);
    }

    [Fact]
    public void An_unreadable_page_is_used_only_when_nothing_better_exists()
    {
        var doubtful = new ExtractedPage($"<p>{Words(100)}</p>", null, null, IsReadable: false);

        Assert.Equal(BodySource.FeedSummary, ArticleBuilder.Build("a1", Selected(Item(summary: Words(10))), doubtful).BodySource);
        Assert.Equal(BodySource.ExtractedPage, ArticleBuilder.Build("a1", Selected(Item()), doubtful).BodySource);
    }

    [Fact]
    public void No_text_anywhere_gives_a_headline_only_article()
    {
        var article = ArticleBuilder.Build("a1", Selected(Item()), page: null);

        Assert.Equal(BodySource.None, article.BodySource);
        Assert.Empty(article.Blocks);
        Assert.Equal("Bridge approved", article.Title);
    }

    [Fact]
    public void Feed_image_is_the_lead_and_its_copy_is_removed_from_the_body()
    {
        var content = $"""
            <figure><img src="https://cdn.example.com/bridge.jpg?width=1200"><figcaption>The bridge site.</figcaption></figure>
            <p>{Words(50)}</p>
            <figure><img src="https://cdn.example.com/other.jpg"></figure>
            """;

        var article = ArticleBuilder.Build("a3", Selected(Item(content: content, imageUrl: "https://cdn.example.com/bridge.jpg?width=140")), null);

        Assert.Equal("a3-lead", article.LeadImage!.Id);
        Assert.Equal("https://cdn.example.com/bridge.jpg?width=140", article.LeadImage.SourceUrl);
        Assert.Equal("The bridge site.", article.LeadImage.Caption);
        var bodyImage = Assert.Single(article.BodyImages);
        Assert.Equal("https://cdn.example.com/other.jpg", bodyImage.SourceUrl);
        Assert.Equal([BlockKind.Paragraph, BlockKind.Figure], article.Blocks.Select(b => b.Kind));
    }

    [Fact]
    public void Page_featured_image_is_used_when_the_feed_has_none()
    {
        var page = new ExtractedPage($"<p>{Words(50)}</p>", null, "/images/og.jpg", IsReadable: true);

        var article = ArticleBuilder.Build("a1", Selected(Item()), page);

        Assert.Equal("https://news.example.com/images/og.jpg", article.LeadImage!.SourceUrl);
    }

    [Fact]
    public void First_body_figure_becomes_the_lead_when_nothing_else_is_offered()
    {
        var content = $"""<p>{Words(20)}</p><figure><img src="https://cdn.example.com/first.jpg"><figcaption>First.</figcaption></figure>""";

        var article = ArticleBuilder.Build("a1", Selected(Item(content: content)), null);

        Assert.Equal("https://cdn.example.com/first.jpg", article.LeadImage!.SourceUrl);
        Assert.Equal("First.", article.LeadImage.Caption);
        Assert.Empty(article.BodyImages);
        Assert.DoesNotContain(article.Blocks, b => b.Kind == BlockKind.Figure);
    }
}
