namespace AIOMarketMaker.Core.Data.Models;

public class ListingPrediction
{
    public int ListingId { get; set; }
    public int SimilarSoldCount { get; set; }
    public decimal AverageSoldPrice { get; set; }
    public decimal? MedianSoldPrice { get; set; }
    public decimal PotentialProfit { get; set; }
    public int? EstimatedDaysToSell { get; set; }
    public double Confidence { get; set; }
    public int OutliersRemoved { get; set; }
    public DateTime ComputedUtc { get; set; }

    public Listing Listing { get; set; } = null!;
}
