namespace AIOMarketMaker.Core.Data.Models;

public class TaxonomyListingAssignment
{
    public int Id { get; set; }
    public int TaxonomyRunId { get; set; }
    public int ListingId { get; set; }
    public required string CellJson { get; set; }
    public bool HasConflict { get; set; }

    public TaxonomyRun TaxonomyRun { get; set; } = null!;
    public Listing Listing { get; set; } = null!;
}
