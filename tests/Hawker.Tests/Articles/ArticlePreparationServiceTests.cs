using System.Net;
using System.Text;
using Hawker.Core.Articles;
using Hawker.Core.Domain;
using Hawker.Core.Feeds;
using Hawker.Core.Selection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Hawker.Tests.Articles;

public class ArticlePreparationServiceTests
{
    private static readonly string ArticlePage = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "article-page.html"));

    private readonly StubHandler _http = new();

    private ArticlePreparationService CreateService() => new(
        new PageExtractor(new HttpClient(_http), NullLogger<PageExtractor>.Instance),
        new ImageProcessor(new HttpClient(_http), NullLogger<ImageProcessor>.Instance));

    private static SelectedArticle Selected(string url, string title = "Story", string? summary = null, string? imageUrl = null, int feedId = 1) =>
        new(new FeedItem { FeedId = feedId, FeedName = "Feed", Title = title, Url = url, SummaryHtml = summary, ImageUrl = imageUrl },
            UrlNormalizer.Hash(url), DateTimeOffset.UnixEpoch);

    private static Dictionary<int, Feed> Feeds(bool fetchFullText = true) =>
        new() { [1] = new Feed { Id = 1, Name = "Feed", Url = "https://feed.example.com/rss", FetchFullText = fetchFullText } };

    [Fact]
    public async Task Extracts_full_text_and_embeds_images()
    {
        _http.Html("https://news.example.com/bridge", ArticlePage);
        _http.Image("https://news.example.com/img/bridge-lead.jpg", 1600, 900);
        _http.Image("https://news.example.com/img/site-plan.jpg", 800, 600);

        var result = await CreateService().PrepareAsync(
            [Selected("https://news.example.com/bridge", "Council approves new river bridge", summary: "Short teaser.")],
            Feeds(), new ImageOptions());

        var article = Assert.Single(result.Articles);
        Assert.Equal("a1", article.Id);
        Assert.Equal(BodySource.ExtractedPage, article.BodySource);
        Assert.Contains(article.Blocks, b => b.Html.Contains("Work is expected to begin in the spring"));
        Assert.DoesNotContain(article.Blocks, b => b.Html.Contains("Subscribe to our newsletter"));
        Assert.Equal("a1-lead", article.LeadImage!.Id);
        Assert.NotNull(article.LeadImage.DataUri);
        Assert.All(article.BodyImages, i => Assert.NotNull(i.DataUri));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task Failed_page_falls_back_to_feed_text_with_a_note()
    {
        _http.Status("https://news.example.com/gone", HttpStatusCode.Forbidden);

        var result = await CreateService().PrepareAsync(
            [Selected("https://news.example.com/gone", summary: "<p>The feed's own summary.</p>")], Feeds(), new ImageOptions());

        var article = Assert.Single(result.Articles);
        Assert.Equal(BodySource.FeedSummary, article.BodySource);
        Assert.Contains(result.Notes, n => n.Contains("could not be read"));
    }

    [Fact]
    public async Task Discussion_pages_and_feeds_without_full_text_are_not_fetched()
    {
        var result = await CreateService().PrepareAsync(
            [
                Selected("https://news.ycombinator.com/item?id=1", "Ask HN: Thoughts?", summary: "<p>Self post text.</p>"),
                Selected("https://news.example.com/other", summary: "<p>Summary only.</p>"),
            ],
            Feeds(fetchFullText: false), new ImageOptions());

        Assert.Empty(_http.Requested);
        Assert.All(result.Articles, a => Assert.Equal(BodySource.FeedSummary, a.BodySource));
        Assert.Equal(["a1", "a2"], result.Articles.Select(a => a.Id));
    }

    [Fact]
    public async Task Broken_lead_image_is_replaced_by_the_first_good_body_image()
    {
        var summary = """
            <p>Text.</p>
            <figure><img src="https://cdn.example.com/icon.png"></figure>
            <figure><img src="https://cdn.example.com/good.jpg"><figcaption>Good photo.</figcaption></figure>
            <figure><img src="https://cdn.example.com/missing.jpg"></figure>
            """;
        _http.Status("https://cdn.example.com/broken-lead.jpg", HttpStatusCode.NotFound);
        _http.Image("https://cdn.example.com/icon.png", 64, 64);
        _http.Image("https://cdn.example.com/good.jpg", 800, 600);
        _http.Status("https://cdn.example.com/missing.jpg", HttpStatusCode.NotFound);

        var result = await CreateService().PrepareAsync(
            [Selected("https://news.example.com/x", summary: summary, imageUrl: "https://cdn.example.com/broken-lead.jpg")],
            Feeds(fetchFullText: false), new ImageOptions());

        var article = Assert.Single(result.Articles);
        Assert.Equal("https://cdn.example.com/good.jpg", article.LeadImage!.SourceUrl);
        Assert.Equal("Good photo.", article.LeadImage.Caption);
        Assert.Equal("a1-lead", article.LeadImage.Id);
        Assert.Empty(article.BodyImages);
        Assert.Equal([BlockKind.Paragraph], article.Blocks.Select(b => b.Kind));
    }

    [Fact]
    public async Task Larger_page_image_beats_a_small_feed_thumbnail()
    {
        var page = ArticlePage.Replace("https://news.example.com/img/bridge-lead.jpg", "https://news.example.com/img/og-large.jpg");
        _http.Html("https://news.example.com/bridge", page);
        _http.Image("https://feed.example.com/thumb-240.jpg", 240, 135);
        _http.Image("https://news.example.com/img/og-large.jpg", 1200, 675);

        var result = await CreateService().PrepareAsync(
            [Selected("https://news.example.com/bridge", imageUrl: "https://feed.example.com/thumb-240.jpg")], Feeds(), new ImageOptions());

        var lead = Assert.Single(result.Articles).LeadImage!;
        Assert.Equal("https://news.example.com/img/og-large.jpg", lead.SourceUrl);
        Assert.Equal(1200, lead.Width);
        Assert.Empty(result.Articles[0].LeadImageCandidates);
    }

    [Fact]
    public async Task Unbranded_body_picture_beats_the_featured_image_and_leaves_the_body()
    {
        // The fixture page has a body figure (site-plan.jpg) and an og:image (bridge-lead.jpg, often logo-branded).
        _http.Html("https://news.example.com/bridge", ArticlePage);
        _http.Image("https://news.example.com/img/site-plan.jpg", 800, 600);
        _http.Image("https://news.example.com/img/bridge-lead.jpg", 1600, 900);

        var result = await CreateService().PrepareAsync([Selected("https://news.example.com/bridge")], Feeds(), new ImageOptions());

        var article = Assert.Single(result.Articles);
        Assert.Equal("https://news.example.com/img/site-plan.jpg", article.LeadImage!.SourceUrl);
        Assert.Equal("a1-lead", article.LeadImage.Id);
        Assert.Equal("An artist’s impression of the finished bridge. Image: City Council", article.LeadImage.Caption);
        Assert.Empty(article.BodyImages);
        Assert.DoesNotContain(article.Blocks, b => b.Kind == BlockKind.Figure);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = [];

        public List<string> Requested { get; } = [];

        public void Html(string url, string html) => _responses[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };

        public void Image(string url, int width, int height) => _responses[url] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(ImageProcessorTests.MakeImage(width, height, SKColors.CadetBlue, SKEncodedImageFormat.Jpeg)),
        };

        public void Status(string url, HttpStatusCode status) => _responses[url] = () => new HttpResponseMessage(status);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requested)
            {
                Requested.Add(url);
            }

            var response = _responses.TryGetValue(url, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
