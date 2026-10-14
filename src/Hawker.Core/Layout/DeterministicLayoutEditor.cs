using Hawker.Core.Articles;

namespace Hawker.Core.Layout;

/// <summary>
/// Lays out an edition with fixed rules and no AI. Used when no AI is configured and as the fallback when an AI plan
/// cannot be obtained.
/// </summary>
public sealed class DeterministicLayoutEditor : ILayoutEditor
{
    /// <summary>Roughly how many words each size gets before the story is cut to "Continued online".</summary>
    public static int WordBudget(StorySize size) => size switch
    {
        StorySize.Lead => 550,
        StorySize.Major => 380,
        StorySize.Minor => 220,
        _ => 70,
    };

    private const int MajorStoriesPerEdition = 2;
    private const int BriefWordThreshold = 60;
    private const int SubstantialWordCount = 150;

    public string Name => "standard layout";

    public Task<LayoutPlan> PlanAsync(LayoutRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(Plan(request.Articles));

    public static LayoutPlan Plan(IReadOnlyList<Article> articles)
    {
        if (articles.Count == 0)
        {
            throw new ArgumentException("An edition needs at least one article.", nameof(articles));
        }

        var leadArticle = ChooseLead(articles);

        var majors = articles
            .Where(a => a != leadArticle && a.Page.Kind != PageKind.NotArticle && a.LeadImage?.DataUri is not null && a.WordCount >= SubstantialWordCount)
            .Take(MajorStoriesPerEdition)
            .ToHashSet();

        var sections = articles
            .Where(a => a != leadArticle)
            .GroupBy(a => a.Source)
            .Select(g => new LayoutSection(g.Key, g
                .Select(a => SizeFor(a, majors.Contains(a)))
                .OrderBy(i => i.Size)
                .ToList()))
            .ToList();

        return new LayoutPlan(Item(leadArticle, StorySize.Lead), sections);
    }

    /// <summary>
    /// The highest-ranked story with a picture and some substance leads, preferring real articles over a company's
    /// announcement about itself. Product pages and the like never lead while there is anything else.
    /// </summary>
    public static Article ChooseLead(IReadOnlyList<Article> articles)
    {
        bool Substantial(Article a) => a.LeadImage?.DataUri is not null && a.WordCount >= SubstantialWordCount;
        return articles.FirstOrDefault(a => a.Page.Kind == PageKind.Article && Substantial(a))
            ?? articles.FirstOrDefault(a => a.Page.Kind == PageKind.Announcement && Substantial(a))
            ?? articles.FirstOrDefault(a => a.Page.Kind != PageKind.NotArticle)
            ?? articles[0];
    }

    private static LayoutItem SizeFor(Article article, bool major)
    {
        if (major)
        {
            return Item(article, StorySize.Major);
        }

        return article.WordCount < BriefWordThreshold || article.Page.Kind == PageKind.NotArticle
            ? Item(article, StorySize.Brief)
            : Item(article, StorySize.Minor);
    }

    private static LayoutItem Item(Article article, StorySize size) =>
        new(article.Id, size, ShowImage: size != StorySize.Brief, KeepBlocks(article, WordBudget(size)));

    /// <summary>
    /// How many text blocks fit the word budget, cutting at a block boundary. A story that only slightly overruns
    /// is printed whole rather than cut for the sake of a paragraph or two.
    /// </summary>
    public static int? KeepBlocks(Article article, int wordBudget)
    {
        var textBlocks = article.Blocks.Where(b => b.Kind != BlockKind.Figure).ToList();
        if (textBlocks.Sum(b => b.WordCount) <= wordBudget * 1.25)
        {
            return null;
        }

        var words = 0;
        for (var i = 0; i < textBlocks.Count; i++)
        {
            words += textBlocks[i].WordCount;
            if (words >= wordBudget)
            {
                return i + 1;
            }
        }

        return null;
    }
}
