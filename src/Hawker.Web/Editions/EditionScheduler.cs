using Hawker.Core.Data;
using Hawker.Core.Domain;
using Hawker.Core.Scheduling;
using Hawker.Core.Selection;
using Microsoft.EntityFrameworkCore;

namespace Hawker.Web.Editions;

/// <summary>
/// Checks every 30 seconds which editions are due and queues them. Checking often, rather than sleeping until the next
/// run, keeps the timing right after the PC sleeps or the clock changes, and picks up schedule edits straight away.
/// </summary>
public sealed class EditionScheduler(
    IDbContextFactory<HawkerDbContext> dbFactory,
    EditionRunQueue queue,
    SeenArticleStore seenArticles,
    TimeProvider time,
    ILogger<EditionScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>Already-printed records are kept long enough to outlast any look-back range.</summary>
    private static readonly TimeSpan SeenRetention = TimeSpan.FromDays(62);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var now = time.GetUtcNow();
        await TickAsync(now, lastCheck: null, stoppingToken);
        var lastCheck = now;
        var lastPrune = DateOnly.MinValue;

        using var timer = new PeriodicTimer(CheckInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            now = time.GetUtcNow();
            try
            {
                await TickAsync(now, lastCheck, stoppingToken);
                lastCheck = now;

                if (DateOnly.FromDateTime(now.UtcDateTime) != lastPrune)
                {
                    lastPrune = DateOnly.FromDateTime(now.UtcDateTime);
                    var pruned = await seenArticles.PruneAsync(SeenRetention, stoppingToken);
                    if (pruned > 0)
                    {
                        logger.LogInformation("Forgot {Count} old printed-article records", pruned);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Keep lastCheck where it was so the next tick looks at this window again.
                logger.LogError(ex, "Schedule check failed");
            }
        }
    }

    /// <param name="lastCheck">Null on startup, when missed runs are caught up instead.</param>
    public async Task<DuePlan> TickAsync(DateTimeOffset now, DateTimeOffset? lastCheck, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.AppSettings.SingleAsync(cancellationToken);
        var grace = TimeSpan.FromMinutes(Math.Max(0, settings.MissedRunGraceMinutes));

        var editions = await db.Editions
            .Where(e => e.Enabled)
            .Include(e => e.Schedules)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Schedules,
                e.TimeZone,
                LastScheduled = e.Runs
                    .Where(r => r.Trigger == RunTrigger.Schedule || r.Trigger == RunTrigger.MissedOnStartup)
                    .Max(r => (DateTimeOffset?)r.StartedAt),
            })
            .ToListAsync(cancellationToken);

        var scheduled = editions
            .Select(e => new ScheduledEdition(e.Id, e.Name, e.Schedules, EditionComposer.ResolveTimeZone(e.TimeZone), e.LastScheduled))
            .ToList();

        var plan = lastCheck is { } last
            ? DueRunPlanner.Plan(scheduled, last, now, grace)
            : DueRunPlanner.PlanMissed(scheduled, now, grace);

        foreach (var run in plan.Due)
        {
            logger.LogInformation("Queueing edition {Edition} for {ScheduledFor}", run.EditionId, run.ScheduledFor);
            queue.Enqueue(new RunRequest(run.EditionId, run.Trigger, run.ScheduledFor));
        }

        foreach (var skipped in plan.Skipped)
        {
            logger.LogWarning("Skipped {Edition}'s run for {ScheduledFor}: it is older than the {Grace}-minute catch-up window",
                skipped.Name, skipped.ScheduledFor, grace.TotalMinutes);
        }

        return plan;
    }
}
