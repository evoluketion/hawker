namespace Hawker.Core.Domain;

public class EditionSchedule
{
    public int Id { get; set; }
    public int EditionId { get; set; }
    public Edition? Edition { get; set; }

    public ScheduleKind Kind { get; set; } = ScheduleKind.Daily;

    /// <summary>Times of day the edition runs. One entry for Daily/Weekly/Monthly, several for MultiDaily.</summary>
    public List<TimeOnly> Times { get; set; } = [new(6, 0)];

    /// <summary>Days the edition runs on, for Weekly.</summary>
    public List<DayOfWeek> Weekdays { get; set; } = [];

    /// <summary>Day of the month (1–31), for Monthly.</summary>
    public int? DayOfMonth { get; set; }

    /// <summary>Generated from the fields above, or entered directly when Kind is Cron.</summary>
    public string CronExpression { get; set; } = "0 6 * * *";
}

public enum ScheduleKind
{
    Daily,
    MultiDaily,
    Weekly,
    Monthly,
    Cron,
}
