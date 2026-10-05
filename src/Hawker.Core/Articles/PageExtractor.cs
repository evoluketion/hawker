using Microsoft.Extensions.Logging;
using SmartReader;

namespace Hawker.Core.Articles;

/// <summary>The readable part of an article's web page.</summary>
public sealed record ExtractedPage(string? ContentHtml, string? Byline, string? FeaturedImage, bool IsReadable);

/// <summary>Fetches an article page and pulls out its main content with SmartReader (a port of Mozilla Readability).</summary>
public sealed class PageExtractor(HttpClient http, ILogger<PageExtractor> logger)
{
    private const long MaxPageBytes = 5 * 1024 * 1024;

    public async Task<ExtractedPage?> ExtractAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Article page {Url} returned {Status}", url, (int)response.StatusCode);
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxPageBytes)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            // Redirects (e.g. feed tracking links) mean the final URL is the right base for relative links.
            var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
            return Parse(finalUrl, html);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Could not extract article page {Url}", url);
            return null;
        }
    }

    public static ExtractedPage Parse(string url, string html)
    {
        var article = new Reader(url, html).GetArticle();
        return new ExtractedPage(
            NullIfBlank(article.Content),
            NullIfBlank(article.Byline ?? article.Author),
            NullIfBlank(article.FeaturedImage),
            article.IsReadable);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
