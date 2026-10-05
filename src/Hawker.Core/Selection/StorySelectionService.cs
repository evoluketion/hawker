using Hawker.Core.Domain;
using Hawker.Core.Feeds;

namespace Hawker.Core.Selection;

/// <summary>Fetches an edition's feeds and chooses the articles that will go into it.</summary>
public sealed class StorySelectionService(FeedFetcher fetcher, SeenArticleStore seenStore, TimeProvider time)
{
    /// <param name="edition">Must have its Feeds loaded.</param>
    public async Task<SelectionResult> SelectAsync(Edition edition, CancellationToken cancellationToken = default)
    {
        var results = await fetcher.FetchAllAsync(edition.Feeds, cancellationToken);
        var items = results.SelectMany(r => r.Items).ToList();

        var seen = await seenStore.GetSeenAsync(edition.Id, items.Select(i => UrlNormalizer.Hash(i.Url)), cancellationToken);
        var rules = edition.Feeds.ToDictionary(f => f.Id, FeedRules.For);
        var selection = ArticleSelector.Select(items, SelectionOptions.For(edition, time.GetUtcNow()), rules, seen);

        var fetchNotes = results.Select(r => r.Error is null
            ? $"{r.Feed.Name}: {r.Items.Count} items."
            : $"{r.Feed.Name}: failed to fetch ({r.Error}).");

        return selection with { Notes = [.. fetchNotes, .. selection.Notes] };
    }
}
