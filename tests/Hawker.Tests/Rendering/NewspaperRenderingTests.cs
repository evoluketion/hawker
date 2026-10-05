using Hawker.Core.Articles;
using Hawker.Core.Domain;
using Hawker.Core.Layout;
using Hawker.Web.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Hawker.Tests.Layout.TestArticles;

namespace Hawker.Tests.Rendering;

public class NewspaperRenderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);

    private static NewspaperModel Model(LayoutPlan plan, IReadOnlyList<Article> articles, PaperSize paper = PaperSize.A4) =>
        NewspaperModelBuilder.Build("The Morning Hawker", 12, paper, Now, TimeZoneInfo.Utc, plan, articles);

    private static async Task<string> RenderAsync(NewspaperModel model)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        return await new NewspaperRenderer(services, services.GetRequiredService<ILoggerFactory>()).RenderHtmlAsync(model);
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
    public void Whole_stories_have_no_qr_code_but_briefs_and_headline_only_stories_do()
    {
        Article[] articles = [Make("a1"), Make("a2"), Make("a3", paragraphs: [])];
        var plan = new LayoutPlan(new LayoutItem("a1", StorySize.Lead, true, null),
            [new LayoutSection("News", [new LayoutItem("a2", StorySize.Brief, false, 1), new LayoutItem("a3", StorySize.Minor, false, null)])]);

        var model = Model(plan, articles);

        Assert.Null(model.Lead.QrSvg);
        Assert.NotNull(model.Sections[0].Briefs.Single().QrSvg);
        Assert.NotNull(model.Sections[0].Minors.Single().QrSvg);
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
}
