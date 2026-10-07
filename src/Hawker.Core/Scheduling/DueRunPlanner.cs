using Hawker.Core.Domain;

namespace Hawker.Core.Scheduling;

/// <param name="LastScheduledRunAt">When the edition last started a scheduled (or caught-up) run, if ever.</param>
public sealed record ScheduledEdition(int EditionId, string Name, IReadOnlyList<EditionSchedule> Schedules, TimeZoneInfo Zone, DateTimeOffset? LastScheduledRunAt);

public sealed record DueRun(int EditionId, DateTimeOffset ScheduledFor, RunTrigger Trigger);

public sealed record SkippedRun(int EditionId, string Name, DateTimeOffset ScheduledFor);

public sealed record DuePlan(IReadOnlyList<DueRun> Due, IReadOnlyList<SkippedRun> Skipped);

/// <summary>
/// Decides which editions should run now. At most one run per edition is started at a time: if several occurrences
/// were passed (the PC was asleep or off), only the latest is run, and only if it is within the grace period, so a
/// machine waking after a long weekend prints one paper rather than a pile of stale ones.
/// </summary>
public static class DueRunPlanner
{
    /// <summary>For the scheduler's regular check: occurrences after <paramref name="lastCheck"/> up to <paramref name="now"/>.</summary>
    public static DuePlan Plan(IEnumerable<ScheduledEdition> editions, DateTimeOffset lastCheck, DateTimeOffset now, TimeSpan grace) =>
        PlanWindow(editions, _ => lastCheck, now, grace, RunTrigger.Schedule);

    /// <summary>
    /// At startup: the latest occurrence within the grace period that has no scheduled run since, for each edition.
    /// </summary>
    public static DuePlan PlanMissed(IEnumerable<ScheduledEdition> editions, DateTimeOffset now, TimeSpan grace) =>
        PlanWindow(editions, e => Max(now - grace, e.LastScheduledRunAt), now, grace, RunTrigger.MissedOnStartup);

    private static DuePlan PlanWindow(
        IEnumerable<ScheduledEdition> editions, Func<ScheduledEdition, DateTimeOffset> windowStart, DateTimeOffset now, TimeSpan grace, RunTrigger trigger)
    {
        var due = new List<DueRun>();
        var skipped = new List<SkippedRun>();

        foreach (var edition in editions)
        {
            var from = windowStart(edition);
            if (from >= now)
            {
                continue;
            }

            var latest = edition.Schedules
                .SelectMany(s => ScheduleCalculator.Occurrences(s, from, now, edition.Zone))
                .DefaultIfEmpty()
                .Max();
            if (latest == default)
            {
                continue;
            }

            if (now - latest > grace)
            {
                skipped.Add(new SkippedRun(edition.EditionId, edition.Name, latest));
            }
            else
            {
                due.Add(new DueRun(edition.EditionId, latest, trigger));
            }
        }

        return new DuePlan(due, skipped);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset? b) => b is { } value && value > a ? value : a;
}
