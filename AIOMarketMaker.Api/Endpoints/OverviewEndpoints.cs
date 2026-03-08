using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Api.Endpoints;

public record OverviewResponse(
    int TotalListings, int ActiveListings, int SoldListings, int EndedListings,
    int Opportunities, decimal AggregateProfit,
    LastScrapeResponse? LastScrape,
    IEnumerable<CumulativeGrowthEntry> CumulativeGrowth,
    IEnumerable<OpportunitiesByDayEntry> OpportunitiesByDay,
    IEnumerable<TopJobOpportunityEntry> TopJobsByOpportunities,
    IEnumerable<ConditionProfitEntry> AvgProfitByCondition,
    IEnumerable<PriceVsProfitEntry> PriceVsProfitPoints,
    IEnumerable<TopOpportunityResponse> TopOpportunities);

public record LastScrapeResponse(
    DateTime StartedUtc, string? Status, string? JobSearchTerm,
    int ListingsAddedActive, int ListingsAddedSold);

public record CumulativeGrowthEntry(string Date, int Cumulative);

public record OpportunitiesByDayEntry(string Date, int Count);

public record TopJobOpportunityEntry(int JobId, string? SearchTerm, int OpportunityCount, decimal TotalProfit);

public record ConditionProfitEntry(string Condition, decimal AvgProfit, int Count);

public record DaysToSellEntry(int JobId, string? SearchTerm, decimal? AvgDaysToSell);

public record PriceVsProfitEntry(decimal Price, decimal PotentialProfit, string? Condition);

public record TopOpportunityResponse(
    string ListingId, string? Title, decimal? Price, string? Currency,
    decimal? AverageSoldPrice, decimal? PotentialProfit,
    int SimilarSoldCount, string? Condition, string? Url);

public record RecentRunResponse(
    int Id, DateTime? StartedUtc, string? JobSearchTerm,
    string? Status, int ListingsAddedActive, int ListingsAddedSold,
    int ListingsFailed);

public static class OverviewEndpoints
{
    public static void MapOverviewEndpoints(this WebApplication app)
    {
        app.MapGet("/api/overview", GetOverview);
    }

    private static async Task<IResult> GetOverview(
        EtlDbContext db,
        IListingPredictionService predictionService,
        IOptions<PricingOptions> pricingOptions,
        int? minComps = null,
        decimal? feePercent = null,
        bool matchCondition = true)
    {
        var opts = pricingOptions.Value;
        var filters = new PredictionFilters(
            FeePercent: feePercent ?? (decimal)opts.FeePercent,
            MatchCondition: matchCondition,
            MinComps: minComps ?? opts.MinComps);

        var statusCounts = await GetStatusCounts(db);
        var lastScrape = await GetLastScrape(db);
        var cumulativeGrowth = await GetCumulativeGrowth(db);
        var opportunitiesByDay = await GetOpportunitiesByDay(db, filters);
        var agg = await predictionService.GetAggregates(filters);

        var response = new OverviewResponse(
            TotalListings: statusCounts.Total,
            ActiveListings: statusCounts.Active,
            SoldListings: statusCounts.Sold,
            EndedListings: statusCounts.Ended,
            Opportunities: agg.Opportunities,
            AggregateProfit: agg.AggregateProfit,
            LastScrape: lastScrape,
            CumulativeGrowth: cumulativeGrowth,
            OpportunitiesByDay: opportunitiesByDay,
            TopJobsByOpportunities: agg.TopJobsByOpportunities
                .Select(j => new TopJobOpportunityEntry(j.JobId, j.SearchTerm, j.OpportunityCount, j.TotalProfit)),
            AvgProfitByCondition: agg.AvgProfitByCondition
                .Select(c => new ConditionProfitEntry(c.Condition, c.AvgProfit, c.Count)),
            PriceVsProfitPoints: agg.PriceVsProfitPoints
                .Select(p => new PriceVsProfitEntry(p.Price, p.PotentialProfit, p.Condition)),
            TopOpportunities: agg.TopOpportunities
                .Select(o => new TopOpportunityResponse(
                    o.ListingId, o.Title, o.Price, o.Currency,
                    o.AverageSoldPrice, o.PotentialProfit, o.SimilarSoldCount,
                    o.Condition, o.Url)));

        return Results.Ok(response);
    }

