namespace AIOMarketMaker.Core.Data.Models;

public class ListingPricingComparable
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public int ComparableListingId { get; set; }
    public double SimilarityScore { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public Listing Listing { get; set; } = null!;
    public Listing ComparableListing { get; set; } = null!;
}
