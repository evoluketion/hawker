using System.Net;
using Hawker.Core.Domain;
using Hawker.Core.Feeds;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hawker.Tests.Feeds;

public class FeedFetcherTests
{
    private static readonly Feed Hn = new() { Name = "Hacker News", Url = "https://hnrss.org/best" };

    private static readonly string FeedXml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "hnrss-frontpage.xml"));

    private static FeedFetcher Fetcher(SequenceHandler handler) =>
        new(new HttpClient(handler), NullLogger<FeedFetcher>.Instance, TimeSpan.Zero);

    [Fact]
    public async Task A_timeout_is_retried_and_the_feed_still_arrives()
    {
        // HttpClient.Timeout surfaces as a TaskCanceledException the caller didn't ask for.
        var handler = new SequenceHandler(
            () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing."),
            () => throw new HttpRequestException("Connection reset"),
            Ok);

        var result = await Fetcher(handler).FetchAsync(Hn);

        Assert.Null(result.Error);
        Assert.NotEmpty(result.Items);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Server_errors_are_retried_up_to_the_limit_and_the_attempts_are_reported()
    {
        var handler = new SequenceHandler(() => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await Fetcher(handler).FetchAsync(Hn);

        Assert.Empty(result.Items);
        Assert.Contains("Tried 3 times.", result.Error);
        Assert.Equal(FeedFetcher.MaxAttempts, handler.Calls);
    }

    [Fact]
    public async Task A_missing_feed_is_not_retried()
    {
        var handler = new SequenceHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await Fetcher(handler).FetchAsync(Hn);

        Assert.NotNull(result.Error);
        Assert.DoesNotContain("Tried", result.Error);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Cancelling_the_run_stops_without_retrying()
    {
        using var cts = new CancellationTokenSource();
        var handler = new SequenceHandler(() =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetcher(handler).FetchAsync(Hn, cts.Token));
        Assert.Equal(1, handler.Calls);
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent(FeedXml) };

    /// <summary>Answers each request with the next response in turn, repeating the last one.</summary>
    private sealed class SequenceHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responses[Math.Min(Calls, responses.Length - 1)];
            Calls++;
            return Task.FromResult(response());
        }
    }
}
