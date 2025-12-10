namespace AIOMarketMaker.Etl.Data.Models;

/// <summary>
/// Raw eBay listing data as scraped from the website.
/// </summary>
public class Listing
{
    public int Id { get; set; }

    /// <summary>
    /// eBay listing ID (e.g., "123456789012")
    /// </summary>
    public required string ListingId { get; set; }

    /// <summary>
    /// Foreign key to the ScrapeJob that found this listing
    /// </summary>
    public int ScrapeJobId { get; set; }

    public string? Title { get; set; }
    public decimal? Price { get; set; }
    public string? Currency { get; set; }
    public decimal? ShippingCost { get; set; }
    public string? Url { get; set; }
    public string? Condition { get; set; }
    public string? ListingStatus { get; set; }
    public string? PurchaseFormat { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// JSON serialized item specifics
    /// </summary>
    public string? ItemSpecifics { get; set; }

    /// <summary>
    /// JSON serialized array of image URLs
    /// </summary>
    public string? Images { get; set; }

    public string? Location { get; set; }
    public DateTime? EndDateUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedUtc { get; set; }

    // Navigation properties
    public ScrapeJob? ScrapeJob { get; set; }
    public ICollection<ListingStatusHistory> StatusHistory { get; set; } = new List<ListingStatusHistory>();
}
