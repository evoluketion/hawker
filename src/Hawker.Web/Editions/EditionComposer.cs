using Hawker.Core.Articles;
using Hawker.Core.Domain;
using Hawker.Core.Layout;
using Hawker.Core.Selection;
using Hawker.Web.Rendering;
using Hawker.Web.Settings;

namespace Hawker.Web.Editions;

public sealed record ComposedEdition(
    byte[] Pdf,
    string Html,
    IReadOnlyList<Article> Articles,
    LayoutPlan Plan,
    IReadOnlyList<string> Log);

public sealed class NoArticlesException(string message, IReadOnlyList<string> log) : Exception(message)
{
    public IReadOnlyList<string> Log { get; } = log;
}

/// <summary>Builds one issue of an edition: choose articles, prepare them, lay them out, render HTML and PDF.</summary>
public sealed class EditionComposer(
    StorySelectionService selection,
    ArticlePreparationService preparation,
    LayoutEditorResolver layoutEditors,
    NewspaperRenderer htmlRenderer,
    PdfRenderer pdfRenderer,
    TimeProvider time,
    ILogger<EditionComposer> logger)
{
    /// <param name="edition">Must have its Feeds loaded.</param>
    public async Task<ComposedEdition> ComposeAsync(Edition edition, int issueNumber, CancellationToken cancellationToken = default)
    {
        var log = new List<string>();

        var selected = await selection.SelectAsync(edition, cancellationToken);
        log.AddRange(selected.Notes);
        log.Add($"Selected {selected.Articles.Count} article(s).");
        if (selected.Articles.Count == 0)
        {
            throw new NoArticlesException("No articles were available for this edition.", log);
        }

        var imageOptions = new ImageOptions { Greyscale = edition.Greyscale };
        var prepared = await preparation.PrepareAsync(selected.Articles, edition.Feeds.ToDictionary(f => f.Id), imageOptions, cancellationToken);
        log.AddRange(prepared.Notes);

        var plan = await PlanAsync(edition, prepared.Articles, log, cancellationToken);

        var model = NewspaperModelBuilder.Build(
            edition.Name, issueNumber, edition.PaperSize, time.GetUtcNow(), ResolveTimeZone(edition.TimeZone), plan, prepared.Articles);
        var html = await htmlRenderer.RenderHtmlAsync(model);
        var pdf = await pdfRenderer.RenderAsync(html, edition.Name, edition.PaperSize, cancellationToken);
        log.Add($"Rendered {pdf.Length / 1024:N0} KB PDF.");

        return new ComposedEdition(pdf, html, prepared.Articles, plan, log);
    }

    private async Task<LayoutPlan> PlanAsync(Edition edition, IReadOnlyList<Article> articles, List<string> log, CancellationToken cancellationToken)
    {
        var layoutEditor = await layoutEditors.ResolveAsync(cancellationToken);
        LayoutPlan proposed;
        try
        {
            proposed = await layoutEditor.PlanAsync(new LayoutRequest(edition.Name, articles), cancellationToken);
            log.Add($"Laid out by {layoutEditor.Name}.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && layoutEditor is not DeterministicLayoutEditor)
        {
            logger.LogWarning(ex, "{Editor} failed; using the standard layout.", layoutEditor.Name);
            log.Add($"{layoutEditor.Name} could not lay out the edition ({ex.Message}); the standard layout was used.");
            proposed = DeterministicLayoutEditor.Plan(articles);
        }

        var validated = LayoutPlanValidator.Validate(proposed, articles);
        log.AddRange(validated.Corrections.Select(c => $"Layout: {c}"));
        return validated.Plan;
    }

    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
        {
            return zone;
        }

        return TimeZoneInfo.Local;
    }
}
