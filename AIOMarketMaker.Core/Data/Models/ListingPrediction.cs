namespace AIOMarketMaker.Core.Data.Models;

/// <summary>
/// Read-only model backed by vw_ListingPredictions view.
/// Computed live from ListingRelationships + ListingStatusHistory.
/// </summary>
public class ListingPrediction
{
    public int ListingId { get; set; }
    public decimal AverageSoldPrice { get; set; }
    public int SimilarSoldCount { get; set; }
    public int? EstimatedDaysToSell { get; set; }
    public decimal? PotentialProfit { get; set; }
    public DateTime ComputedUtc { get; set; }
}
