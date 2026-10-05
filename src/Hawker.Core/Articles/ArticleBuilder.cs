using System.Text.RegularExpressions;
using Hawker.Core.Selection;

namespace Hawker.Core.Articles;

/// <summary>
/// Combines a feed item and (optionally) its extracted page into an <see cref="Article"/>: picks the fullest body
/// available and the lead image. Images are not downloaded here.
/// </summary>
public static partial class ArticleBuilder
{
    public static Article Build(string id, SelectedArticle selected, ExtractedPage? page)
    {
        var item = selected.Item;
        var baseUri = new Uri(item.Url);

        var candidates = new List<(BodySource Source, CleanedBody Body)>();
        if (page is { IsReadable: true, ContentHtml: { } pageHtml })
        {
            candidates.Add((BodySource.ExtractedPage, ArticleHtmlCleaner.Clean(pageHtml, baseUri, id, item.Title)));
        }

        candidates.Add((BodySource.FeedContent, ArticleHtmlCleaner.Clean(item.ContentHtml, baseUri, id, item.Title)));
        candidates.Add((BodySource.FeedSummary, ArticleHtmlCleaner.Clean(item.SummaryHtml, baseUri, id, item.Title)));

        // The fullest version wins (paywalled pages often extract to a teaser shorter than the feed's own content).
        // Earlier candidates win ties.
        var best = Fullest(candidates);

        // A page Readability was unsure about is used only when there is nothing else.
        if (best.Body is null && page is { IsReadable: false, ContentHtml: { } doubtfulHtml })
        {
            best = Fullest([(BodySource.ExtractedPage, ArticleHtmlCleaner.Clean(doubtfulHtml, baseUri, id, item.Title))]);
        }

        var source = best.Body is null ? BodySource.None : best.Source;
        var blocks = best.Body?.Blocks.ToList() ?? [];
        var bodyImages = best.Body?.Images.ToList() ?? [];

        var leadCandidates = ChooseLeadCandidates(id, [item.ImageUrl, page?.FeaturedImage], baseUri, blocks, bodyImages);

        return new Article
        {
            Id = id,
            Title = item.Title,
            Url = item.Url,
            UrlHash = selected.UrlHash,
            Source = item.FeedName,
            Author = CleanByline(item.Author ?? page?.Byline),
            Published = item.Published,
            Score = item.Score,
            CommentCount = item.CommentCount,
            CommentsUrl = item.CommentsUrl,
            Blocks = blocks,
            BodyImages = bodyImages,
            LeadImage = leadCandidates.FirstOrDefault(),
            LeadImageCandidates = leadCandidates,
            BodySource = source,
        };
    }

    private static (BodySource Source, CleanedBody? Body) Fullest(List<(BodySource Source, CleanedBody Body)> candidates) =>
        candidates
            .Select((c, index) => (c.Source, c.Body, Index: index, Words: c.Body.Blocks.Sum(b => b.WordCount)))
            .Where(c => c.Words > 0)
            .OrderByDescending(c => c.Words)
            .ThenBy(c => c.Index)
            .Select(c => (c.Source, (CleanedBody?)c.Body))
            .FirstOrDefault();

    /// <summary>
    /// Lead candidates are the feed's image and the page's featured image; failing both, the first image in the body.
    /// Any body image that duplicates a candidate is removed so it does not print twice.
    /// </summary>
    private static List<ArticleImage> ChooseLeadCandidates(string id, IEnumerable<string?> urls, Uri baseUri, List<ArticleBlock> blocks, List<ArticleImage> bodyImages)
    {
        var candidates = new List<ArticleImage>();
        foreach (var url in urls)
        {
            if (url is null || !Uri.TryCreate(baseUri, url, out var uri) || uri.Scheme is not ("http" or "https")
                || candidates.Any(c => c.SourceUrl == uri.AbsoluteUri))
            {
                continue;
            }

            var duplicate = bodyImages.FirstOrDefault(i => SameImage(i.SourceUrl, uri.AbsoluteUri));
            if (duplicate is not null)
            {
                RemoveFromBody(duplicate, blocks, bodyImages);
            }

            candidates.Add(new ArticleImage { Id = $"{id}-lead", SourceUrl = uri.AbsoluteUri, Caption = duplicate?.Caption });
        }

        if (candidates.Count > 0)
        {
            return candidates;
        }

        var firstFigure = blocks.FirstOrDefault(b => b.Kind == BlockKind.Figure);
        if (firstFigure is null)
        {
            return [];
        }

        var image = bodyImages.First(i => i.Id == firstFigure.ImageId);
        RemoveFromBody(image, blocks, bodyImages);
        return [image with { Id = $"{id}-lead" }];
    }

    private static void RemoveFromBody(ArticleImage image, List<ArticleBlock> blocks, List<ArticleImage> bodyImages)
    {
        blocks.RemoveAll(b => b.ImageId == image.Id);
        bodyImages.Remove(image);
    }

    /// <summary>Same picture at a different size often differs only in query string (e.g. ?width=460).</summary>
    private static bool SameImage(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub)
        && string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ua.AbsolutePath, ub.AbsolutePath, StringComparison.Ordinal);

    private static string? CleanByline(string? byline)
    {
        if (string.IsNullOrWhiteSpace(byline))
        {
            return null;
        }

        var cleaned = WhiteSpace().Replace(ByPrefix().Replace(byline.Trim(), ""), " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    [GeneratedRegex(@"^by\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ByPrefix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
