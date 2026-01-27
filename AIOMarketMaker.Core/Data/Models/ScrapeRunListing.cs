namespace AIOMarketMaker.Core.Data.Models;

/// <summary>
/// Junction table linking ScrapeRuns to Listings.
/// Tracks which listings were discovered and processed during each scrape run.
/// </summary>
public class ScrapeRunListing
{
    /// <summary>
    /// The scrape run this listing was discovered in
    /// </summary>
    public int ScrapeRunId { get; set; }

    /// <summary>
    /// The scrape job configuration that found this listing
    /// </summary>
    public int ScrapeJobId { get; set; }

    /// <summary>
    /// The eBay listing ID
    /// </summary>
    public required string ListingId { get; set; }

    /// <summary>
    /// Processing status: Pending, Processing, Completed, Failed
    /// </summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When processing completed (null if still pending or failed)
    /// </summary>
    public DateTime? CompletedUtc { get; set; }

    // Navigation properties

    /// <summary>
    /// The parent scrape run
    /// </summary>
    public ScrapeRun? ScrapeRun { get; set; }

    /// <summary>
    /// The parent scrape job
    /// </summary>
    public ScrapeJob? ScrapeJob { get; set; }
}
