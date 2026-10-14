using Hawker.Core.Articles;

namespace Hawker.Core.Layout;

public sealed record ValidatedPlan(LayoutPlan Plan, IReadOnlyList<string> Corrections);

/// <summary>
/// Makes any proposed plan safe to render: every article appears exactly once, unknown IDs are dropped, there is one
/// lead, cut points are within the story, images are only requested where they exist, and half-width stories are short
/// enough to fit on one page. Pages that are not articles never lead and print as briefs.
/// </summary>
public static class LayoutPlanValidator
{
    public const string FallbackSectionName = "More news";
    private const int MaxSectionNameLength = 40;

    /// <summary>
    /// Most words a half-width story may print. An A4 half-width column holds about 470 words a page, or about 390 under a
    /// lead image; these leave about 10mm spare for font differences between machines.
    /// </summary>
    public const int MinorMaxWords = 450;

    /// <inheritdoc cref="MinorMaxWords"/>
    public const int MinorMaxWordsWithImage = 370;

    public static ValidatedPlan Validate(LayoutPlan proposed, IReadOnlyList<Article> articles)
    {
        if (articles.Count == 0)
        {
            throw new ArgumentException("An edition needs at least one article.", nameof(articles));
        }

        var corrections = new List<string>();
        var byId = articles.ToDictionary(a => a.Id);
        var placed = new HashSet<string>();

        LayoutItem? lead = null;
        if (byId.ContainsKey(proposed.Lead.ArticleId))
        {
            lead = proposed.Lead;
            placed.Add(lead.ArticleId);
        }
        else
        {
            corrections.Add($"Lead story '{proposed.Lead.ArticleId}' is not in this edition.");
        }

        var sections = new List<(string Name, List<LayoutItem> Items)>();
        foreach (var section in proposed.Sections)
        {
            var items = new List<LayoutItem>();
            foreach (var item in section.Items)
            {
                if (!byId.ContainsKey(item.ArticleId))
                {
                    corrections.Add($"Dropped unknown article '{item.ArticleId}'.");
                }
                else if (!placed.Add(item.ArticleId))
                {
                    corrections.Add($"Dropped repeat of article '{item.ArticleId}'.");
                }
                else
                {
                    items.Add(item);
                }
            }

            if (items.Count > 0)
            {
                AddToSection(sections, CleanName(section.Name), items);
            }
        }

        if (lead is null)
        {
            // Promote the first placed story, or failing that the top-ranked article.
            var promoted = sections.SelectMany(s => s.Items).FirstOrDefault();
            if (promoted is not null)
            {
                sections.ForEach(s => s.Items.Remove(promoted));
                lead = promoted;
            }
            else
            {
                lead = new LayoutItem(articles[0].Id, StorySize.Lead, true, null);
                placed.Add(lead.ArticleId);
            }
        }

        lead = KeepNonArticlesOffTheLead(lead, articles, sections, placed, corrections);

        var missing = articles.Where(a => !placed.Contains(a.Id)).ToList();
        if (missing.Count > 0)
        {
            corrections.Add($"Added {missing.Count} article(s) the plan left out.");
            AddToSection(sections, FallbackSectionName, missing
                .Select(a => new LayoutItem(a.Id, a.BodySource == BodySource.None ? StorySize.Brief : StorySize.Minor, true, null))
                .ToList());
        }

        var finalLead = Fix(lead with { Size = StorySize.Lead }, byId[lead.ArticleId], corrections);
        var finalSections = sections
            .Where(s => s.Items.Count > 0)
            .Select(s => new LayoutSection(s.Name, s.Items
                .Select(i => Fix(i.Size == StorySize.Lead ? i with { Size = StorySize.Major } : i, byId[i.ArticleId], corrections))
                .ToList()))
            .ToList();

        return new ValidatedPlan(new LayoutPlan(finalLead, finalSections), corrections);
    }

    /// <summary>
    /// A half-width story taller than a page is set full width, so it can cross the page on its own instead of splitting
    /// the pair beside it.
    /// </summary>
    private static LayoutItem Fix(LayoutItem item, Article article, List<string> corrections)
    {
        if (article.Page.Kind == PageKind.NotArticle && item.Size is StorySize.Major or StorySize.Minor)
        {
            item = item with { Size = StorySize.Brief };
            corrections.Add($"Set '{item.ArticleId}' as a brief; it is not an article.");
        }

        var textBlocks = article.Blocks.Count(b => b.Kind != BlockKind.Figure);
        int? keep = item.KeepBlocks;

        if (item.Size == StorySize.Brief)
        {
            keep = Math.Min(keep ?? 1, 1);
        }

        if (keep is { } k)
        {
            keep = k >= textBlocks ? null : Math.Max(1, k);
        }

        var showImage = item.ShowImage && item.Size != StorySize.Brief && article.LeadImage?.DataUri is not null;
        keep = textBlocks == 0 ? null : keep;
        var size = item.Size;

        if (size == StorySize.Minor)
        {
            var words = article.Blocks.Where(b => b.Kind != BlockKind.Figure).Take(keep ?? textBlocks).Sum(b => b.WordCount);
            if (words > (showImage ? MinorMaxWordsWithImage : MinorMaxWords))
            {
                size = StorySize.Major;
                corrections.Add($"Widened '{item.ArticleId}' to full width; too long for a half-width column.");
            }
        }

        return item with { Size = size, ShowImage = showImage, KeepBlocks = keep };
    }

    /// <summary>
    /// A product page, tool or the like never leads while a real story is available: the two swap places, and the page
    /// takes the story's spot as a brief.
    /// </summary>
    private static LayoutItem KeepNonArticlesOffTheLead(
        LayoutItem lead, IReadOnlyList<Article> articles, List<(string Name, List<LayoutItem> Items)> sections, HashSet<string> placed, List<string> corrections)
    {
        if (articles.First(a => a.Id == lead.ArticleId).Page.Kind != PageKind.NotArticle)
        {
            return lead;
        }

        var replacement = DeterministicLayoutEditor.ChooseLead(articles);
        if (replacement.Page.Kind == PageKind.NotArticle)
        {
            return lead;
        }

        var demoted = new LayoutItem(lead.ArticleId, StorySize.Brief, false, null);
        var section = sections.FindIndex(s => s.Items.Any(i => i.ArticleId == replacement.Id));
        if (section >= 0)
        {
            var items = sections[section].Items;
            items[items.FindIndex(i => i.ArticleId == replacement.Id)] = demoted;
        }
        else
        {
            AddToSection(sections, FallbackSectionName, [demoted]);
        }

        placed.Add(replacement.Id);
        corrections.Add($"Led with '{replacement.Id}' instead of '{lead.ArticleId}', which is not an article.");
        return new LayoutItem(replacement.Id, StorySize.Lead, true, DeterministicLayoutEditor.KeepBlocks(replacement, DeterministicLayoutEditor.WordBudget(StorySize.Lead)));
    }

    private static void AddToSection(List<(string Name, List<LayoutItem> Items)> sections, string name, List<LayoutItem> items)
    {
        var existing = sections.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            sections[existing].Items.AddRange(items);
        }
        else
        {
            sections.Add((name, items));
        }
    }

    private static string CleanName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return FallbackSectionName;
        }

        return trimmed.Length <= MaxSectionNameLength ? trimmed : trimmed[..MaxSectionNameLength].TrimEnd();
    }
}
