namespace Hawker.Core.Domain;

public class EditionRun
{
    public int Id { get; set; }
    public int EditionId { get; set; }
    public Edition? Edition { get; set; }

    /// <summary>The schedule time this run was for; null for manual runs and previews.</summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public RunTrigger Trigger { get; set; }

    public int IssueNumber { get; set; }
    public string Log { get; set; } = "";
    public string? PdfPath { get; set; }
    public bool Printed { get; set; }
    public DateTimeOffset? PrintedAt { get; set; }

    /// <summary>A one-line reason when the run failed or did not print.</summary>
    public string? Error { get; set; }
    public List<string> ArticleUrls { get; set; } = [];
}

public enum RunStatus
{
    Queued,
    Running,
    Succeeded,

    /// <summary>The paper was built and archived but printing failed; it can be reprinted from the archive.</summary>
    PrintFailed,

    Failed,
}

public enum RunTrigger
{
    Schedule,
    Manual,
    Preview,
    MissedOnStartup,
}
