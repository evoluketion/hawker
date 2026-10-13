using System.Net;
using Hawker.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Hawker.Core.Feeds;

public sealed record FeedFetchResult(Feed Feed, IReadOnlyList<FeedItem> Items, string? Error);

/// <param name="retryDelay">The pause before each retry; null for the default of a few seconds.</param>
public sealed class FeedFetcher(HttpClient http, ILogger<FeedFetcher> logger, TimeSpan? retryDelay = null)
{
    /// <summary>
    /// A feed that fails leaves an edition with nothing to print, and free feed services such as hnrss.org are
    /// occasionally slow for a moment, so a fetch that times out or hits a network or server error is tried again.
    /// Fetching is read-only, so repeating it is harmless (unlike printing).
    /// </summary>
    public const int MaxAttempts = 3;

    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);

    public async Task<FeedFetchResult> FetchAsync(Feed feed, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
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
                if (attempt < MaxAttempts && IsTransient(ex))
                {
                    logger.LogWarning(ex, "Fetching feed {Feed} failed (attempt {Attempt} of {Max}); trying again", feed.Name, attempt, MaxAttempts);
                    await Task.Delay(_retryDelay, cancellationToken);
                    continue;
                }

                logger.LogWarning(ex, "Failed to fetch feed {Feed} ({Url})", feed.Name, feed.Url);
                return new FeedFetchResult(feed, [], attempt > 1 ? $"{ex.Message} Tried {attempt} times." : ex.Message);
            }
        }
    }

    public async Task<IReadOnlyList<FeedFetchResult>> FetchAllAsync(IEnumerable<Feed> feeds, CancellationToken cancellationToken = default) =>
        await Task.WhenAll(feeds.Where(f => f.Enabled).Select(f => FetchAsync(f, cancellationToken)));

    /// <summary>Timeouts, network errors and server errors can clear up; a bad address, a 404 or a broken feed won't.</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        // HttpClient.Timeout surfaces as a cancellation the caller didn't ask for (the caller's own is filtered out above).
        OperationCanceledException => true,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || status == HttpStatusCode.RequestTimeout,
        _ => false,
    };
}
