using Hawker.Core.Data;
using Hawker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hawker.Core.Selection;

/// <summary>Remembers which articles each edition has printed so they are not printed again.</summary>
public sealed class SeenArticleStore(IDbContextFactory<HawkerDbContext> dbFactory, TimeProvider time)
{
    public async Task<IReadOnlySet<string>> GetSeenAsync(int editionId, IEnumerable<string> urlHashes, CancellationToken cancellationToken = default)
    {
        var candidates = urlHashes.Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var seen = await db.SeenArticles
            .Where(s => s.EditionId == editionId && candidates.Contains(s.UrlHash))
            .Select(s => s.UrlHash)
            .ToListAsync(cancellationToken);
        return seen.ToHashSet();
    }

    public async Task MarkSeenAsync(int editionId, IEnumerable<string> urlHashes, CancellationToken cancellationToken = default)
    {
        var hashes = urlHashes.Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.SeenArticles
            .Where(s => s.EditionId == editionId && hashes.Contains(s.UrlHash))
            .Select(s => s.UrlHash)
            .ToListAsync(cancellationToken);

        var now = time.GetUtcNow();
        db.SeenArticles.AddRange(hashes.Except(existing).Select(h => new SeenArticle { EditionId = editionId, UrlHash = h, FirstSeen = now }));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Drops records old enough that the article can no longer fall inside any look-back range.</summary>
    public async Task<int> PruneAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var cutoff = time.GetUtcNow() - olderThan;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.SeenArticles.Where(s => s.FirstSeen < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
