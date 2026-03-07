namespace AIOMarketMaker.Core.Data.Models;

public class TaxonomyRun
{
    public int Id { get; set; }
    public int ScrapeJobId { get; set; }
    public double CoveragePercent { get; set; }
    public double ConflictPercent { get; set; }
    public int TotalListings { get; set; }
    public int AssignedListings { get; set; }
    public int AxisCount { get; set; }
    public int DurationMs { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public ScrapeJob ScrapeJob { get; set; } = null!;
    public ICollection<TaxonomyAxis> Axes { get; set; } = new List<TaxonomyAxis>();
    public ICollection<TaxonomyListingAssignment> Assignments { get; set; } = new List<TaxonomyListingAssignment>();
}
