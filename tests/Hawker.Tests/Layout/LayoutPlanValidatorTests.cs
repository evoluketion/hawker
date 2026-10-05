using Hawker.Core.Articles;
using Hawker.Core.Layout;
using static Hawker.Tests.Layout.TestArticles;

namespace Hawker.Tests.Layout;

public class LayoutPlanValidatorTests
{
    private static readonly IReadOnlyList<Article> Articles = [Make("a1"), Make("a2"), Make("a3"), Make("a4", image: false)];

    private static LayoutItem Item(string id, StorySize size = StorySize.Minor, bool image = true, int? keep = null) => new(id, size, image, keep);

    [Fact]
    public void A_valid_plan_passes_unchanged()
    {
        var plan = new LayoutPlan(Item("a1", StorySize.Lead),
            [new LayoutSection("World", [Item("a2", StorySize.Major), Item("a3"), Item("a4", image: false)])]);

        var result = LayoutPlanValidator.Validate(plan, Articles);

        Assert.Empty(result.Corrections);
        Assert.Equal(plan.Lead, result.Plan.Lead);
        Assert.Equal(["a2", "a3", "a4"], result.Plan.Sections.Single().Items.Select(i => i.ArticleId));
    }

    [Fact]
    public void Unknown_and_repeated_articles_are_dropped()
    {
        var plan = new LayoutPlan(Item("a1", StorySize.Lead),
            [new LayoutSection("World", [Item("a2"), Item("zz"), Item("a2"), Item("a1"), Item("a3"), Item("a4")])]);

        var result = LayoutPlanValidator.Validate(plan, Articles);

        Assert.Equal(["a2", "a3", "a4"], result.Plan.Sections.Single().Items.Select(i => i.ArticleId));
        Assert.Contains("Dropped unknown article 'zz'.", result.Corrections);
        Assert.Contains("Dropped repeat of article 'a2'.", result.Corrections);
        Assert.Contains("Dropped repeat of article 'a1'.", result.Corrections);
    }

    [Fact]
    public void Every_article_appears_exactly_once_even_if_the_plan_forgets_some()
    {
        var plan = new LayoutPlan(Item("a1", StorySize.Lead), [new LayoutSection("World", [Item("a2")])]);

        var result = LayoutPlanValidator.Validate(plan, Articles);

        Assert.Equal(["a1", "a2", "a3", "a4"], result.Plan.Items.Select(i => i.ArticleId).Order());
        var fallback = result.Plan.Sections.Single(s => s.Name == LayoutPlanValidator.FallbackSectionName);
        Assert.Equal(["a3", "a4"], fallback.Items.Select(i => i.ArticleId));
    }

    [Fact]
    public void Missing_lead_is_replaced_by_the_first_placed_story()
    {
        var plan = new LayoutPlan(Item("nope", StorySize.Lead), [new LayoutSection("World", [Item("a3"), Item("a2")])]);

        var result = LayoutPlanValidator.Validate(plan, Articles);

        Assert.Equal("a3", result.Plan.Lead.ArticleId);
        Assert.Equal(StorySize.Lead, result.Plan.Lead.Size);
        Assert.DoesNotContain(result.Plan.Sections.SelectMany(s => s.Items), i => i.ArticleId == "a3");
    }

    [Fact]
    public void An_empty_plan_still_produces_a_paper()
    {
        var result = LayoutPlanValidator.Validate(new LayoutPlan(Item("nope"), []), Articles);

        Assert.Equal("a1", result.Plan.Lead.ArticleId);
        Assert.Equal(4, result.Plan.Items.Count());
    }

    [Fact]
    public void Only_one_story_can_be_the_lead()
    {
        var plan = new LayoutPlan(Item("a1", StorySize.Lead), [new LayoutSection("World", [Item("a2", StorySize.Lead)])]);

        var result = LayoutPlanValidator.Validate(plan, Articles);

        Assert.Equal(StorySize.Major, result.Plan.Sections[0].Items[0].Size);
    }

    [Fact]
    public void Cut_points_are_kept_within_the_story_and_briefs_keep_one_block()
    {
        var plan = new LayoutPlan(Item("a1", StorySize.Lead, keep: 50),
            [new LayoutSection("World", [Item("a2", keep: 0), Item("a3", StorySize.Brief), Item("a4", keep: 1)])]);

        var result = LayoutPlanValidator.Validate(plan, Articles);

        Assert.Null(result.Plan.Lead.KeepBlocks); // 50 ≥ 2 blocks: print it all
        var items = result.Plan.Sections[0].Items;
        Assert.Equal(1, items[0].KeepBlocks);     // 0 is raised to 1
        Assert.Equal(1, items[1].KeepBlocks);     // briefs always cut to one block
        Assert.Equal(1, items[2].KeepBlocks);
    }

    [Fact]
    public void Images_are_only_shown_where_they_exist_and_never_on_briefs()
    {
        var plan = new LayoutPlan(Item("a1", StorySize.Lead),
            [new LayoutSection("World", [Item("a2", StorySize.Brief), Item("a3"), Item("a4")])]);

        var result = LayoutPlanValidator.Validate(plan, Articles);

        Assert.True(result.Plan.Lead.ShowImage);
        Assert.Equal([false, true, false], result.Plan.Sections[0].Items.Select(i => i.ShowImage));
    }

    [Fact]
    public void Section_names_are_cleaned_and_same_named_sections_merged()
    {
        var plan = new LayoutPlan(Item("a1", StorySize.Lead),
        [
            new LayoutSection("  World  ", [Item("a2")]),
            new LayoutSection("", [Item("a3")]),
            new LayoutSection("world", [Item("a4")]),
        ]);

        var result = LayoutPlanValidator.Validate(plan, Articles);

        Assert.Equal(["World", LayoutPlanValidator.FallbackSectionName], result.Plan.Sections.Select(s => s.Name));
        Assert.Equal(["a2", "a4"], result.Plan.Sections[0].Items.Select(i => i.ArticleId));
    }
}