    // -- Status counts --

    private record StatusCountResult(int Total, int Active, int Sold, int Ended);

    private static async Task<StatusCountResult> GetStatusCounts(EtlDbContext db)
    {
        var groups = await db.Listings
            .GroupBy(l => l.ListingStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();

        var total = groups.Sum(g => g.Count);
        var active = groups.Where(g => g.Status == "Active").Sum(g => g.Count);
        var sold = groups.Where(g => g.Status == "Sold").Sum(g => g.Count);
        var ended = groups.Where(g => g.Status == "Ended" || g.Status == "OutOfStock").Sum(g => g.Count);

        return new StatusCountResult(total, active, sold, ended);
    }

    // -- Last scrape run --

    private static async Task<LastScrapeResponse?> GetLastScrape(EtlDbContext db)
    {
        var run = await db.ScrapeRuns
            .OrderByDescending(r => r.StartedUtc)
            .FirstOrDefaultAsync();

        if (run == null)
        {
            return null;
        }

        string? searchTerm = null;
        if (run.JobId.HasValue)
        {
            searchTerm = await db.ScrapeJobs
                .Where(j => j.Id == run.JobId.Value)
                .Select(j => j.SearchTerm)
                .FirstOrDefaultAsync();
        }

        return new LastScrapeResponse(
            run.StartedUtc, run.Status, searchTerm,
            run.ListingsAddedActive, run.ListingsAddedSold);
    }

    // -- Cumulative growth by date --

    private record DailyCount(string Date, int Count);

    private static async Task<IEnumerable<CumulativeGrowthEntry>> GetCumulativeGrowth(EtlDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        var isSqlite = conn.GetType().Name.Contains("Sqlite");
        var dateCast = isSqlite ? "DATE(CreatedUtc)" : "CAST(CreatedUtc AS DATE)";

        var sql = $@"
            SELECT {dateCast} AS ListingDate, COUNT(*) AS DailyCount
            FROM Listings
            GROUP BY {dateCast}
            ORDER BY ListingDate";

        var dailyCounts = await DbQueryHelper.ExecuteQuery(db, sql, reader => new DailyCount(
            isSqlite ? reader.GetString(0) : reader.GetDateTime(0).ToString("yyyy-MM-dd"),
            reader.GetInt32(1)));

        // Build running sum in C#
        var result = new List<CumulativeGrowthEntry>();
        int cumulative = 0;
        foreach (var day in dailyCounts)
        {
            cumulative += day.Count;
            result.Add(new CumulativeGrowthEntry(day.Date, Cumulative: cumulative));
        }

        return result;
    }

    // -- Opportunities by day --

    private static async Task<IEnumerable<OpportunitiesByDayEntry>> GetOpportunitiesByDay(
        EtlDbContext db, PredictionFilters filters)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        var isSqlite = conn.GetType().Name.Contains("Sqlite");
        var dateCast = isSqlite ? "DATE(l.CreatedUtc)" : "CAST(l.CreatedUtc AS DATE)";
        var minComps = filters.MinComps > 0 ? filters.MinComps : 1;

        var sql = $@"
            SELECT {dateCast} AS OpDate, COUNT(*) AS OpCount
            FROM Listings l
            WHERE l.ListingStatus = 'Active'
              AND (SELECT COUNT(*) FROM ListingRelationships lr WHERE lr.ListingIdA = l.Id) >= {minComps}
            GROUP BY {dateCast}
            ORDER BY OpDate";

        return await DbQueryHelper.ExecuteQuery(db, sql, reader => new OpportunitiesByDayEntry(
            isSqlite ? reader.GetString(0) : reader.GetDateTime(0).ToString("yyyy-MM-dd"),
            reader.GetInt32(1)));
    }

}
