namespace AIOMarketMaker.Core.Data.Models;

public class ListingRelationship
{
    public int Id { get; set; }
    public int ListingIdA { get; set; }
    public int ListingIdB { get; set; }
    public bool IsComparable { get; set; }
    public string Explanation { get; set; } = string.Empty;
    public double SimilarityScore { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public Listing ListingA { get; set; } = null!;
    public Listing ListingB { get; set; } = null!;
}
