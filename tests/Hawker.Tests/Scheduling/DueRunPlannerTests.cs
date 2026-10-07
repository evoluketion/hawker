using Hawker.Core.Domain;
using Hawker.Core.Scheduling;

namespace Hawker.Tests.Scheduling;

public class DueRunPlannerTests
{
    private static readonly DateTimeOffset Monday6am = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(2);

    private static ScheduledEdition Edition(int id, DateTimeOffset? lastRun = null, params (int H, int M)[] times) => new(
        id,
        $"Edition {id}",
        [new EditionSchedule { Kind = ScheduleKind.MultiDaily, Times = times.Select(t => new TimeOnly(t.H, t.M)).ToList() }],
        TimeZoneInfo.Utc,
        lastRun);

    [Fact]
    public void Queues_an_edition_whose_time_falls_in_the_window()
    {
        var plan = DueRunPlanner.Plan([Edition(1, null, (6, 0)), Edition(2, null, (7, 0))], Monday6am.AddSeconds(-30), Monday6am.AddSeconds(5), Grace);

        var due = Assert.Single(plan.Due);
        Assert.Equal(new DueRun(1, Monday6am, RunTrigger.Schedule), due);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void A_time_exactly_at_the_previous_check_is_not_queued_twice()
    {
        var first = DueRunPlanner.Plan([Edition(1, null, (6, 0))], Monday6am.AddSeconds(-30), Monday6am, Grace);
        var second = DueRunPlanner.Plan([Edition(1, null, (6, 0))], Monday6am, Monday6am.AddSeconds(30), Grace);

        Assert.Single(first.Due);
        Assert.Empty(second.Due);
    }

    [Fact]
    public void After_a_sleep_only_the_latest_missed_run_is_queued()
    {
        // Asleep from 05:00 to 13:10 through runs at 06:00, 09:00 and 12:00.
        var plan = DueRunPlanner.Plan([Edition(1, null, (6, 0), (9, 0), (12, 0))], Monday6am.AddHours(-1), Monday6am.AddHours(7).AddMinutes(10), Grace);

        Assert.Equal(new DueRun(1, Monday6am.AddHours(6), RunTrigger.Schedule), Assert.Single(plan.Due));
    }

    [Fact]
    public void Runs_older_than_the_grace_period_are_skipped_not_printed()
    {
        // Asleep over the weekend: Friday 06:00 to Monday 05:00.
        var plan = DueRunPlanner.Plan([Edition(1, null, (6, 0))], Monday6am.AddDays(-3), Monday6am.AddHours(-1), Grace);

        Assert.Empty(plan.Due);
        Assert.Equal(new SkippedRun(1, "Edition 1", Monday6am.AddDays(-1)), Assert.Single(plan.Skipped));
    }

    [Fact]
    public void Startup_catches_up_a_run_missed_within_the_grace_period()
    {
        var plan = DueRunPlanner.PlanMissed([Edition(1, Monday6am.AddDays(-1), (6, 0))], Monday6am.AddMinutes(45), Grace);

        Assert.Equal(new DueRun(1, Monday6am, RunTrigger.MissedOnStartup), Assert.Single(plan.Due));
    }

    [Fact]
    public void Startup_does_not_repeat_a_run_that_already_happened()
    {
        var plan = DueRunPlanner.PlanMissed([Edition(1, Monday6am.AddSeconds(3), (6, 0))], Monday6am.AddMinutes(45), Grace);

        Assert.Empty(plan.Due);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void Startup_ignores_runs_missed_long_ago()
    {
        var plan = DueRunPlanner.PlanMissed([Edition(1, null, (6, 0))], Monday6am.AddHours(5), Grace);

        Assert.Empty(plan.Due);
        Assert.Empty(plan.Skipped); // the window only reaches back over the grace period
    }

    [Fact]
    public void Invalid_schedules_never_run()
    {
        var broken = new ScheduledEdition(1, "Broken", [new EditionSchedule { Kind = ScheduleKind.Cron, CronExpression = "nope" }], TimeZoneInfo.Utc, null);

        Assert.Empty(DueRunPlanner.Plan([broken], Monday6am.AddDays(-1), Monday6am, Grace).Due);
    }
}
