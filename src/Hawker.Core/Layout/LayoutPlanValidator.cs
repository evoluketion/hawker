using Hawker.Core.Articles;

namespace Hawker.Core.Layout;

public sealed record ValidatedPlan(LayoutPlan Plan, IReadOnlyList<string> Corrections);

/// <summary>
/// Makes any proposed plan safe to render: every article appears exactly once, unknown IDs are dropped, there is one
/// lead, cut points are within the story, and images are only requested where they exist.
/// </summary>
public static class LayoutPlanValidator
{
    public const string FallbackSectionName = "More news";
    private const int MaxSectionNameLength = 40;

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

        var missing = articles.Where(a => !placed.Contains(a.Id)).ToList();
        if (missing.Count > 0)
        {
            corrections.Add($"Added {missing.Count} article(s) the plan left out.");
            AddToSection(sections, FallbackSectionName, missing
                .Select(a => new LayoutItem(a.Id, a.BodySource == BodySource.None ? StorySize.Brief : StorySize.Minor, true, null))
                .ToList());
        }

        var finalLead = Fix(lead with { Size = StorySize.Lead }, byId[lead.ArticleId]);
        var finalSections = sections
            .Where(s => s.Items.Count > 0)
            .Select(s => new LayoutSection(s.Name, s.Items
                .Select(i => Fix(i.Size == StorySize.Lead ? i with { Size = StorySize.Major } : i, byId[i.ArticleId]))
                .ToList()))
            .ToList();

        return new ValidatedPlan(new LayoutPlan(finalLead, finalSections), corrections);
    }

    private static LayoutItem Fix(LayoutItem item, Article article)
    {
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

        return item with
        {
            ShowImage = item.ShowImage && item.Size != StorySize.Brief && article.LeadImage?.DataUri is not null,
            KeepBlocks = textBlocks == 0 ? null : keep,
        };
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
