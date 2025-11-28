namespace AIOMarketMaker.Etl.Data.Models;

/// <summary>
/// Represents a scheduled eBay scraping job configuration.
/// </summary>
public class ScrapeJob
{
    public int Id { get; set; }

    /// <summary>
    /// The search term to use on eBay (e.g., "Playstation 5 Console")
    /// </summary>
    public required string SearchTerm { get; set; }

    /// <summary>
    /// Buying format filter: BUY_NOW, AUCTION, ALL
    /// </summary>
    public required string BuyingFormat { get; set; }

    /// <summary>
    /// Item condition filter: NEW, USED, FOR_PARTS_NOT_WORKING, etc.
    /// </summary>
    public required string Condition { get; set; }

    /// <summary>
    /// Whether to search SOLD or ACTIVE listings
    /// </summary>
    public required string SearchType { get; set; }

    /// <summary>
    /// How often to run this job in minutes
    /// </summary>
    public int FrequencyMinutes { get; set; }

    /// <summary>
    /// For sold items - how many days back to search
    /// </summary>
    public int? LookbackDays { get; set; }

    /// <summary>
    /// For active items - maximum number of items to fetch
    /// </summary>
    public int? ItemLimit { get; set; }

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
