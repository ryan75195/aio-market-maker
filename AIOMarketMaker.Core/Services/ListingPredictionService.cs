using System.Data;
using System.Data.Common;
using System.Globalization;
using AIOMarketMaker.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Core.Services;

public record PredictionFilters(
    decimal PriceBand = 0,
    decimal FeePercent = 0,
    bool MatchCondition = true,
    int MinComps = 0);

public record ListingPredictionResult(
    int ListingId,
    int SimilarSoldCount,
    decimal AverageSoldPrice,
    decimal PotentialProfit,
    int? EstimatedDaysToSell);

public record ComparableSoldListing(
    int RelationshipId,
    int SoldListingId,
    string? ListingId,
    string? Title,
    string? Description,
    decimal? Price,
    string? Condition,
    string? Url,
    string? Images,
    DateTime? SoldDateUtc,
    double SimilarityScore,
    string Explanation);

public record PagedPredictions(
    IEnumerable<ListingPredictionResult> Items,
    IEnumerable<int> OrderedListingIds,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);

public record PredictionAggregates(
    int Opportunities,
    decimal AggregateProfit,
    IEnumerable<TopOpportunity> TopOpportunities,
    IEnumerable<TopJobOpportunity> TopJobsByOpportunities,
    IEnumerable<ConditionProfit> AvgProfitByCondition,
    IEnumerable<DaysToSell> AvgDaysToSellByJob,
    IEnumerable<PriceVsProfit> PriceVsProfitPoints);

public record TopOpportunity(
    string ListingId, string? Title, decimal? Price, string? Currency,
    decimal? AverageSoldPrice, decimal? PotentialProfit,
    int SimilarSoldCount, string? Condition, string? Url);

public record TopJobOpportunity(int JobId, string? SearchTerm, int OpportunityCount, decimal TotalProfit);
public record ConditionProfit(string Condition, decimal AvgProfit, int Count);
public record DaysToSell(int JobId, string? SearchTerm, decimal? AvgDaysToSell);
public record PriceVsProfit(decimal Price, decimal PotentialProfit, string? Condition);

public interface IListingPredictionService
{
    Task<ListingPredictionResult?> GetPrediction(int listingId, PredictionFilters filters);
    Task<IEnumerable<ComparableSoldListing>> GetComparables(int listingId, PredictionFilters filters);
    Task<PagedPredictions> GetPredictions(
        PredictionFilters filters, IEnumerable<int>? jobIds,
        string sortBy, string sortDir, int page, int pageSize);
    Task<PredictionAggregates> GetAggregates(PredictionFilters filters);
}

public class ListingPredictionService : IListingPredictionService
{
    private readonly EtlDbContext _db;

    public ListingPredictionService(EtlDbContext db)
    {
        _db = db;
    }

    public Task<ListingPredictionResult?> GetPrediction(int listingId, PredictionFilters filters)
    {
        throw new NotImplementedException();
    }

    public Task<IEnumerable<ComparableSoldListing>> GetComparables(int listingId, PredictionFilters filters)
    {
        throw new NotImplementedException();
    }

    public Task<PagedPredictions> GetPredictions(
        PredictionFilters filters, IEnumerable<int>? jobIds,
        string sortBy, string sortDir, int page, int pageSize)
    {
        throw new NotImplementedException();
    }

    public Task<PredictionAggregates> GetAggregates(PredictionFilters filters)
    {
        throw new NotImplementedException();
    }
}
