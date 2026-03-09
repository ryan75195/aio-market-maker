using System.Data;
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

public record PriceVsProfitEntry(decimal Price, decimal EstimatedProfit, string? Condition);

public record TopOpportunityResponse(
    string ListingId, string? Title, decimal? Price, string? Currency,
    decimal? MedianSoldPrice, decimal? EstimatedProfit,
    int SoldComps, string? Condition, string? Url);

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
        IOptions<PricingOptions> pricingOptions,
        int? minComps = null)
    {
        var opts = pricingOptions.Value;
        var effectiveMinComps = minComps ?? opts.MinComps;

        var statusCounts = await GetStatusCounts(db);
        var lastScrape = await GetLastScrape(db);
        var cumulativeGrowth = await GetCumulativeGrowth(db);
        var opportunitiesByDay = await GetOpportunitiesByDay(db, effectiveMinComps);

        var oppQuery = db.TaxonomyOpportunities
            .AsNoTracking()
            .AsQueryable();

        if (effectiveMinComps > 0)
        {
            oppQuery = oppQuery.Where(o => o.SoldComps >= effectiveMinComps);
        }

        var opportunities = await oppQuery.CountAsync();
        var aggregateProfit = opportunities > 0
            ? await oppQuery.SumAsync(o => (double)o.EstimatedProfit)
            : 0;

        // Materialize opportunity rows with joined data for in-memory aggregation.
        // The dataset is bounded (only profitable opportunities above min comps),
        // so materializing is safe and avoids EF Core GroupBy translation issues.
        var oppRows = await oppQuery
            .Select(o => new OpportunityRow(
                o.ScrapeJobId, o.ListingId, o.AskPrice,
                o.MedianSoldPrice, o.EstimatedProfit, o.SoldComps,
                o.Listing!.ListingId, o.Listing.Id,
                o.Listing.Title, o.Listing.Currency,
                o.Listing.Condition, o.Listing.Url,
                o.ScrapeJob!.SearchTerm))
            .ToListAsync();

        var topJobsByOpportunities = oppRows
            .GroupBy(o => new { o.ScrapeJobId, o.SearchTerm })
            .Select(g => new TopJobOpportunityEntry(
                g.Key.ScrapeJobId, g.Key.SearchTerm ?? "",
                g.Count(), g.Sum(x => x.EstimatedProfit)))
            .OrderByDescending(j => j.OpportunityCount)
            .Take(10)
            .ToList();

        var avgProfitByCondition = oppRows
            .Where(o => o.Condition != null)
            .GroupBy(o => o.Condition!)
            .Select(g => new ConditionProfitEntry(
                g.Key, g.Average(x => x.EstimatedProfit), g.Count()))
            .OrderByDescending(c => c.AvgProfit)
            .ToList();

        var priceVsProfitPoints = oppRows
            .Take(500)
            .Select(o => new PriceVsProfitEntry(
                o.AskPrice, o.EstimatedProfit, o.Condition))
            .ToList();

        var topOpportunities = oppRows
            .OrderByDescending(o => o.EstimatedProfit)
            .Take(10)
            .Select(o => new TopOpportunityResponse(
                o.ListingEbayId ?? o.ListingDbId.ToString(),
                o.Title,
                o.AskPrice,
                o.Currency ?? "GBP",
                o.MedianSoldPrice,
                o.EstimatedProfit,
                o.SoldComps,
                o.Condition,
                o.Url))
            .ToList();

        var response = new OverviewResponse(
            TotalListings: statusCounts.Total,
            ActiveListings: statusCounts.Active,
            SoldListings: statusCounts.Sold,
            EndedListings: statusCounts.Ended,
            Opportunities: opportunities,
            AggregateProfit: (decimal)aggregateProfit,
            LastScrape: lastScrape,
            CumulativeGrowth: cumulativeGrowth,
            OpportunitiesByDay: opportunitiesByDay,
            TopJobsByOpportunities: topJobsByOpportunities,
            AvgProfitByCondition: avgProfitByCondition,
            PriceVsProfitPoints: priceVsProfitPoints,
            TopOpportunities: topOpportunities);

        return Results.Ok(response);
    }

    // -- Materialized opportunity row for in-memory aggregation --

    private record OpportunityRow(
        int ScrapeJobId, int ListingId, decimal AskPrice,
        decimal MedianSoldPrice, decimal EstimatedProfit, int SoldComps,
        string? ListingEbayId, int ListingDbId, string? Title,
        string? Currency, string? Condition, string? Url, string? SearchTerm);

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
        EtlDbContext db, int minComps)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        var isSqlite = conn.GetType().Name.Contains("Sqlite");
        var dateCast = isSqlite ? "DATE(ComputedUtc)" : "CAST(ComputedUtc AS DATE)";

        var sql = $@"
            SELECT {dateCast} AS OpDate, COUNT(*) AS OpCount
            FROM TaxonomyOpportunities
            WHERE SoldComps >= {minComps}
            GROUP BY {dateCast}
            ORDER BY OpDate";

        return await DbQueryHelper.ExecuteQuery(db, sql, reader => new OpportunitiesByDayEntry(
            isSqlite ? reader.GetString(0) : reader.GetDateTime(0).ToString("yyyy-MM-dd"),
            reader.GetInt32(1)));
    }

}
