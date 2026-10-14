using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;

namespace Hawker.Core.Articles;

public enum PageKind
{
    Article,

    /// <summary>An article in which a company announces its own product, release or deal: fine to print, but a poor lead.</summary>
    Announcement,

    /// <summary>A product, landing, tool, repository or other page that is not an article and should not lead the paper.</summary>
    NotArticle,
}

/// <param name="Kind">How the page should be treated in the layout.</param>
/// <param name="ClearlyNotArticle">
/// Not an article even allowing for hand-made pages without metadata; strict enough to leave the story out entirely.
/// </param>
public sealed record PageClassification(PageKind Kind, bool ClearlyNotArticle)
{
    public static readonly PageClassification Article = new(PageKind.Article, false);
}

/// <summary>What a page's own markup says about it, read once when the page is extracted.</summary>
/// <param name="OgArticle">og:type is "article".</param>
/// <param name="HasPublishedDate">A publication date in meta tags, structured data or a time element.</param>
/// <param name="ArticleStructuredData">Structured data declares an article type (NewsArticle, BlogPosting, …).</param>
/// <param name="ProductStructuredData">Structured data declares a product, offer, app or FAQ.</param>
public sealed record PageSignals(bool OgArticle, bool HasPublishedDate, bool ArticleStructuredData, bool ProductStructuredData)
{
    public bool HasArticleMetadata => OgArticle || HasPublishedDate || ArticleStructuredData;
}

/// <summary>
/// Tells articles from product pages, landing pages, tools and the like, so those never lead the paper. The rules and
/// thresholds come from 1,705 hand-labelled Hacker News links: product pages almost never carry article metadata or an
/// article-shaped URL, while articles almost always have one or the other.
/// </summary>
public static partial class PageClassifier
{
    /// <summary>Paragraphs this long are prose; a page with many of them is an article even without any metadata.</summary>
    public const int LongParagraphWords = 40;

    public const int ProseParagraphs = 10;

    private static readonly HashSet<string> ArticleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Article", "NewsArticle", "BlogPosting", "ReportageNewsArticle", "TechArticle", "ScholarlyArticle", "AnalysisNewsArticle",
        "OpinionNewsArticle", "LiveBlogPosting", "Report", "BackgroundNewsArticle", "ReviewNewsArticle", "SatiricalArticle", "Review",
    };

    private static readonly HashSet<string> ProductTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Product", "Offer", "AggregateOffer", "SoftwareApplication", "MobileApplication", "WebApplication", "FAQPage", "BuyAction",
        "ProductGroup", "IndividualProduct", "SoftwareSourceCode",
    };

    private static readonly string[] DateMeta =
    [
        "article:published_time", "datePublished", "date", "pubdate", "publish-date", "parsely-pub-date", "dc.date",
        "citation_publication_date", "citation_date",
    ];

    public static PageSignals ReadSignals(IHtmlDocument document, DateTime? readabilityDate)
    {
        string? Meta(string name) => document.QuerySelectorAll("meta")
            .FirstOrDefault(m => string.Equals(m.GetAttribute("property") ?? m.GetAttribute("name") ?? m.GetAttribute("itemprop"), name, StringComparison.OrdinalIgnoreCase))
            ?.GetAttribute("content");

        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var structuredDate = false;
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json' i]"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                CollectTypes(json.RootElement, 0, types, ref structuredDate);
            }
            catch (JsonException)
            {
                // Broken structured data says nothing either way.
            }
        }

        foreach (var element in document.QuerySelectorAll("[itemtype]"))
        {
            types.Add(element.GetAttribute("itemtype")!.TrimEnd('/').Split('/')[^1]);
        }

        var hasDate = readabilityDate is not null
            || structuredDate
            || DateMeta.Any(name => !string.IsNullOrWhiteSpace(Meta(name)))
            || document.QuerySelector("time[datetime]") is not null;

        return new PageSignals(
            string.Equals(Meta("og:type")?.Trim(), "article", StringComparison.OrdinalIgnoreCase),
            hasDate,
            types.Overlaps(ArticleTypes),
            types.Overlaps(ProductTypes));
    }

    /// <param name="paragraphWords">Word count of each paragraph of the extracted body.</param>
    /// <param name="opening">The first few paragraphs as plain text.</param>
    public static PageClassification Classify(string url, string title, PageSignals signals, IEnumerable<int> paragraphWords, string opening)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "";
        var articleUrl = HasArticleSlug(path) || DatedPath().IsMatch(path);
        var claimsProduct = signals.ProductStructuredData && !signals.ArticleStructuredData && !signals.OgArticle;

        if ((!signals.HasArticleMetadata && !articleUrl) || claimsProduct)
        {
            // Hand-made blogs often have no metadata at all, but they do have prose and a blog-like address.
            var prose = paragraphWords.Count(w => w >= LongParagraphWords) >= ProseParagraphs;
            var clearly = !prose && (claimsProduct || !BlogPath().IsMatch(path));
            return new PageClassification(PageKind.NotArticle, clearly);
        }

        var announcement = AnnouncementOpening().IsMatch(opening)
            || PressPath().IsMatch(path)
            || (LaunchWords().IsMatch(title) && VersionNumber().IsMatch(title));
        return announcement ? new PageClassification(PageKind.Announcement, false) : PageClassification.Article;
    }

    private static bool HasArticleSlug(string path)
    {
        var last = IndexPage().Replace(path, "").TrimEnd('/').Split('/')[^1];
        return last.Count(c => c == '-') >= 3 || last.Count(c => c == '_') >= 3;
    }

    private static void CollectTypes(JsonElement element, int depth, HashSet<string> types, ref bool hasDate)
    {
        if (depth > 6)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectTypes(item, depth + 1, types, ref hasDate);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("@type"))
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    types.Add(property.Value.GetString()!);
                }
                else if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    types.UnionWith(property.Value.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!));
                }
            }
            else if (property.NameEquals("datePublished") && depth <= 2)
            {
                hasDate = true;
            }
            else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                CollectTypes(property.Value, depth + 1, types, ref hasDate);
            }
        }
    }

    [GeneratedRegex(@"/index\.(html?|php)$", RegexOptions.IgnoreCase)]
    private static partial Regex IndexPage();

    // /2026/09/…, /2026-09-…, /20260915/
    [GeneratedRegex(@"/(19|20)\d\d[/-]\d{1,2}\b|/(19|20)\d{6}\b")]
    private static partial Regex DatedPath();

    [GeneratedRegex(@"/(blog|posts?|news|articles?|writing|p|story|stories|notes|essays?|archives?|entry|newsroom|press|research)/", RegexOptions.IgnoreCase)]
    private static partial Regex BlogPath();

    [GeneratedRegex(@"/(newsroom|press|press-releases?|releases?|announcements?|changelog)/", RegexOptions.IgnoreCase)]
    private static partial Regex PressPath();

    [GeneratedRegex(@"\b(today,? we|we('| a)re (excited|thrilled|pleased|proud|happy)|we('| a)re (introducing|announcing|launching|releasing)|is now available)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AnnouncementOpening();

    [GeneratedRegex(@"\b(introduc\w*|announc\w*|launch\w*|now (available|generally available|open.source)|released?|is here|joins?|acquires?|raises?|series [a-d])\b", RegexOptions.IgnoreCase)]
    private static partial Regex LaunchWords();

    [GeneratedRegex(@"\b\d+\.\d+(\.\d+)?\b|\bv\d")]
    private static partial Regex VersionNumber();
}
