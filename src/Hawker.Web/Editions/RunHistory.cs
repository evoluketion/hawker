using Hawker.Core.Data;
using Hawker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Hawker.Web.Editions;

/// <summary>Reads past runs for the dashboard, archive and run pages.</summary>
public sealed class RunHistory(IDbContextFactory<HawkerDbContext> dbFactory)
{
    public async Task<IReadOnlyList<EditionRun>> RecentAsync(int count, int? editionId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.EditionRuns.Include(r => r.Edition).AsNoTracking();
        if (editionId is { } id)
        {
            query = query.Where(r => r.EditionId == id);
        }

        return await query.OrderByDescending(r => r.Id).Take(count).ToListAsync(cancellationToken);
    }

    public async Task<EditionRun?> GetAsync(int runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.EditionRuns.Include(r => r.Edition).AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
    }

    public static string DescribeTrigger(RunTrigger trigger) => trigger switch
    {
        RunTrigger.Schedule => "Scheduled",
        RunTrigger.MissedOnStartup => "Caught up",
        RunTrigger.Preview => "Preview",
        _ => "By hand",
    };
}
