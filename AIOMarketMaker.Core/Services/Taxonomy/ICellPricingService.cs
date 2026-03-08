namespace AIOMarketMaker.Core.Services.Taxonomy;

public record PricedListing(int ListingId, string Title, decimal Price, bool IsSold, int ListingIndex, string? Condition = null);

public record CellPricingResult(
    IEnumerable<CellPricing> Cells,
    IEnumerable<ArbitrageOpportunity> Opportunities,
    int TotalListings,
    int PricedListings,
    int CoveredListings);

public record CellPricing(
    string CellKey,
    IReadOnlyDictionary<string, string> Cell,
    int ActiveCount,
    int SoldCount,
    decimal? MedianActivePrice,
    decimal? MedianSoldPrice,
    decimal? Spread);

public record ArbitrageOpportunity(
    int ListingId,
    string Title,
    decimal AskPrice,
    decimal MedianSoldPrice,
    decimal EstimatedProfit,
    double MarginPercent,
    int SoldComps,
    string CellKey);

public interface ICellPricingService
{
    CellPricingResult Compute(
        TaxonomyResult taxonomy,
        IEnumerable<PricedListing> listings,
        double feePercent,
        int minComps);
}
