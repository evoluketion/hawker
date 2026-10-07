using Hawker.Core.Domain;
using Hawker.Core.Scheduling;

namespace Hawker.Tests.Scheduling;

public class ScheduleCalculatorTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static EditionSchedule Daily(params (int H, int M)[] times) =>
        new() { Kind = times.Length > 1 ? ScheduleKind.MultiDaily : ScheduleKind.Daily, Times = times.Select(t => new TimeOnly(t.H, t.M)).ToList() };

    private static EditionSchedule Weekly(int hour, params DayOfWeek[] days) =>
        new() { Kind = ScheduleKind.Weekly, Times = [new TimeOnly(hour, 0)], Weekdays = days.ToList() };

    private static EditionSchedule Monthly(int day, int hour = 9) =>
        new() { Kind = ScheduleKind.Monthly, Times = [new TimeOnly(hour, 0)], DayOfMonth = day };

    private static EditionSchedule Cron(string expression) => new() { Kind = ScheduleKind.Cron, CronExpression = expression };

    private static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    [Fact]
    public void Presets_become_cron_expressions()
    {
        Assert.Equal(["0 6 * * *"], ScheduleCalculator.CronExpressions(Daily((6, 0))));
        Assert.Equal(["0 6 * * *", "30 12 * * *", "0 18 * * *"], ScheduleCalculator.CronExpressions(Daily((18, 0), (6, 0), (12, 30))));
        Assert.Equal(["0 8 * * 0,6"], ScheduleCalculator.CronExpressions(Weekly(8, DayOfWeek.Saturday, DayOfWeek.Sunday)));
        Assert.Equal(["0 9 1 * *"], ScheduleCalculator.CronExpressions(Monthly(1)));
        Assert.Equal(["0 9 L * *"], ScheduleCalculator.CronExpressions(Monthly(ScheduleCalculator.LastDayOfMonth)));
        Assert.Equal(["0 6 * * 1-5"], ScheduleCalculator.CronExpressions(Cron(" 0 6 * * 1-5 ")));
    }

    [Theory]
    [InlineData(ScheduleKind.Daily, "Every day at 06:00")]
    [InlineData(ScheduleKind.MultiDaily, "Every day at 06:00, 12:30 and 18:00")]
    public void Describes_daily_schedules(ScheduleKind kind, string expected)
    {
        var schedule = kind == ScheduleKind.Daily ? Daily((6, 0)) : Daily((6, 0), (12, 30), (18, 0));

        Assert.Equal(expected, ScheduleCalculator.Describe(schedule));
    }

    [Fact]
    public void Describes_weekly_and_monthly_schedules()
    {
        Assert.Equal("Every Sunday at 08:00", ScheduleCalculator.Describe(Weekly(8, DayOfWeek.Sunday)));
        Assert.Equal("Weekdays at 07:00", ScheduleCalculator.Describe(Weekly(7, Weekdays)));
        Assert.Equal("Weekends at 08:00", ScheduleCalculator.Describe(Weekly(8, DayOfWeek.Sunday, DayOfWeek.Saturday)));
        Assert.Equal("Every Monday, Wednesday and Friday at 07:00",
            ScheduleCalculator.Describe(Weekly(7, DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Wednesday)));
        Assert.Equal("On the 1st of every month at 09:00", ScheduleCalculator.Describe(Monthly(1)));
        Assert.Equal("On the 22nd of every month at 09:00", ScheduleCalculator.Describe(Monthly(22)));
        Assert.Equal("On the 13th of every month at 09:00", ScheduleCalculator.Describe(Monthly(13)));
        Assert.Equal("On the last day of every month at 09:00", ScheduleCalculator.Describe(Monthly(ScheduleCalculator.LastDayOfMonth)));
    }

    [Fact]
    public void Describes_custom_cron_in_words()
    {
        Assert.Equal("At 06:00, Monday through Friday", ScheduleCalculator.Describe(Cron("0 6 * * 1-5")));
        Assert.Equal("Invalid custom schedule", ScheduleCalculator.Describe(Cron("not cron")));
    }

    [Fact]
    public void Validates_each_kind()
    {
        Assert.Null(ScheduleCalculator.Validate(Daily((6, 0))));
        Assert.NotNull(ScheduleCalculator.Validate(new EditionSchedule { Kind = ScheduleKind.Daily, Times = [] }));
        Assert.NotNull(ScheduleCalculator.Validate(Weekly(8)));
        Assert.NotNull(ScheduleCalculator.Validate(Monthly(30)));
        Assert.NotNull(ScheduleCalculator.Validate(Monthly(0)));
        Assert.Null(ScheduleCalculator.Validate(Monthly(28)));
        Assert.NotNull(ScheduleCalculator.Validate(Cron("61 * * * *")));
        Assert.NotNull(ScheduleCalculator.Validate(Cron("")));
    }

    [Fact]
    public void Next_runs_follow_the_edition_time_zone_across_a_clock_change()
    {
        // UK clocks go back at 02:00 on Sunday 25 October 2026.
        var after = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        var runs = ScheduleCalculator.NextOccurrences([Daily((6, 0))], after, London, 3);

        Assert.Equal(
        [
            new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.FromHours(0)),  // GMT
            new DateTimeOffset(2026, 10, 26, 6, 0, 0, TimeSpan.FromHours(0)),
            new DateTimeOffset(2026, 10, 27, 6, 0, 0, TimeSpan.FromHours(0)),
        ], runs);
        Assert.All(runs, r => Assert.Equal(6, TimeZoneInfo.ConvertTime(r, London).Hour));
    }

    [Fact]
    public void Next_runs_merge_several_schedules_in_order()
    {
        var after = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero); // a Monday

        var runs = ScheduleCalculator.NextOccurrences([Weekly(17, Weekdays), Daily((6, 0))], after, TimeZoneInfo.Utc, 3);

        Assert.Equal([after.AddHours(6), after.AddHours(17), after.AddDays(1).AddHours(6)], runs);
    }

    [Fact]
    public void Last_day_of_month_lands_on_short_months()
    {
        var runs = ScheduleCalculator.NextOccurrences([Monthly(ScheduleCalculator.LastDayOfMonth)],
            new DateTimeOffset(2027, 1, 31, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, 2);

        Assert.Equal([new DateTimeOffset(2027, 2, 28, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 3, 31, 9, 0, 0, TimeSpan.Zero)], runs);
    }

    [Theory]
    [InlineData("daily", 24)]
    [InlineData("three-a-day", 12)]
    [InlineData("weekdays", 72)]
    [InlineData("weekly", 168)]
    [InlineData("monthly", 744)]
    public void Suggests_a_look_back_covering_the_longest_gap(string kind, int expectedHours)
    {
        EditionSchedule schedule = kind switch
        {
            "daily" => Daily((6, 0)),
            "three-a-day" => Daily((6, 0), (12, 0), (18, 0)),
            "weekdays" => Weekly(7, Weekdays),
            "weekly" => Weekly(8, DayOfWeek.Sunday),
            _ => Monthly(1),
        };

        Assert.Equal(expectedHours, ScheduleCalculator.SuggestedLookbackHours([schedule], new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Normalize_stores_the_generated_expressions()
    {
        var schedule = Daily((18, 0), (6, 0));

        ScheduleCalculator.Normalize(schedule);

        Assert.Equal("0 6 * * *; 0 18 * * *", schedule.CronExpression);
        Assert.Equal([new TimeOnly(6, 0), new TimeOnly(18, 0)], schedule.Times);
    }
}
