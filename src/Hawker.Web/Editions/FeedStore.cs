using System.Text.RegularExpressions;
using Hawker.Core.Data;
using Hawker.Core.Domain;
using Hawker.Core.Feeds;
using Microsoft.EntityFrameworkCore;

namespace Hawker.Web.Editions;

public sealed record FeedSummary(Feed Feed, int EditionCount, ScoreSource EffectiveScoreSource);

public sealed record FeedTestResult(IReadOnlyList<FeedItem> Items, string? Error, ScoreSource ScoreSource);

/// <summary>Loads, saves and tries out feeds.</summary>
public sealed class FeedStore(IDbContextFactory<HawkerDbContext> dbFactory, FeedFetcher fetcher)
{
    public async Task<IReadOnlyList<FeedSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var feeds = await db.Feeds.Include(f => f.Editions).OrderBy(f => f.Name).ToListAsync(cancellationToken);
        return feeds.Select(f => new FeedSummary(f, f.Editions.Count, PopularityExtractor.ResolveSource(f))).ToList();
    }

    public async Task<Feed?> LoadAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Feeds.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
    }

    public static IReadOnlyList<string> Validate(Feed feed)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(feed.Name))
        {
            errors.Add("Give the feed a name; it is printed as the source of each story.");
        }

        if (!Uri.TryCreate(feed.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            errors.Add("Enter the feed's full web address, starting with http:// or https://.");
        }

        if (feed.MaxItemsPerRun is < 1)
        {
            errors.Add("The per-edition cap must be at least 1, or left empty for no cap.");
        }

        if (feed.ScoreSource == ScoreSource.Regex)
        {
            if (string.IsNullOrWhiteSpace(feed.ScoreRegex))
            {
                errors.Add("Enter a score pattern, or choose a different score source.");
            }
            else
            {
                try
                {
                    _ = new RegexPopularityExtractor(feed.ScoreRegex);
                }
                catch (ArgumentException ex)
                {
                    errors.Add($"The score pattern is not valid: {ex.Message}");
                }
            }
        }

        return errors;
    }

    public async Task<SaveResult> SaveAsync(Feed feed, CancellationToken cancellationToken = default)
    {
        var errors = Validate(feed);
        if (errors.Count > 0)
        {
            return new SaveResult(feed.Id, errors);
        }

        feed.Name = feed.Name.Trim();
        feed.Url = feed.Url.Trim();
        feed.ScoreRegex = string.IsNullOrWhiteSpace(feed.ScoreRegex) ? null : feed.ScoreRegex;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var normalized = UrlNormalizer.Normalize(feed.Url);
        var others = await db.Feeds.Where(f => f.Id != feed.Id).Select(f => new { f.Name, f.Url }).ToListAsync(cancellationToken);
        if (others.FirstOrDefault(f => UrlNormalizer.Normalize(f.Url) == normalized) is { } duplicate)
        {
            return new SaveResult(feed.Id, [$"This feed is already added as \"{duplicate.Name}\"."]);
        }

        if (feed.Id == 0)
        {
            db.Feeds.Add(feed);
        }
        else
        {
            db.Feeds.Update(feed);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new SaveResult(feed.Id, []);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Feeds.Where(f => f.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Fetches the feed as it would be for an edition, without saving anything.</summary>
    public async Task<FeedTestResult> TestAsync(Feed feed, CancellationToken cancellationToken = default)
    {
        var errors = Validate(feed);
        if (errors.Count > 0)
        {
            return new FeedTestResult([], string.Join(" ", errors), PopularityExtractor.ResolveSource(feed));
        }

        var result = await fetcher.FetchAsync(feed, cancellationToken);
        return new FeedTestResult(result.Items, result.Error, PopularityExtractor.ResolveSource(feed));
    }
}
