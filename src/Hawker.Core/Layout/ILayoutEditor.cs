using Hawker.Core.Articles;

namespace Hawker.Core.Layout;

/// <summary>Decides the arrangement of an edition: lead story, sections, order, sizes and where long stories are cut.</summary>
public interface ILayoutEditor
{
    /// <summary>Shown in the run log, e.g. "standard layout" or "Claude (claude-opus-5-5)".</summary>
    string Name { get; }

    Task<LayoutPlan> PlanAsync(LayoutRequest request, CancellationToken cancellationToken = default);
}

/// <param name="Articles">In ranked order (most recent or most popular first).</param>
public sealed record LayoutRequest(string EditionName, IReadOnlyList<Article> Articles);
