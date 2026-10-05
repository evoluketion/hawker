namespace Hawker.Core.Domain;

public class EditionRun
{
    public int Id { get; set; }
    public int EditionId { get; set; }
    public Edition? Edition { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public RunTrigger Trigger { get; set; }

    public int IssueNumber { get; set; }
    public string Log { get; set; } = "";
    public string? PdfPath { get; set; }
    public bool Printed { get; set; }
    public List<string> ArticleUrls { get; set; } = [];
}

public enum RunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
}

public enum RunTrigger
{
    Schedule,
    Manual,
    Preview,
    MissedOnStartup,
}
