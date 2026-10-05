namespace Hawker.Core.Domain;

/// <summary>An article already printed in an edition, so it is not repeated.</summary>
public class SeenArticle
{
    public int EditionId { get; set; }

    /// <summary>SHA-256 of the normalised article URL.</summary>
    public required string UrlHash { get; set; }

    public DateTimeOffset FirstSeen { get; set; }
}
