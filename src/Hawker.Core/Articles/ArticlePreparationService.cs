using Hawker.Core.Domain;
using Hawker.Core.Selection;

namespace Hawker.Core.Articles;

public sealed record PreparationResult(IReadOnlyList<Article> Articles, IReadOnlyList<string> Notes);

/// <summary>Turns the selected feed items into finished articles: full text, cleaned HTML and embedded images.</summary>
public sealed class ArticlePreparationService(PageExtractor extractor, ImageProcessor images)
{
    private const int MaxConcurrency = 4;

    /// <summary>Body images beyond this are dropped; a printed story rarely has room for more.</summary>
    private const int MaxBodyImages = 3;

    /// <summary>Hosts whose item pages are discussion threads rather than articles; the feed's own text is used instead.</summary>
    private static readonly string[] NoExtractHosts = ["news.ycombinator.com", "reddit.com", "lobste.rs"];

    public async Task<PreparationResult> PrepareAsync(
        IReadOnlyList<SelectedArticle> selected,
        IReadOnlyDictionary<int, Feed> feeds,
        ImageOptions imageOptions,
        CancellationToken cancellationToken = default)
    {
        using var gate = new SemaphoreSlim(MaxConcurrency);
        var tasks = selected.Select(async (article, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var fetchFullText = feeds.GetValueOrDefault(article.Item.FeedId)?.FetchFullText ?? true;
                return await PrepareOneAsync($"a{index + 1}", article, fetchFullText, imageOptions, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });

        var prepared = await Task.WhenAll(tasks);
        return new PreparationResult(prepared.Select(p => p.Article).ToList(), prepared.SelectMany(p => p.Notes).ToList());
    }

    private async Task<(Article Article, List<string> Notes)> PrepareOneAsync(
        string id, SelectedArticle selected, bool fetchFullText, ImageOptions imageOptions, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        ExtractedPage? page = null;
        if (fetchFullText && !IsDiscussionPage(selected.Item.Url))
        {
            page = await extractor.ExtractAsync(selected.Item.Url, cancellationToken);
            if (page is null)
            {
                notes.Add($"“{selected.Item.Title}”: the article page could not be read, so the feed's text was used.");
            }
        }

        var article = ArticleBuilder.Build(id, selected, page);
        if (article.BodySource == BodySource.None)
        {
            notes.Add($"“{article.Title}”: no article text was available, so it will print as a headline only.");
        }

        var bodyImages = await Task.WhenAll(article.BodyImages.Take(MaxBodyImages)
            .Select(i => images.ProcessAsync(i, imageOptions, cancellationToken)));
        var usable = bodyImages.OfType<ArticleImage>().ToList();

        var leads = await Task.WhenAll(article.LeadImageCandidates.Select(i => images.ProcessAsync(i, imageOptions, cancellationToken)));
        var lead = leads.OfType<ArticleImage>().MaxBy(i => i.Width * i.Height);
        if (lead is null && usable.Count > 0)
        {
            // The preferred lead failed (or was an icon); promote the first good body image instead.
            lead = usable[0] with { Id = $"{id}-lead" };
            usable.RemoveAt(0);
        }

        var usableIds = usable.Select(i => i.Id).ToHashSet();
        var blocks = article.Blocks.Where(b => b.Kind != BlockKind.Figure || usableIds.Contains(b.ImageId!)).ToList();

        return (article with { LeadImage = lead, LeadImageCandidates = [], BodyImages = usable, Blocks = blocks }, notes);
    }

    private static bool IsDiscussionPage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && NoExtractHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
}
