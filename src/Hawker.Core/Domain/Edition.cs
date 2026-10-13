namespace Hawker.Core.Domain;

public class Edition
{
    public int Id { get; set; }

    /// <summary>Printed as the masthead.</summary>
    public required string Name { get; set; }

    public bool Enabled { get; set; } = true;

    public List<Feed> Feeds { get; set; } = [];
    public List<EditionSchedule> Schedules { get; set; } = [];

    /// <summary>IANA or Windows time zone ID the schedules are evaluated in.</summary>
    public string TimeZone { get; set; } = TimeZoneInfo.Local.Id;

    public int ArticleCount { get; set; } = 10;
    public SortMode SortMode { get; set; } = SortMode.MostRecent;
    public int LookbackHours { get; set; } = 24;

    public PaperSize PaperSize { get; set; } = PaperSize.A4;
    public bool Greyscale { get; set; }

    /// <summary>Null means the system default printer.</summary>
    public string? PrinterName { get; set; }
    public int Copies { get; set; } = 1;
    public bool Duplex { get; set; } = true;
    public bool AutoPrint { get; set; } = true;

    public int IssueCounter { get; set; }

    public List<EditionRun> Runs { get; set; } = [];
}

public enum SortMode
{
    MostRecent,
    MostPopular,
}

public enum PaperSize
{
    A4,
    Letter,
}
