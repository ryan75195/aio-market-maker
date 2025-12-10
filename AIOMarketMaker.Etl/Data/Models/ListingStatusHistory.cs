namespace AIOMarketMaker.Etl.Data.Models;

/// <summary>
/// Tracks the history of listing status changes.
/// Each time a status change is detected, a new record is added.
/// </summary>
public class ListingStatusHistory
{
    public int Id { get; set; }

    /// <summary>
    /// Foreign key to the Listing this history record belongs to
    /// </summary>
    public int ListingId { get; set; }

    /// <summary>
    /// The listing status at this point in time (Active, Sold, Ended)
    /// </summary>
    public required string ListingStatus { get; set; }

    /// <summary>
    /// The price at the time of this status check
    /// </summary>
    public decimal? Price { get; set; }

    /// <summary>
    /// eBay's actual sale/end date (parsed from listing page)
    /// </summary>
    public DateTime? SoldDateUtc { get; set; }

    /// <summary>
    /// When we observed/recorded this status
    /// </summary>
    public DateTime RecordedUtc { get; set; }

    /// <summary>
    /// Source of this record: "InitialScrape" or "StatusRefresh"
    /// </summary>
    public string? Source { get; set; }

    // Navigation property
    public Listing Listing { get; set; } = null!;
}
