using System.Globalization;
using CronExpressionDescriptor;
using Cronos;
using Hawker.Core.Domain;

namespace Hawker.Core.Scheduling;

/// <summary>
/// Turns schedule presets (daily, several times a day, weekly, monthly) into cron expressions, describes them in plain
/// words, and works out when they next run. Custom schedules use a five-field cron expression directly.
/// </summary>
public static class ScheduleCalculator
{
    /// <summary>Stored as <see cref="EditionSchedule.DayOfMonth"/> to mean the last day of each month.</summary>
    public const int LastDayOfMonth = 31;

    private static readonly DayOfWeek[] WeekOrder =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <returns>A message describing what is wrong, or null if the schedule is usable.</returns>
    public static string? Validate(EditionSchedule schedule)
    {
        switch (schedule.Kind)
        {
            case ScheduleKind.Cron:
                return TryParse(schedule.CronExpression, out _) ? null : $"\"{schedule.CronExpression}\" is not a valid five-field cron expression.";
            case ScheduleKind.Daily or ScheduleKind.Weekly or ScheduleKind.Monthly when schedule.Times.Count == 0:
            case ScheduleKind.MultiDaily when schedule.Times.Count == 0:
                return "Choose at least one time.";
            case ScheduleKind.Weekly when schedule.Weekdays.Count == 0:
                return "Choose at least one day of the week.";
            case ScheduleKind.Monthly when schedule.DayOfMonth is not ({ } d and (>= 1 and <= 28 or LastDayOfMonth)):
                return "Choose a day from 1 to 28, or the last day of the month.";
            default:
                return null;
        }
    }

    /// <summary>The cron expressions a schedule runs on (one per time of day for presets).</summary>
    public static IReadOnlyList<string> CronExpressions(EditionSchedule schedule)
    {
        if (schedule.Kind == ScheduleKind.Cron)
        {
            return [schedule.CronExpression.Trim()];
        }

        var days = schedule.Kind switch
        {
            ScheduleKind.Weekly => string.Join(',', schedule.Weekdays.Distinct().Order().Select(d => (int)d)),
            _ => "*",
        };
        var dayOfMonth = schedule.Kind == ScheduleKind.Monthly
            ? schedule.DayOfMonth == LastDayOfMonth ? "L" : schedule.DayOfMonth!.Value.ToString(CultureInfo.InvariantCulture)
            : "*";

        return Times(schedule).Select(t => $"{t.Minute} {t.Hour} {dayOfMonth} * {days}").ToList();
    }

    /// <summary>Stores the generated expressions on the schedule, for display and troubleshooting.</summary>
    public static void Normalize(EditionSchedule schedule)
    {
        if (schedule.Kind != ScheduleKind.Cron)
        {
            schedule.Times = Times(schedule).ToList();
            if (schedule.Kind is ScheduleKind.Daily)
            {
                schedule.Times = schedule.Times.Take(1).ToList();
            }

            schedule.CronExpression = string.Join("; ", CronExpressions(schedule));
        }
    }

    public static string Describe(EditionSchedule schedule)
    {
        var at = $"at {JoinWords(Times(schedule).Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)))}";
        return schedule.Kind switch
        {
            ScheduleKind.Daily or ScheduleKind.MultiDaily => $"Every day {at}",
            ScheduleKind.Weekly => $"{DescribeDays(schedule.Weekdays)} {at}",
            ScheduleKind.Monthly when schedule.DayOfMonth == LastDayOfMonth => $"On the last day of every month {at}",
            ScheduleKind.Monthly => $"On the {Ordinal(schedule.DayOfMonth ?? 1)} of every month {at}",
            _ => TryParse(schedule.CronExpression, out _)
                ? ExpressionDescriptor.GetDescription(schedule.CronExpression, new Options { Use24HourTimeFormat = true, Locale = "en" })
                : "Invalid custom schedule",
        };
    }

    /// <summary>Occurrences after <paramref name="from"/> (exclusive) up to <paramref name="to"/> (inclusive).</summary>
    public static IEnumerable<DateTimeOffset> Occurrences(EditionSchedule schedule, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        if (Validate(schedule) is not null)
        {
            return [];
        }

        return CronExpressions(schedule)
            .Select(Parse)
            .SelectMany(c => c.GetOccurrences(from, to, zone, fromInclusive: false, toInclusive: true))
            .Distinct()
            .Order();
    }

    public static DateTimeOffset? NextOccurrence(IEnumerable<EditionSchedule> schedules, DateTimeOffset after, TimeZoneInfo zone) =>
        schedules
            .Where(s => Validate(s) is null)
            .SelectMany(s => CronExpressions(s).Select(Parse))
            .Select(c => c.GetNextOccurrence(after, zone, inclusive: false))
            .Where(o => o.HasValue)
            .Min();

    public static IReadOnlyList<DateTimeOffset> NextOccurrences(IEnumerable<EditionSchedule> schedules, DateTimeOffset after, TimeZoneInfo zone, int count)
    {
        var list = schedules.ToList();
        var result = new List<DateTimeOffset>();
        var cursor = after;
        while (result.Count < count && NextOccurrence(list, cursor, zone) is { } next)
        {
            result.Add(next);
            cursor = next;
        }

        return result;
    }

    /// <summary>
    /// A look-back range that covers the longest gap between runs, so nothing published between two editions is missed
    /// (a weekday paper on Monday reaches back over the weekend). Between 1 hour and 31 days.
    /// </summary>
    public static int SuggestedLookbackHours(IEnumerable<EditionSchedule> schedules, DateTimeOffset now, TimeZoneInfo zone)
    {
        var runs = NextOccurrences(schedules, now, zone, 60);
        if (runs.Count < 2)
        {
            return 24;
        }

        var longestGap = runs.Zip(runs.Skip(1), (a, b) => b - a).Max();
        return Math.Clamp((int)Math.Ceiling(longestGap.TotalHours), 1, 31 * 24);
    }

    private static IEnumerable<TimeOnly> Times(EditionSchedule schedule) =>
        schedule.Times.Select(t => new TimeOnly(t.Hour, t.Minute)).Distinct().Order();

    private static CronExpression Parse(string expression) => CronExpression.Parse(expression.Trim(), CronFormat.Standard);

    private static bool TryParse(string? expression, out CronExpression? cron)
    {
        cron = null;
        return !string.IsNullOrWhiteSpace(expression) && CronExpression.TryParse(expression.Trim(), CronFormat.Standard, out cron);
    }

    private static string DescribeDays(IEnumerable<DayOfWeek> weekdays)
    {
        var days = WeekOrder.Where(weekdays.Contains).ToList();
        return days.Count switch
        {
            7 => "Every day",
            5 when days.SequenceEqual(WeekOrder.Take(5)) => "Weekdays",
            2 when days.SequenceEqual(WeekOrder.Skip(5)) => "Weekends",
            _ => $"Every {JoinWords(days.Select(d => d.ToString()))}",
        };
    }

    private static string JoinWords(IEnumerable<string> words)
    {
        var list = words.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list[..^1])} and {list[^1]}";
    }

    private static string Ordinal(int day) => day switch
    {
        11 or 12 or 13 => $"{day}th",
        _ when day % 10 == 1 => $"{day}st",
        _ when day % 10 == 2 => $"{day}nd",
        _ when day % 10 == 3 => $"{day}rd",
        _ => $"{day}th",
    };
}
