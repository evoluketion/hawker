using Hawker.Core.Domain;
using Hawker.Core.Selection;

namespace Hawker.Core.Articles;

/// <param name="LeftOut">URL hashes of stories left out because their feed skips pages that are not articles.</param>
public sealed record PreparationResult(IReadOnlyList<Article> Articles, IReadOnlyList<string> Notes, IReadOnlyList<string> LeftOut);

/// <summary>Turns the selected feed items into finished articles: full text, cleaned HTML and embedded images.</summary>
public sealed class ArticlePreparationService(PageExtractor extractor, ImageProcessor images)
{
    private const int MaxConcurrency = 4;

    /// <summary>Body images beyond this are dropped; a printed story rarely has room for more.</summary>
    private const int MaxBodyImages = 3;

    /// <summary>A lead candidate at least this wide is used without looking further down the preference list.</summary>
    private const int PreferredLeadWidth = 450;

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
                var feed = feeds.GetValueOrDefault(article.Item.FeedId);
                return await PrepareOneAsync($"a{index + 1}", article, feed?.FetchFullText ?? true, feed?.SkipNonArticlePages ?? false,
                    imageOptions, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });

        var prepared = await Task.WhenAll(tasks);
        return new PreparationResult(
            prepared.Where(p => !p.LeftOut).Select(p => p.Article).ToList(),
            prepared.SelectMany(p => p.Notes).ToList(),
            prepared.Where(p => p.LeftOut).Select(p => p.Article.UrlHash).ToList());
    }

    private async Task<(Article Article, List<string> Notes, bool LeftOut)> PrepareOneAsync(
        string id, SelectedArticle selected, bool fetchFullText, bool skipNonArticles, ImageOptions imageOptions, CancellationToken cancellationToken)
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
        if (skipNonArticles && article.Page.ClearlyNotArticle)
        {
            return (article, [$"“{article.Title}”: left out because the page is not an article."], true);
        }

        if (article.BodySource == BodySource.None)
        {
            notes.Add($"“{article.Title}”: no article text was available, so it will print as a headline only.");
        }

        var processed = new Dictionary<string, ArticleImage?>();
        foreach (var (image, result) in await ProcessAllAsync(article.BodyImages.Take(MaxBodyImages), imageOptions, cancellationToken))
        {
            processed[image.Id] = result;
        }

        // Lead: the first candidate big enough to print well wins; otherwise the biggest that worked.
        var leadResults = new List<ArticleImage>();
        foreach (var candidate in article.LeadImageCandidates)
        {
            if (!processed.TryGetValue(candidate.Id, out var result))
            {
                result = await images.ProcessAsync(candidate, imageOptions, cancellationToken);
            }

            if (result is not null)
            {
                leadResults.Add(result);
            }
        }

        var lead = leadResults.FirstOrDefault(i => i.Width >= PreferredLeadWidth) ?? leadResults.MaxBy(i => i.Width * i.Height);
        var usable = article.BodyImages.Select(i => processed.GetValueOrDefault(i.Id)).OfType<ArticleImage>().ToList();
        if (lead is null && usable.Count > 0)
        {
            // Every candidate failed (or was an icon); promote the first good body image instead.
            lead = usable[0];
        }

        if (lead is not null)
        {
            usable.RemoveAll(i => i.Id == lead.Id);
            lead = lead with { Id = $"{id}-lead" };
        }

        var usableIds = usable.Select(i => i.Id).ToHashSet();
        var blocks = article.Blocks.Where(b => b.Kind != BlockKind.Figure || usableIds.Contains(b.ImageId!)).ToList();

        return (article with { LeadImage = lead, LeadImageCandidates = [], BodyImages = usable, Blocks = blocks }, notes, false);
    }

    private async Task<(ArticleImage Image, ArticleImage? Result)[]> ProcessAllAsync(
        IEnumerable<ArticleImage> toProcess, ImageOptions imageOptions, CancellationToken cancellationToken) =>
        await Task.WhenAll(toProcess.Select(async i => (i, await images.ProcessAsync(i, imageOptions, cancellationToken))));

    private static bool IsDiscussionPage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && NoExtractHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
}
