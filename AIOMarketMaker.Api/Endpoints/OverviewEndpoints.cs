using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Api.Endpoints;

public record OverviewResponse(
    int TotalListings, int ActiveListings, int SoldListings, int EndedListings,
    int OpportunitiesCount, decimal TotalProfit,
    IEnumerable<TopOpportunityResponse> TopOpportunities,
    LastScrapeResponse? LastScrape,
    IEnumerable<CumulativeGrowthEntry> CumulativeGrowth,
    IEnumerable<ListingsByJobEntry> ListingsByJob,
    ProfitDistributionResponse ProfitDistribution,
    IEnumerable<RecentRunResponse> RecentRuns);

public record LastScrapeResponse(
    int Id, DateTime? StartedUtc, DateTime? CompletedUtc, string? Status,
    string? CurrentPhase, int TotalListingsFound, int ListingsProcessed,
    string? SearchTerm);

public record CumulativeGrowthEntry(string Date, int CumulativeCount);

public record ListingsByJobEntry(int JobId, string? SearchTerm, int Count);

public record ProfitDistributionResponse(
    int Range0To25, int Range25To50, int Range50To100, int Range100Plus);

public record TopOpportunityResponse(
    int Id, string ListingId, string? Title, decimal? Price, string? Currency,
    decimal? ShippingCost, string? Url, string? Condition, string? ListingStatus,
    string? SearchTerm, decimal? AverageSoldPrice, int SimilarSoldCount,
    decimal? PotentialProfit);

public record RecentRunResponse(
    int Id, DateTime? StartedUtc, DateTime? CompletedUtc, string? Status,
    string? CurrentPhase, int TotalListingsFound, int ListingsProcessed,
    string? SearchTerm, string? TriggerType);

public static class OverviewEndpoints
{
    public static void MapOverviewEndpoints(this WebApplication app)
    {
        app.MapGet("/api/overview", GetOverview);
    }

