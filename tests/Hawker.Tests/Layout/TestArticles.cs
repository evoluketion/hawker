using Hawker.Core.Articles;

namespace Hawker.Tests.Layout;

internal static class TestArticles
{
    public const string TinyJpeg = "data:image/jpeg;base64,/9j/4AAQ";

    /// <param name="paragraphs">Word count of each paragraph.</param>
    public static Article Make(string id, string source = "Example News", int[]? paragraphs = null, bool image = true,
        BodySource body = BodySource.ExtractedPage, string? title = null, int? score = null, PageKind page = PageKind.Article)
    {
        var blocks = (paragraphs ?? [100, 100])
            .Select((words, i) => new ArticleBlock(BlockKind.Paragraph, $"<p>{id} paragraph {i + 1}: {string.Join(' ', Enumerable.Repeat("word", Math.Max(0, words - 3)))}</p>", words))
            .ToList();

        return new Article
        {
            Id = id,
            Title = title ?? $"Headline {id}",
            Url = $"https://news.example.com/{id}",
            UrlHash = id,
            Source = source,
            Blocks = paragraphs is { Length: 0 } ? [] : blocks,
            BodySource = paragraphs is { Length: 0 } ? BodySource.None : body,
            Score = score,
            Page = new PageClassification(page, page == PageKind.NotArticle),
            LeadImage = image
                ? new ArticleImage { Id = $"{id}-lead", SourceUrl = $"https://cdn.example.com/{id}.jpg", DataUri = TinyJpeg, Width = 1200, Height = 800 }
                : null,
        };
    }
}
