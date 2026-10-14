using Hawker.Core.Articles;
using Hawker.Core.Domain;
using Hawker.Core.Layout;
using Hawker.Web.Rendering;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Hawker.Tests.Layout.TestArticles;

namespace Hawker.Tests.Rendering;

public class NewspaperRenderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);

    private static NewspaperModel Model(LayoutPlan plan, IReadOnlyList<Article> articles, PaperSize paper = PaperSize.A4, string name = "The Morning Hawker",
        bool qrCodesOnWholeStories = true) =>
        NewspaperModelBuilder.Build(name, 12, paper, Now, TimeZoneInfo.Utc, plan, articles, qrCodesOnWholeStories);

    private static async Task<string> RenderAsync(NewspaperModel model)
    {
        // A real web host container with the app's component registrations, so renderer scoping problems show up here.
        var builder = WebApplication.CreateBuilder();
        builder.Host.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        builder.Services.AddRazorComponents();
        var services = builder.Build().Services;
        return await new NewspaperRenderer(services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<ILoggerFactory>()).RenderHtmlAsync(model);
    }

    [Fact]
    public void Truncated_stories_end_at_the_cut_with_a_qr_code()
    {
        var article = Make("a1", paragraphs: [100, 100, 100, 100]);
        var plan = new LayoutPlan(new LayoutItem("a1", StorySize.Lead, true, KeepBlocks: 2), []);

        var story = Model(plan, [article]).Lead;

        Assert.True(story.Truncated);
        Assert.Equal(2, story.Blocks.Count);
        Assert.Contains("<svg", story.QrSvg);
        Assert.Equal("news.example.com/a1", story.ShortUrl);
    }

    [Fact]
    public void Every_story_has_a_qr_code_whether_whole_cut_brief_or_headline_only()
    {
        Article[] articles = [Make("a1"), Make("a2"), Make("a3", paragraphs: []), Make("a4", paragraphs: [100, 100, 100])];
        var plan = new LayoutPlan(new LayoutItem("a1", StorySize.Lead, true, null),
            [new LayoutSection("News", [
                new LayoutItem("a2", StorySize.Brief, false, 1),
                new LayoutItem("a3", StorySize.Minor, false, null),
                new LayoutItem("a4", StorySize.Major, false, 1)])]);

        var model = Model(plan, articles);
        var section = model.Sections[0];

        Assert.False(model.Lead.Truncated);
        Assert.Contains("<svg", model.Lead.QrSvg);
        Assert.Contains("<svg", section.Briefs.Single().QrSvg);
        Assert.Contains("<svg", section.Minors.Single().QrSvg);
        Assert.True(section.Majors.Single().Truncated);
        Assert.Contains("<svg", section.Majors.Single().QrSvg);
    }

    [Fact]
    public void With_the_setting_off_only_whole_stories_lose_their_qr_code()
    {
        Article[] articles = [Make("a1"), Make("a2"), Make("a3", paragraphs: []), Make("a4", paragraphs: [100, 100, 100])];
        var plan = new LayoutPlan(new LayoutItem("a1", StorySize.Lead, true, null),
            [new LayoutSection("News", [
                new LayoutItem("a2", StorySize.Brief, false, 1),
                new LayoutItem("a3", StorySize.Minor, false, null),
                new LayoutItem("a4", StorySize.Major, false, 1)])]);

        var model = Model(plan, articles, qrCodesOnWholeStories: false);
        var section = model.Sections[0];

        Assert.Null(model.Lead.QrSvg);
        Assert.NotNull(section.Briefs.Single().QrSvg);
        Assert.NotNull(section.Minors.Single().QrSvg);
        Assert.NotNull(section.Majors.Single().QrSvg);
    }

    [Fact]
    public void Qr_code_links_to_the_article_rather_than_its_comments()
    {
        var article = Make("a1") with { CommentsUrl = "https://news.ycombinator.com/item?id=1" };

        var story = Model(new LayoutPlan(new LayoutItem("a1", StorySize.Lead, true, null), []), [article]).Lead;

        Assert.Equal(NewspaperModelBuilder.QrSvg(article.Url), story.QrSvg);
        Assert.NotEqual(NewspaperModelBuilder.QrSvg(article.CommentsUrl!), story.QrSvg);
    }

    [Fact]
    public void Qr_code_link_has_tracking_parameters_removed()
    {
        var article = Make("a1") with { Url = "https://news.example.com/a1?utm_source=rss" };

        var story = Model(new LayoutPlan(new LayoutItem("a1", StorySize.Lead, true, null), []), [article]).Lead;

        Assert.Equal(NewspaperModelBuilder.QrSvg("https://news.example.com/a1"), story.QrSvg);
    }

    [Fact]
    public void Body_figures_print_only_in_wide_stories_and_never_dangle_after_a_cut()
    {
        var baseArticle = Make("a1", paragraphs: [100, 100, 100]);
        var figureImage = new ArticleImage { Id = "a1-1", SourceUrl = "https://cdn.example.com/f.jpg", DataUri = TinyJpeg, Width = 800, Height = 600 };
        var article = baseArticle with
        {
            Blocks = [baseArticle.Blocks[0], new ArticleBlock(BlockKind.Figure, "", 0, "a1-1"), baseArticle.Blocks[1], baseArticle.Blocks[2]],
            BodyImages = [figureImage],
        };

        StoryView Story(StorySize size, int? keep) => Model(new LayoutPlan(new LayoutItem("x", StorySize.Lead, true, null),
            [new LayoutSection("S", [new LayoutItem("a1", size, true, keep)])]), [article, Make("x")]).Sections[0] switch
        {
            var s => s.Majors.Concat(s.Minors).Single(),
        };

        Assert.Contains(Story(StorySize.Major, null).Blocks, b => b.Kind == BlockKind.Figure);
        Assert.DoesNotContain(Story(StorySize.Minor, null).Blocks, b => b.Kind == BlockKind.Figure);
        Assert.DoesNotContain(Story(StorySize.Major, 1).Blocks, b => b.Kind == BlockKind.Figure);
    }

    [Fact]
    public void Meta_line_lists_source_score_comments_author_and_time()
    {
        var article = Make("a1", "Hacker News", score: 1412) with { CommentCount = 1, Author = "pg", Published = Now.AddHours(-2) };

        Assert.Equal("Hacker News · 1,412 points · 1 comment · By pg · 5 Oct, 04:00", NewspaperModelBuilder.MetaLine(article, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task Html_contains_masthead_sections_and_escaped_source_text()
    {
        Article[] articles =
        [
            Make("a1", "BBC News", title: "Tom & Jerry <script>alert(1)</script> return"),
            Make("a2", "Hacker News"),
            Make("a3", "Hacker News", paragraphs: [30]),
        ];
        var plan = LayoutPlanValidator.Validate(DeterministicLayoutEditor.Plan(articles), articles).Plan;

        var html = await RenderAsync(Model(plan, articles, PaperSize.Letter));

        Assert.Contains("The Morning Hawker", html);
        Assert.Contains("Issue No. 12", html);
        Assert.Contains("Monday, 5 October 2026", html);
        Assert.Contains("size: Letter", html);
        Assert.Contains("Tom &amp; Jerry &lt;script&gt;alert(1)&lt;/script&gt; return", html);
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.Contains("class=\"section-head\">Hacker News</h2>", html);
        Assert.Contains("In brief", html);
        Assert.Contains("Read online", html);
        Assert.Contains("a2 paragraph 1:", html);
        Assert.Contains("@font-face", html);
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task Renders_a_pdf_with_headless_chromium()
    {
        Article[] articles = [Make("a1"), Make("a2", "Other"), Make("a3", "Other", paragraphs: [40])];
        var plan = LayoutPlanValidator.Validate(DeterministicLayoutEditor.Plan(articles), articles).Plan;
        var html = await RenderAsync(Model(plan, articles));

        var pdf = await new PdfRenderer(Options.Create(new PdfOptions()), NullLogger<PdfRenderer>.Instance)
            .RenderAsync(html, "The Morning Hawker", PaperSize.A4);

        Assert.True(pdf.Length > 10_000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Theory]
    [Trait("Category", "Browser")]
    [InlineData("Hawker Tuah", PaperSize.A4, 46, false)]
    [InlineData("The Morning Hawker", PaperSize.A4, 40, false)]
    [InlineData("The Greater Metropolitan Evening Chronicle", PaperSize.A4, 20, false)]
    [InlineData("The Greater Metropolitan Evening Chronicle", PaperSize.Letter, 20, false)]
    [InlineData("The Quite Extraordinarily Long-Winded Weekly Digest of Everything Worth Reading", PaperSize.A4, 20, true)]
    public async Task The_masthead_shrinks_to_fit_long_names(string name, PaperSize paper, double minimumPt, bool wraps)
    {
        Article[] articles = [Make("a1")];
        var plan = LayoutPlanValidator.Validate(DeterministicLayoutEditor.Plan(articles), articles).Plan;
        var html = await RenderAsync(Model(plan, articles, paper, name));

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync(html);
        await PdfRenderer.FitMastheadAsync(page, paper);

        // Lay the page out at the printed width, then check the title stays between the ears.
        await page.SetViewportSizeAsync((int)Math.Floor(PdfRenderer.ContentWidthMm(paper) * 96 / 25.4), 1000);
        var fit = await page.EvaluateAsync<MastheadFit>("""
            () => {
              const title = document.querySelector('.masthead-title');
              const [left, right] = [...document.querySelectorAll('.masthead-ears .ear')].map(e => e.getBoundingClientRect());
              const text = document.createRange();
              text.selectNodeContents(title);
              const box = text.getBoundingClientRect();
              return {
                fontPt: Math.round(parseFloat(getComputedStyle(title).fontSize) * 72 / 96 * 10) / 10,
                lines: Math.round(box.height / parseFloat(getComputedStyle(title).lineHeight)),
                clearOfEars: box.left >= left.right && box.right <= right.left,
              };
            }
            """);

        Assert.True(fit.FontPt >= minimumPt && fit.FontPt <= 46, $"Masthead set at {fit.FontPt}pt");
        Assert.True(fit.ClearOfEars, "The masthead overlaps the ears");
        Assert.Equal(wraps, fit.Lines > 1);
        if (minimumPt == 46)
        {
            Assert.Equal(46, fit.FontPt, 1);
        }
        else
        {
            Assert.True(fit.FontPt < 46);
        }
    }

    [Fact]
    [Trait("Category", "Browser")]
    public async Task Side_by_side_stories_fit_on_one_page_and_move_over_whole()
    {
        Article[] articles =
        [
            Prose("lead", 300, image: true),
            Prose("tall-pictured", LayoutPlanValidator.MinorMaxWordsWithImage, image: true),
            Prose("tall-plain", LayoutPlanValidator.MinorMaxWords, image: false),
            Prose("very-long", 2000, image: false),
        ];
        var proposed = new LayoutPlan(new LayoutItem("lead", StorySize.Lead, true, null),
            [new LayoutSection("World", articles.Skip(1).Select(a => new LayoutItem(a.Id, StorySize.Minor, true, null)).ToList())]);
        var html = await RenderAsync(Model(LayoutPlanValidator.Validate(proposed, articles).Plan, articles));

        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetViewportSizeAsync((int)Math.Floor(PdfRenderer.ContentWidthMm(PaperSize.A4) * 96 / 25.4), 1000);
        await page.EmulateMediaAsync(new() { Media = Microsoft.Playwright.Media.Print });
        await page.SetContentAsync(html);
        var stories = await page.EvaluateAsync<RenderedStory[]>("""
            () => [...document.querySelectorAll('.story--minor, .story--major')].map(s => ({
              id: s.querySelector('.headline').textContent.trim(),
              size: s.classList.contains('story--major') ? 'major' : 'minor',
              breakInside: getComputedStyle(s).breakInside,
              heightMm: s.getBoundingClientRect().height * 25.4 / 96,
            }))
            """);

        Assert.Equal("major", stories.Single(s => s.Id == "Headline very-long").Size);
        var pair = stories.Where(s => s.Size == "minor").ToList();
        Assert.Equal(["Headline tall-pictured", "Headline tall-plain"], pair.Select(s => s.Id));
        const double pageContentMm = 297 - 10 - 13;
        Assert.All(pair, s =>
        {
            Assert.Equal("avoid", s.BreakInside);
            Assert.True(s.HeightMm <= pageContentMm, $"{s.Id} is {s.HeightMm:F0}mm tall");
        });
    }

    /// <summary>An article of ordinary-looking prose, since word lengths decide how tall a column runs.</summary>
    private static Article Prose(string id, int words, bool image)
    {
        string[] vocabulary =
        [
            "the", "government", "said", "on", "Tuesday", "that", "it", "would", "review", "plans", "for", "a", "new",
            "railway", "line", "between", "northern", "cities", "after", "months", "of", "delays", "and", "rising",
            "costs", "officials", "expect", "construction", "to", "begin", "next", "year", "although", "critics",
            "remain", "unconvinced", "by", "the", "projected", "benefits", "in", "local", "communities",
        ];
        var random = new Random(words);
        var paragraphs = new List<ArticleBlock>();
        for (var left = words; left > 0; left -= 60)
        {
            var count = Math.Min(60, left);
            var text = string.Join(' ', Enumerable.Range(0, count).Select(_ => vocabulary[random.Next(vocabulary.Length)]));
            paragraphs.Add(new ArticleBlock(BlockKind.Paragraph, $"<p>{text}.</p>", count));
        }

        // 1200×800, the 3:2 shape of a typical news photo.
        var photo = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1200' height='800'><rect width='1200' height='800' fill='#888'/></svg>"));
        return Make(id, image: image, title: $"Headline {id}") with
        {
            Blocks = paragraphs,
            LeadImage = image ? new ArticleImage { Id = $"{id}-lead", SourceUrl = $"https://cdn.example.com/{id}.jpg", DataUri = photo, Width = 1200, Height = 800 } : null,
        };
    }

    private sealed class RenderedStory
    {
        public string Id { get; set; } = "";
        public string Size { get; set; } = "";
        public string BreakInside { get; set; } = "";
        public double HeightMm { get; set; }
    }

    private sealed class MastheadFit
    {
        public double FontPt { get; set; }
        public int Lines { get; set; }
        public bool ClearOfEars { get; set; }
    }
}
