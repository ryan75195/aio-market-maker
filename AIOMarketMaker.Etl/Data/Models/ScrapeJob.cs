namespace AIOMarketMaker.Etl.Data.Models;

/// <summary>
/// Represents an eBay scraping job configuration.
/// Jobs always scrape both active and sold listings.
/// </summary>
public class ScrapeJob
{
    public int Id { get; set; }

    /// <summary>
    /// The search term to use on eBay (e.g., "Playstation 5 Console")
    /// </summary>
    public required string SearchTerm { get; set; }

    /// <summary>
    /// Optional instructions for filtering results (e.g., "exclude bundles, accessories")
    /// </summary>
    public string? FilterInstructions { get; set; }

    /// <summary>
    /// Whether this job is enabled
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When this job was last executed
    /// </summary>
    public DateTime? LastRunUtc { get; set; }

    /// <summary>
    /// When this job was created
    /// </summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
