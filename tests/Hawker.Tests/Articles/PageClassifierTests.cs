using AngleSharp.Html.Parser;
using Hawker.Core.Articles;

namespace Hawker.Tests.Articles;

public class PageClassifierTests
{
    private static PageSignals Signals(string head, string body = "") =>
        PageClassifier.ReadSignals(new HtmlParser().ParseDocument($"<html><head>{head}</head><body>{body}</body></html>"), null);

    private static readonly PageSignals None = new(false, false, false, false);

    private static PageClassification Classify(string url, PageSignals signals, int longParagraphs = 2, string title = "A headline", string opening = "") =>
        PageClassifier.Classify(url, title, signals, [.. Enumerable.Repeat(60, longParagraphs), 20, 20], opening);

    [Fact]
    public void Reads_article_metadata()
    {
        Assert.True(Signals("<meta property=\"og:type\" content=\" Article \">").OgArticle);
        Assert.True(Signals("<meta property=\"article:published_time\" content=\"2026-10-12\">").HasPublishedDate);
        Assert.True(Signals("", "<time datetime=\"2026-10-12\">12 October</time>").HasPublishedDate);
        Assert.True(Signals("<script type=\"application/ld+json\">{\"@graph\":[{\"@type\":\"WebPage\"},{\"@type\":[\"NewsArticle\"],\"datePublished\":\"2026-10-12\"}]}</script>")
            is { ArticleStructuredData: true, HasPublishedDate: true });
        Assert.True(Signals("<script type=\"application/ld+json\">{\"@type\":\"Product\",\"offers\":{\"@type\":\"Offer\"}}</script>").ProductStructuredData);
        Assert.Equal(None, Signals("<meta property=\"og:type\" content=\"website\"><script type=\"application/ld+json\">{ not json</script>"));
    }

    [Theory]
    [InlineData("https://deepmind.google/models/gemini/")]
    [InlineData("https://www.apple.com/iphone-duo/")]
    [InlineData("https://rea.tools/")]
    public void A_product_page_without_article_metadata_is_not_an_article(string url)
    {
        Assert.Equal(new PageClassification(PageKind.NotArticle, true), Classify(url, None));
    }

    [Fact]
    public void Article_metadata_or_an_article_shaped_address_makes_an_article()
    {
        Assert.Equal(PageClassification.Article, Classify("https://example.com/about", None with { OgArticle = true }));
        Assert.Equal(PageClassification.Article, Classify("https://example.com/about", None with { HasPublishedDate = true }));
        Assert.Equal(PageClassification.Article, Classify("https://example.com/how-the-bridge-was-built", None));
        Assert.Equal(PageClassification.Article, Classify("https://example.com/2026/10/bridge", None));
    }

    [Fact]
    public void Claiming_to_be_a_product_outweighs_a_date()
    {
        var product = None with { HasPublishedDate = true, ProductStructuredData = true };

        Assert.Equal(PageKind.NotArticle, Classify("https://shop.example.com/how-it-all-works-now", product).Kind);
        Assert.Equal(PageKind.Article, Classify("https://example.com/x", product with { OgArticle = true }).Kind);
    }

    [Fact]
    public void A_hand_made_blog_post_cannot_lead_but_is_not_left_out()
    {
        Assert.Equal(new PageClassification(PageKind.NotArticle, false), Classify("https://paulgraham.com/powerful.html", None, longParagraphs: 12));
        Assert.Equal(new PageClassification(PageKind.NotArticle, false), Classify("https://example.com/blog/sqldoom/", None));
    }

    [Theory]
    [InlineData("https://example.com/news/2026/10/thing-we-did-today", "Thing", "Today we're excited to share our biggest update yet.")]
    [InlineData("https://www.apple.com/newsroom/2026/09/apple-unveils-iphone-duo/", "iPhone Duo", "")]
    [InlineData("https://example.com/blog/a-big-day-for-all-of-us", "Introducing Widget 2.0", "")]
    public void Recognises_a_company_announcing_itself(string url, string title, string opening)
    {
        Assert.Equal(PageKind.Announcement, Classify(url, None with { OgArticle = true }, title: title, opening: opening).Kind);
    }

    [Fact]
    public void Ordinary_reporting_is_not_an_announcement()
    {
        var reporting = Classify("https://news.example.com/2026/10/council-approves-river-bridge", None with { OgArticle = true },
            title: "Council approves new river bridge", opening: "The council voted on Tuesday to approve the bridge.");

        Assert.Equal(PageClassification.Article, reporting);
    }
}
