namespace Hawker.Core.Layout;

/// <summary>
/// How an edition's articles are arranged. A plan only refers to articles by ID and says where and how big they go;
/// it never carries text, so whoever writes the plan (rules or AI) cannot change what is printed.
/// </summary>
/// <param name="Lead">The front-page story. It does not also appear in a section.</param>
public sealed record LayoutPlan(LayoutItem Lead, IReadOnlyList<LayoutSection> Sections)
{
    public IEnumerable<LayoutItem> Items => Sections.SelectMany(s => s.Items).Prepend(Lead);
}

public sealed record LayoutSection(string Name, IReadOnlyList<LayoutItem> Items);

/// <param name="ArticleId">The article's <c>Id</c>.</param>
/// <param name="Size">How much space the story gets.</param>
/// <param name="ShowImage">Whether to print the lead image (ignored for briefs).</param>
/// <param name="KeepBlocks">Text blocks to print before cutting to "Continued online"; null prints the whole story.</param>
public sealed record LayoutItem(string ArticleId, StorySize Size, bool ShowImage, int? KeepBlocks);

public enum StorySize
{
    /// <summary>Front-page lead: full width, largest headline.</summary>
    Lead,

    /// <summary>Full width within a section.</summary>
    Major,

    /// <summary>Half width.</summary>
    Minor,

    /// <summary>Headline and opening paragraph only, grouped into an "In brief" panel.</summary>
    Brief,
}