    private static async Task<IResult> GetOverview(
        EtlDbContext db,
        int minComps = 3,
        decimal feePercent = 13.25m,
        bool matchCondition = true)
    {
        var statusCounts = await GetStatusCounts(db);
        var (opportunitiesCount, totalProfit, topOpportunities, profitDistribution) =
            await GetOpportunityData(db, minComps, feePercent, matchCondition);
        var lastScrape = await GetLastScrape(db);
        var cumulativeGrowth = await GetCumulativeGrowth(db);
        var listingsByJob = await GetListingsByJob(db);
        var recentRuns = await GetRecentRuns(db);

        var response = new OverviewResponse(
            TotalListings: statusCounts.Total,
            ActiveListings: statusCounts.Active,
            SoldListings: statusCounts.Sold,
            EndedListings: statusCounts.Ended,
            OpportunitiesCount: opportunitiesCount,
            TotalProfit: totalProfit,
            TopOpportunities: topOpportunities,
            LastScrape: lastScrape,
            CumulativeGrowth: cumulativeGrowth,
            ListingsByJob: listingsByJob,
            ProfitDistribution: profitDistribution,
            RecentRuns: recentRuns);

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

    // -- Opportunity data (CTE-based, SQL Server only) --

    private record OpportunityData(
        int OpportunitiesCount, decimal TotalProfit,
        IEnumerable<TopOpportunityResponse> TopOpportunities,
        ProfitDistributionResponse ProfitDistribution);

    private static async Task<OpportunityData> GetOpportunityData(
        EtlDbContext db, int minComps, decimal feePercent, bool matchCondition)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        // On SQLite (tests), skip CTE queries — return empty/zero
        if (conn.GetType().Name.Contains("Sqlite"))
        {
            return new OpportunityData(
                0, 0m,
                Enumerable.Empty<TopOpportunityResponse>(),
                new ProfitDistributionResponse(0, 0, 0, 0));
        }

        var cte = BuildFilteredPredictionsCte(feePercent, minComps, matchCondition);

        // Count + total profit
        var aggregateSql = $@"
            {cte}
            SELECT COUNT(*) AS Cnt, ISNULL(SUM(fp.PotentialProfit), 0) AS TotalProfit
            FROM FilteredPredictions fp";

        int opportunitiesCount = 0;
        decimal totalProfit = 0m;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = aggregateSql;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                opportunitiesCount = reader.GetInt32(0);
                totalProfit = reader.GetDecimal(1);
            }
        }

        // Top 10 opportunities
        var topSql = $@"
            {cte}
            SELECT TOP 10
                l.Id, l.ListingId, l.Title, l.Price, l.Currency,
                l.ShippingCost, l.Url, l.[Condition], l.ListingStatus,
                sj.SearchTerm,
                fp.AverageSoldPrice, fp.SimilarSoldCount, fp.PotentialProfit
            FROM FilteredPredictions fp
            INNER JOIN Listings l ON l.Id = fp.ListingId
            LEFT JOIN ScrapeJobs sj ON sj.Id = l.ScrapeJobId
            ORDER BY fp.PotentialProfit DESC";

        var topOpportunities = await ExecuteQuery(db, topSql, reader => new TopOpportunityResponse(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetDecimal(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetDecimal(10),
            reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetDecimal(12)));

        // Profit distribution (4 buckets)
        var distSql = $@"
            {cte}
            SELECT
                SUM(CASE WHEN fp.PotentialProfit >= 0 AND fp.PotentialProfit < 25 THEN 1 ELSE 0 END) AS Range0To25,
                SUM(CASE WHEN fp.PotentialProfit >= 25 AND fp.PotentialProfit < 50 THEN 1 ELSE 0 END) AS Range25To50,
                SUM(CASE WHEN fp.PotentialProfit >= 50 AND fp.PotentialProfit < 100 THEN 1 ELSE 0 END) AS Range50To100,
                SUM(CASE WHEN fp.PotentialProfit >= 100 THEN 1 ELSE 0 END) AS Range100Plus
            FROM FilteredPredictions fp";

        var profitDistribution = new ProfitDistributionResponse(0, 0, 0, 0);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = distSql;
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                profitDistribution = new ProfitDistributionResponse(
                    reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3));
            }
        }

        return new OpportunityData(opportunitiesCount, totalProfit, topOpportunities, profitDistribution);
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
            run.Id, run.StartedUtc, run.CompletedUtc, run.Status,
            run.CurrentPhase, run.TotalListingsFound, run.ListingsProcessed,
            searchTerm);
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

        var dailyCounts = await ExecuteQuery(db, sql, reader => new DailyCount(
            reader.GetString(0),
            reader.GetInt32(1)));

        // Build running sum in C#
        var result = new List<CumulativeGrowthEntry>();
        int cumulative = 0;
        foreach (var day in dailyCounts)
        {
            cumulative += day.Count;
            result.Add(new CumulativeGrowthEntry(day.Date, cumulative));
        }

        return result;
    }

    // -- Listings by job --

    private static async Task<IEnumerable<ListingsByJobEntry>> GetListingsByJob(EtlDbContext db)
    {
        var groups = await db.Listings
            .GroupBy(l => l.ScrapeJobId)
            .Select(g => new { JobId = g.Key, Count = g.Count() })
            .ToListAsync();

        var jobIds = groups.Select(g => g.JobId).ToList();
        var jobNames = await db.ScrapeJobs
            .Where(j => jobIds.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, j => j.SearchTerm);

        return groups.Select(g => new ListingsByJobEntry(
            g.JobId,
            jobNames.GetValueOrDefault(g.JobId),
            g.Count));
    }

    // -- Recent runs (last 5) --

    private static async Task<IEnumerable<RecentRunResponse>> GetRecentRuns(EtlDbContext db)
    {
        var runs = await db.ScrapeRuns
            .OrderByDescending(r => r.StartedUtc)
            .Take(5)
            .ToListAsync();

        if (runs.Count == 0)
        {
            return Enumerable.Empty<RecentRunResponse>();
        }

        var jobIds = runs
            .Where(r => r.JobId.HasValue)
            .Select(r => r.JobId!.Value)
            .Distinct()
            .ToList();

        var jobNames = await db.ScrapeJobs
            .Where(j => jobIds.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, j => j.SearchTerm);

        return runs.Select(r => new RecentRunResponse(
            r.Id, r.StartedUtc, r.CompletedUtc, r.Status,
            r.CurrentPhase, r.TotalListingsFound, r.ListingsProcessed,
            r.JobId.HasValue ? jobNames.GetValueOrDefault(r.JobId.Value) : null,
            r.TriggerType));
    }

    // -- SQL helpers (same pattern as ListingEndpoints) --

    private static string BuildFilteredPredictionsCte(
        decimal feePercent, int minComps, bool matchCondition)
    {
        var fee = feePercent.ToString(CultureInfo.InvariantCulture);
        var mc = minComps.ToString(CultureInfo.InvariantCulture);

        var conditionFilter = matchCondition
            ? "AND active.[Condition] = sold.[Condition]"
            : "";

        var profitExpr = feePercent > 0
            ? $"AVG(sp.SoldPrice) * (1.0 - {fee} / 100.0) - active.Price - ISNULL(active.ShippingCost, 0)"
            : "AVG(sp.SoldPrice) - active.Price";

        return $@";WITH ComparableSoldNeighbors AS (
            SELECT r.ListingIdA AS ActiveListingId, r.ListingIdB AS SoldListingId
            FROM ListingRelationships r
            INNER JOIN Listings active ON active.Id = r.ListingIdA AND active.ListingStatus = 'Active'
            INNER JOIN Listings sold ON sold.Id = r.ListingIdB AND sold.ListingStatus = 'Sold'
            WHERE r.IsComparable = 1
            {conditionFilter}
            UNION ALL
            SELECT r.ListingIdB AS ActiveListingId, r.ListingIdA AS SoldListingId
            FROM ListingRelationships r
            INNER JOIN Listings active ON active.Id = r.ListingIdB AND active.ListingStatus = 'Active'
            INNER JOIN Listings sold ON sold.Id = r.ListingIdA AND sold.ListingStatus = 'Sold'
            WHERE r.IsComparable = 1
            {conditionFilter}
        ),
        SoldPrices AS (
            SELECT csn.ActiveListingId, csn.SoldListingId,
                COALESCE(
                    (SELECT TOP 1 h.Price FROM ListingStatusHistory h
                     WHERE h.ListingId = csn.SoldListingId AND h.ListingStatus = 'Sold'
                     ORDER BY h.RecordedUtc DESC),
                    sold.Price
                ) AS SoldPrice,
                CASE WHEN sold.EndDateUtc > sold.CreatedUtc
                     THEN DATEDIFF(day, sold.CreatedUtc, sold.EndDateUtc)
                END AS DaysToSell
            FROM ComparableSoldNeighbors csn
            INNER JOIN Listings sold ON sold.Id = csn.SoldListingId
        ),
        FilteredPredictions AS (
            SELECT active.Id AS ListingId,
                COUNT(*) AS SimilarSoldCount,
                AVG(sp.SoldPrice) AS AverageSoldPrice,
                {profitExpr} AS PotentialProfit,
                AVG(sp.DaysToSell) AS EstimatedDaysToSell
            FROM SoldPrices sp
            INNER JOIN Listings active ON active.Id = sp.ActiveListingId
            WHERE sp.SoldPrice > 0
            GROUP BY active.Id, active.Price, active.ShippingCost
            HAVING COUNT(*) >= {mc}
                AND {profitExpr} > 0
        )";
    }

    private static async Task<List<T>> ExecuteQuery<T>(
        EtlDbContext db, string sql, Func<DbDataReader, T> map)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();

        var results = new List<T>();
        while (await reader.ReadAsync())
        {
            results.Add(map(reader));
        }

        return results;
    }
}
