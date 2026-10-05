using Hawker.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Hawker.Core.Feeds;

public sealed record FeedFetchResult(Feed Feed, IReadOnlyList<FeedItem> Items, string? Error);

public sealed class FeedFetcher(HttpClient http, ILogger<FeedFetcher> logger)
{
    public async Task<FeedFetchResult> FetchAsync(Feed feed, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = new Uri(feed.Url);
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

            var extractor = PopularityExtractor.For(feed);
            var items = FeedParser.Parse(stream, uri, feed.Id, feed.Name).Select(extractor.Apply).ToList();
            logger.LogInformation("Fetched {Count} items from {Feed}", items.Count, feed.Name);
            return new FeedFetchResult(feed, items, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Failed to fetch feed {Feed} ({Url})", feed.Name, feed.Url);
            return new FeedFetchResult(feed, [], ex.Message);
        }
    }

    public async Task<IReadOnlyList<FeedFetchResult>> FetchAllAsync(IEnumerable<Feed> feeds, CancellationToken cancellationToken = default) =>
        await Task.WhenAll(feeds.Where(f => f.Enabled).Select(f => FetchAsync(f, cancellationToken)));
}
