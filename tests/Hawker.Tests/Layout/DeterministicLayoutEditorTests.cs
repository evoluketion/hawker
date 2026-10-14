using Hawker.Core.Articles;
using Hawker.Core.Layout;
using static Hawker.Tests.Layout.TestArticles;

namespace Hawker.Tests.Layout;

public class DeterministicLayoutEditorTests
{
    [Fact]
    public void Top_ranked_story_with_a_picture_and_substance_leads()
    {
        Article[] articles = [Make("a1", image: false), Make("a2", paragraphs: [40]), Make("a3"), Make("a4")];

        var plan = DeterministicLayoutEditor.Plan(articles);

        Assert.Equal("a3", plan.Lead.ArticleId);
        Assert.Equal(StorySize.Lead, plan.Lead.Size);
    }

    [Fact]
    public void Falls_back_to_the_top_ranked_story_when_none_has_a_picture()
    {
        var plan = DeterministicLayoutEditor.Plan([Make("a1", image: false), Make("a2", image: false)]);

        Assert.Equal("a1", plan.Lead.ArticleId);
    }

    [Fact]
    public void Groups_by_source_in_order_of_first_appearance_with_bigger_stories_first()
    {
        Article[] articles =
        [
            Make("a1", "BBC"),
            Make("a2", "HN", image: false),
            Make("a3", "BBC", paragraphs: [30]),
            Make("a4", "HN"),
            Make("a5", "BBC"),
            Make("a6", "BBC"),
        ];

        var plan = DeterministicLayoutEditor.Plan(articles);

        Assert.Equal("a1", plan.Lead.ArticleId);
        Assert.Equal(["HN", "BBC"], plan.Sections.Select(s => s.Name));

        // Two majors per edition (a4, a5: pictured and substantial), then minors, then briefs.
        var hn = plan.Sections[0].Items;
        Assert.Equal([("a4", StorySize.Major), ("a2", StorySize.Minor)], hn.Select(i => (i.ArticleId, i.Size)));
        var bbc = plan.Sections[1].Items;
        Assert.Equal([("a5", StorySize.Major), ("a6", StorySize.Minor), ("a3", StorySize.Brief)], bbc.Select(i => (i.ArticleId, i.Size)));
        Assert.False(bbc[2].ShowImage);
    }

    [Fact]
    public void Headline_only_stories_become_briefs()
    {
        var plan = DeterministicLayoutEditor.Plan([Make("a1"), Make("a2", paragraphs: [])]);

        Assert.Equal(StorySize.Brief, plan.Sections.Single().Items.Single().Size);
    }

    [Theory]
    [InlineData(new[] { 100, 100, 100 }, 280, null)]          // 300 words ≤ 280 × 1.25: print whole
    [InlineData(new[] { 100, 100, 100, 100, 100 }, 280, 3)]   // cut once the budget is reached
    [InlineData(new[] { 600, 50, 50 }, 280, 1)]               // one long block already meets it
    [InlineData(new int[] { }, 280, null)]
    public void Cuts_long_stories_at_a_block_boundary(int[] paragraphs, int budget, int? expected)
    {
        Assert.Equal(expected, DeterministicLayoutEditor.KeepBlocks(Make("a1", paragraphs: paragraphs), budget));
    }

    [Fact]
    public void Figures_do_not_count_toward_the_cut()
    {
        var article = Make("a1", paragraphs: [200, 200, 200]);
        article = article with
        {
            Blocks = [article.Blocks[0], new ArticleBlock(BlockKind.Figure, "", 0, "a1-1"), article.Blocks[1], article.Blocks[2]],
        };

        Assert.Equal(2, DeterministicLayoutEditor.KeepBlocks(article, 350));
    }

    [Fact]
    public void Its_plans_need_no_correction()
    {
        Article[] articles = [Make("a1", "BBC"), Make("a2", "HN", image: false), Make("a3", "BBC", paragraphs: [30]), Make("a4", "HN", paragraphs: [])];

        var result = LayoutPlanValidator.Validate(DeterministicLayoutEditor.Plan(articles), articles);

        Assert.Empty(result.Corrections);
    }

    [Fact]
    public void A_page_that_is_not_an_article_never_leads_and_prints_as_a_brief()
    {
        Article[] articles = [Make("a1", page: PageKind.NotArticle), Make("a2", image: false), Make("a3")];

        var plan = DeterministicLayoutEditor.Plan(articles);

        Assert.Equal("a3", plan.Lead.ArticleId);
        Assert.Equal(StorySize.Brief, plan.Sections.SelectMany(s => s.Items).Single(i => i.ArticleId == "a1").Size);
    }

    [Fact]
    public void Real_reporting_leads_ahead_of_a_company_announcement()
    {
        Article[] articles = [Make("a1", page: PageKind.Announcement), Make("a2")];

        Assert.Equal("a2", DeterministicLayoutEditor.Plan(articles).Lead.ArticleId);
        Assert.Equal("a1", DeterministicLayoutEditor.Plan([articles[0], Make("a3", image: false)]).Lead.ArticleId);
    }

    [Fact]
    public void A_non_article_leads_only_when_there_is_nothing_else()
    {
        Assert.Equal("a2", DeterministicLayoutEditor.Plan([Make("a1", page: PageKind.NotArticle), Make("a2", image: false)]).Lead.ArticleId);
        Assert.Equal("a1", DeterministicLayoutEditor.Plan([Make("a1", page: PageKind.NotArticle), Make("a2", page: PageKind.NotArticle)]).Lead.ArticleId);
    }
}
