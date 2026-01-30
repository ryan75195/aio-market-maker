namespace AIOMarketMaker.Core.Data.Models;

/// <summary>
/// Tracks issues encountered during scrape run processing.
/// Provides visibility into parsing failures, bot detection, and other problems.
/// </summary>
public class ScrapeRunIssue
{
    /// <summary>
    /// Primary key
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The scrape run this issue occurred in
    /// </summary>
    public int ScrapeRunId { get; set; }

    /// <summary>
    /// The eBay listing ID that had the issue (if applicable)
    /// </summary>
    public string ListingId { get; set; } = string.Empty;

    /// <summary>
    /// Type of issue: ParseFailure, BotDetection, MissingData, etc.
    /// </summary>
    public string IssueType { get; set; } = string.Empty;

    /// <summary>
    /// Detailed error message or description of the issue
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// When this issue was recorded
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    // Navigation properties

    /// <summary>
    /// The parent scrape run
    /// </summary>
    public ScrapeRun? ScrapeRun { get; set; }
}
