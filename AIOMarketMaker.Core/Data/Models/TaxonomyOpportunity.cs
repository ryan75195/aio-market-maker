namespace AIOMarketMaker.Core.Data.Models;

public class TaxonomyOpportunity
{
    public int Id { get; set; }
    public int ScrapeJobId { get; set; }
    public int ListingId { get; set; }
    public string CellKey { get; set; } = "";
    public decimal AskPrice { get; set; }
    public decimal MedianSoldPrice { get; set; }
    public decimal EstimatedProfit { get; set; }
    public double MarginPercent { get; set; }
    public int SoldComps { get; set; }
    public int? AvgDaysToSell { get; set; }
    public DateTime ComputedUtc { get; set; }

    public ScrapeJob ScrapeJob { get; set; } = null!;
    public Listing Listing { get; set; } = null!;
}
