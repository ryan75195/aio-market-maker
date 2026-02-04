namespace AIOMarketMaker.Core.Data.Models;

public class ListingPrediction
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public decimal AverageSoldPrice { get; set; }
    public int SimilarSoldCount { get; set; }
    public int? EstimatedDaysToSell { get; set; }
    public decimal? PotentialProfit { get; set; }
    public DateTime ComputedUtc { get; set; } = DateTime.UtcNow;

    public Listing Listing { get; set; } = null!;
}
