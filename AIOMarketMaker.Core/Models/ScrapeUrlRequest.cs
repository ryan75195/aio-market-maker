namespace AIOMarketMaker.Core.Models;

/// <summary>
/// Request to scrape a single URL with optional grouping metadata.
/// </summary>
public record ScrapeUrlRequest
{
    /// <summary>
    /// The URL to scrape.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Optional grouping identifier (e.g., listing ID).
    /// When provided with FileKey, determines blob path structure.
    /// </summary>
    public string? GroupId { get; init; }

    /// <summary>
    /// Optional file key (e.g., "listing" or "description").
    /// When provided with GroupId, determines blob file name.
    /// </summary>
    public string? FileKey { get; init; }
}
