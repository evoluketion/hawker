namespace Hawker.Core.Domain;

/// <summary>Single-row table of settings editable from the UI.</summary>
public class AppSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public string ClaudeModel { get; set; } = "claude-sonnet-5-5";

    /// <summary>Encrypted with ASP.NET Data Protection. The ANTHROPIC_API_KEY environment variable takes precedence.</summary>
    public string? EncryptedApiKey { get; set; }

    /// <summary>A run missed while the machine was off is caught up on startup if it is no older than this.</summary>
    public int MissedRunGraceMinutes { get; set; } = 120;
}
