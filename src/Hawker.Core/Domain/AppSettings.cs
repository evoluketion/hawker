namespace Hawker.Core.Domain;

/// <summary>Single-row table of settings editable from the UI.</summary>
public class AppSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public string ClaudeModel { get; set; } = "claude-opus-5-5";

    /// <summary>Encrypted with ASP.NET Data Protection. The ANTHROPIC_API_KEY environment variable takes precedence.</summary>
    public string? EncryptedApiKey { get; set; }

    /// <summary>
    /// How many times a print the printer clearly rejected is tried again. Defaults to 0 because a retry risks duplicate
    /// papers; timed-out prints are never retried whatever this is set to.
    /// </summary>
    public int PrintRetryCount { get; set; }

    public int PrintRetryDelaySeconds { get; set; } = 60;

    /// <summary>A run missed while the machine was off is caught up on startup if it is no older than this.</summary>
    public int MissedRunGraceMinutes { get; set; } = 120;
}
