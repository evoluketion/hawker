using Hawker.Core.Data;
using Hawker.Core.Domain;
using Hawker.Core.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Hawker.Web.Editions;

public sealed record EditionSummary(int Id, string Name, bool Enabled, int FeedCount, IReadOnlyList<string> Schedules, DateTimeOffset? NextRun, TimeZoneInfo Zone, EditionRun? LastRun);

public sealed record SaveResult(int Id, IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>Loads and saves editions for the editor and dashboard.</summary>
public sealed class EditionStore(IDbContextFactory<HawkerDbContext> dbFactory, TimeProvider time)
{
    public async Task<IReadOnlyList<EditionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var editions = await db.Editions.Include(e => e.Schedules).Include(e => e.Feeds).OrderBy(e => e.Name).ToListAsync(cancellationToken);
        var lastRuns = await db.EditionRuns
            .GroupBy(r => r.EditionId)
            .Select(g => g.OrderByDescending(r => r.Id).First())
            .ToListAsync(cancellationToken);

        var now = time.GetUtcNow();
        return editions.Select(e =>
        {
            var zone = EditionComposer.ResolveTimeZone(e.TimeZone);
            return new EditionSummary(
                e.Id,
                e.Name,
                e.Enabled,
                e.Feeds.Count,
                e.Schedules.Select(ScheduleCalculator.Describe).ToList(),
                e.Enabled ? ScheduleCalculator.NextOccurrence(e.Schedules, now, zone) : null,
                zone,
                lastRuns.FirstOrDefault(r => r.EditionId == e.Id));
        }).ToList();
    }

    public async Task<EditionForm?> LoadAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var edition = await db.Editions.Include(e => e.Feeds).Include(e => e.Schedules).SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        return edition is null ? null : EditionForm.From(edition);
    }

    public async Task<SaveResult> SaveAsync(EditionForm form, CancellationToken cancellationToken = default)
    {
        var errors = form.Validate();
        if (errors.Count > 0)
        {
            return new SaveResult(form.Id, errors);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Edition edition;
        if (form.Id == 0)
        {
            edition = new Edition { Name = form.Name };
            db.Editions.Add(edition);
        }
        else
        {
            edition = await db.Editions.Include(e => e.Feeds).Include(e => e.Schedules).SingleOrDefaultAsync(e => e.Id == form.Id, cancellationToken)
                ?? throw new InvalidOperationException("That edition no longer exists.");
            db.EditionSchedules.RemoveRange(edition.Schedules);
        }

        form.ApplyTo(edition);
        edition.Feeds = await db.Feeds.Where(f => form.FeedIds.Contains(f.Id)).ToListAsync(cancellationToken);
        edition.Schedules = form.Schedules.Select(s => s.ToEntity()).ToList();
        await db.SaveChangesAsync(cancellationToken);
        return new SaveResult(edition.Id, []);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Editions.Where(e => e.Id == id).ExecuteDeleteAsync(cancellationToken);
    }
}
