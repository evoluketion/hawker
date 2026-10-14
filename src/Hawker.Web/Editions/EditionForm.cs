using Hawker.Core.Domain;
using Hawker.Core.Scheduling;

namespace Hawker.Web.Editions;

/// <summary>What the edition editor works on; turned into an <see cref="Edition"/> only once it validates.</summary>
public sealed class EditionForm
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public HashSet<int> FeedIds { get; set; } = [];

    public int ArticleCount { get; set; } = 10;
    public SortMode SortMode { get; set; } = SortMode.MostRecent;
    public int LookbackHours { get; set; } = 24;

    public string TimeZone { get; set; } = TimeZoneInfo.Local.Id;
    public List<ScheduleForm> Schedules { get; set; } = [new()];

    public PaperSize PaperSize { get; set; } = PaperSize.A4;
    public bool Greyscale { get; set; }
    public bool QrCodesOnWholeStories { get; set; } = true;
    public string? PrinterName { get; set; }
    public string? PaperSource { get; set; }
    public int Copies { get; set; } = 1;
    public bool Duplex { get; set; } = true;
    public bool AutoPrint { get; set; } = true;

    public const int MaxArticles = 100;
    public const int MaxLookbackHours = 31 * 24;
    public const int MaxCopies = 20;

    /// <summary>Measured on real feeds with pictures: roughly one and a half stories fill an A4 page.</summary>
    public int EstimatedPages => Math.Max(1, (int)Math.Ceiling(ArticleCount / 1.5));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Give the edition a name; it is printed as the masthead.");
        }

        if (FeedIds.Count == 0)
        {
            errors.Add("Choose at least one feed.");
        }

        if (ArticleCount is < 1 or > MaxArticles)
        {
            errors.Add($"The number of articles must be between 1 and {MaxArticles}.");
        }

        if (LookbackHours is < 1 or > MaxLookbackHours)
        {
            errors.Add("The look-back range must be between 1 hour and 31 days.");
        }

        if (Copies is < 1 or > MaxCopies)
        {
            errors.Add($"Copies must be between 1 and {MaxCopies}.");
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out _))
        {
            errors.Add("Choose a valid time zone.");
        }

        foreach (var (schedule, index) in Schedules.Select((s, i) => (s, i)))
        {
            if (ScheduleCalculator.Validate(schedule.ToEntity()) is { } problem)
            {
                errors.Add($"Schedule {index + 1}: {problem}");
            }
        }

        return errors;
    }

    public static EditionForm From(Edition edition) => new()
    {
        Id = edition.Id,
        Name = edition.Name,
        Enabled = edition.Enabled,
        FeedIds = edition.Feeds.Select(f => f.Id).ToHashSet(),
        ArticleCount = edition.ArticleCount,
        SortMode = edition.SortMode,
        LookbackHours = edition.LookbackHours,
        TimeZone = edition.TimeZone,
        Schedules = edition.Schedules.Select(ScheduleForm.From).ToList(),
        PaperSize = edition.PaperSize,
        Greyscale = edition.Greyscale,
        QrCodesOnWholeStories = edition.QrCodesOnWholeStories,
        PrinterName = edition.PrinterName,
        PaperSource = edition.PaperSource,
        Copies = edition.Copies,
        Duplex = edition.Duplex,
        AutoPrint = edition.AutoPrint,
    };

    /// <summary>Copies the form onto an edition. Feeds are set by the caller, which has the tracked entities.</summary>
    public void ApplyTo(Edition edition)
    {
        edition.Name = Name.Trim();
        edition.Enabled = Enabled;
        edition.ArticleCount = ArticleCount;
        edition.SortMode = SortMode;
        edition.LookbackHours = LookbackHours;
        edition.TimeZone = TimeZone;
        edition.PaperSize = PaperSize;
        edition.Greyscale = Greyscale;
        edition.QrCodesOnWholeStories = QrCodesOnWholeStories;
        edition.PrinterName = string.IsNullOrWhiteSpace(PrinterName) ? null : PrinterName;
        edition.PaperSource = string.IsNullOrWhiteSpace(PaperSource) ? null : PaperSource;
        edition.Copies = Copies;
        edition.Duplex = Duplex;
        edition.AutoPrint = AutoPrint;
    }
}

public sealed class ScheduleForm
{
    public ScheduleKind Kind { get; set; } = ScheduleKind.Daily;
    public List<TimeOnly> Times { get; set; } = [new(6, 0)];
    public HashSet<DayOfWeek> Weekdays { get; set; } = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public int DayOfMonth { get; set; } = 1;
    public string CronExpression { get; set; } = "0 6 * * *";

    public EditionSchedule ToEntity()
    {
        var schedule = new EditionSchedule
        {
            Kind = Kind,
            Times = Kind == ScheduleKind.Daily ? Times.Take(1).ToList() : Times.ToList(),
            Weekdays = Kind == ScheduleKind.Weekly ? Weekdays.ToList() : [],
            DayOfMonth = Kind == ScheduleKind.Monthly ? DayOfMonth : null,
            CronExpression = Kind == ScheduleKind.Cron ? CronExpression.Trim() : "",
        };
        ScheduleCalculator.Normalize(schedule);
        return schedule;
    }

    public static ScheduleForm From(EditionSchedule schedule) => new()
    {
        Kind = schedule.Kind,
        Times = schedule.Times.Count > 0 ? schedule.Times.ToList() : [new(6, 0)],
        Weekdays = schedule.Weekdays.Count > 0 ? schedule.Weekdays.ToHashSet() : [DayOfWeek.Monday],
        DayOfMonth = schedule.DayOfMonth ?? 1,
        CronExpression = schedule.Kind == ScheduleKind.Cron ? schedule.CronExpression : "0 6 * * *",
    };
}
