using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Api.Endpoints;

public record JobMarketStats(
    int JobId,
    string? SearchTerm,
    IEnumerable<string> Categories,
    int ActiveCount,
    int SoldCount,
    decimal SalesPerDay,
    int SellThrough,
    int? AvgDaysToSell,
    decimal? AvgAskPrice,
    decimal? MedianSoldPrice,
    decimal? P25SoldPrice,
    decimal? P75SoldPrice);

public record MarketsResponse(
    IEnumerable<JobMarketStats> Jobs,
    IEnumerable<string> AllCategories);

public static class MarketsCalc
{
    public static int SellThrough(int active, int sold)
    {
        var total = active + sold;
        if (total == 0)
        {
            return 0;
        }

        return (int)Math.Round((double)sold / total * 100);
    }

    public static decimal SalesPerDay(int sold, int lookbackDays)
    {
        if (lookbackDays <= 0)
        {
            return 0m;
        }

        return Math.Round((decimal)sold / lookbackDays, 1);
    }
}

record JobStatsRow(
    int JobId,
    string? SearchTerm,
    int ActiveCount,
    int SoldCount,
    decimal? AvgAskPrice,
    int? AvgDaysToSell);

record PercentileRow(
    int JobId,
    decimal? P25,
    decimal? Median,
    decimal? P75);

public static class MarketsEndpoints
{
    public static void MapMarketsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/markets", GetMarkets);
        app.MapGet("/api/markets/{jobId:int}/listings", GetJobListings);
    }

    private static async Task<IResult> GetMarkets(
        EtlDbContext db,
        int lookbackDays = 180)
    {
        var sql = @"
            SELECT
                l.ScrapeJobId,
                sj.SearchTerm,
                COUNT(CASE WHEN l.ListingStatus = 'Active' THEN 1 END) AS ActiveCount,
                COUNT(CASE WHEN l.ListingStatus IN ('Sold', 'Ended') THEN 1 END) AS SoldCount,
                AVG(CASE WHEN l.ListingStatus = 'Active' AND l.Price > 0 THEN l.Price END) AS AvgAskPrice,
                AVG(CASE WHEN l.ListingStatus IN ('Sold', 'Ended') AND l.EndDateUtc IS NOT NULL
                    THEN DATEDIFF(DAY, l.CreatedUtc, l.EndDateUtc) END) AS AvgDaysToSell
            FROM Listings l
            INNER JOIN ScrapeJobs sj ON sj.Id = l.ScrapeJobId
            WHERE sj.IsEnabled = 1
            GROUP BY l.ScrapeJobId, sj.SearchTerm
            HAVING COUNT(*) > 0
            ORDER BY COUNT(CASE WHEN l.ListingStatus IN ('Sold', 'Ended') THEN 1 END) DESC";

        var jobStats = await DbQueryHelper.ExecuteQuery(db, sql, reader => new JobStatsRow(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : DbQueryHelper.SafeGetDecimal(reader, 4),
            reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5)
        ));

        // Percentiles need a separate query (PERCENTILE_CONT requires WITHIN GROUP)
        var percentileSql = @"
            SELECT
                l.ScrapeJobId,
                PERCENTILE_CONT(0.25) WITHIN GROUP (ORDER BY l.Price) OVER (PARTITION BY l.ScrapeJobId) AS P25,
                PERCENTILE_CONT(0.50) WITHIN GROUP (ORDER BY l.Price) OVER (PARTITION BY l.ScrapeJobId) AS Median,
                PERCENTILE_CONT(0.75) WITHIN GROUP (ORDER BY l.Price) OVER (PARTITION BY l.ScrapeJobId) AS P75
            FROM Listings l
            INNER JOIN ScrapeJobs sj ON sj.Id = l.ScrapeJobId
            WHERE l.ListingStatus IN ('Sold', 'Ended')
              AND l.Price > 0
              AND sj.IsEnabled = 1";

        var percentileRows = await DbQueryHelper.ExecuteQuery(db, percentileSql, reader => new PercentileRow(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : DbQueryHelper.SafeGetDecimal(reader, 1),
            reader.IsDBNull(2) ? null : DbQueryHelper.SafeGetDecimal(reader, 2),
            reader.IsDBNull(3) ? null : DbQueryHelper.SafeGetDecimal(reader, 3)
        ));

        // Deduplicate percentile rows (window function returns one per row, not per group)
        var percentileByJob = percentileRows
            .GroupBy(r => r.JobId)
            .ToDictionary(g => g.Key, g => g.First());

        // Load job-category mappings
        var jobCategories = await db.JobCategories
            .Include(jc => jc.Category)
            .Where(jc => jc.Category != null)
            .ToListAsync();

        var catsByJob = jobCategories
            .GroupBy(jc => jc.JobId)
            .ToDictionary(g => g.Key, g => g.Select(jc => jc.Category!.Name).ToList());

        var allCategories = jobCategories
            .Select(jc => jc.Category!.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        var jobs = jobStats.Select(j =>
        {
            percentileByJob.TryGetValue(j.JobId, out var pct);
            catsByJob.TryGetValue(j.JobId, out var cats);

            return new JobMarketStats(
                j.JobId,
                j.SearchTerm,
                cats ?? new List<string>(),
                j.ActiveCount,
                j.SoldCount,
                MarketsCalc.SalesPerDay(j.SoldCount, lookbackDays),
                MarketsCalc.SellThrough(j.ActiveCount, j.SoldCount),
                j.AvgDaysToSell,
                j.AvgAskPrice.HasValue ? Math.Round(j.AvgAskPrice.Value, 2) : null,
                pct?.Median.HasValue == true ? Math.Round(pct.Median.Value, 2) : null,
                pct?.P25.HasValue == true ? Math.Round(pct.P25.Value, 2) : null,
                pct?.P75.HasValue == true ? Math.Round(pct.P75.Value, 2) : null);
        });

        return Results.Ok(new MarketsResponse(jobs, allCategories));
    }

    // Task 3 implements this method — leave as stub for now
    private static async Task<IResult> GetJobListings(
        int jobId,
        EtlDbContext db,
        string? status = null,
        string? search = null,
        string sortBy = "daysOnMarket",
        string sortDir = "asc",
        int page = 1,
        int pageSize = 50)
    {
        await Task.CompletedTask;
        return Results.Ok(new { message = "Not implemented yet" });
    }
}
