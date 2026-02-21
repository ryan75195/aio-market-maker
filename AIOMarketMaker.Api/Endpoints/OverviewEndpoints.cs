using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Api.Endpoints;

public record OverviewResponse(
    int TotalListings, int ActiveListings, int SoldListings, int EndedListings,
    int Opportunities, decimal AggregateProfit,
    LastScrapeResponse? LastScrape,
    IEnumerable<CumulativeGrowthEntry> CumulativeGrowth,
    IEnumerable<TopJobOpportunityEntry> TopJobsByOpportunities,
    ProfitDistributionResponse ProfitDistribution,
    IEnumerable<TopOpportunityResponse> TopOpportunities,
    IEnumerable<RecentRunResponse> RecentRuns);

public record LastScrapeResponse(
    DateTime StartedUtc, string? Status, string? JobSearchTerm,
    int ListingsAddedActive, int ListingsAddedSold);

public record CumulativeGrowthEntry(string Date, int Cumulative);

public record TopJobOpportunityEntry(int JobId, string? SearchTerm, int OpportunityCount, decimal TotalProfit);

public record ProfitDistributionResponse(
    int Range0to25, int Range25to50, int Range50to100, int Range100plus);

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
        int minComps = 3,
        decimal feePercent = 13.25m,
        bool matchCondition = true)
    {
        var statusCounts = await GetStatusCounts(db);
        var oppData = await GetOpportunityData(db, minComps, feePercent, matchCondition);
        var lastScrape = await GetLastScrape(db);
        var cumulativeGrowth = await GetCumulativeGrowth(db);
        var recentRuns = await GetRecentRuns(db);

        var response = new OverviewResponse(
            TotalListings: statusCounts.Total,
            ActiveListings: statusCounts.Active,
            SoldListings: statusCounts.Sold,
            EndedListings: statusCounts.Ended,
            Opportunities: oppData.Opportunities,
            AggregateProfit: oppData.AggregateProfit,
            LastScrape: lastScrape,
            CumulativeGrowth: cumulativeGrowth,
            TopJobsByOpportunities: oppData.TopJobsByOpportunities,
            ProfitDistribution: oppData.ProfitDistribution,
            TopOpportunities: oppData.TopOpportunities,
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
        int Opportunities, decimal AggregateProfit,
        IEnumerable<TopOpportunityResponse> TopOpportunities,
        ProfitDistributionResponse ProfitDistribution,
        IEnumerable<TopJobOpportunityEntry> TopJobsByOpportunities);

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
                new ProfitDistributionResponse(0, 0, 0, 0),
                Enumerable.Empty<TopJobOpportunityEntry>());
        }

        var cte = BuildFilteredPredictionsCte(feePercent, minComps, matchCondition);

        // Materialize the CTE once into a temp table, then run cheap queries against it.
        // The CTE scans 738K+ relationships — running it once instead of 3× saves ~6s.
        var materializeSql = $@"
            {cte}
            SELECT fp.ListingId, fp.SimilarSoldCount, fp.AverageSoldPrice, fp.PotentialProfit
            INTO #Predictions
            FROM FilteredPredictions fp";

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = materializeSql;
            await cmd.ExecuteNonQueryAsync();
        }

        // Aggregate + distribution in a single scan of the temp table
        int opportunities = 0;
        decimal aggregateProfit = 0m;
        var profitDistribution = new ProfitDistributionResponse(0, 0, 0, 0);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT
                    COUNT(*),
                    ISNULL(SUM(PotentialProfit), 0),
                    SUM(CASE WHEN PotentialProfit >= 0 AND PotentialProfit < 25 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN PotentialProfit >= 25 AND PotentialProfit < 50 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN PotentialProfit >= 50 AND PotentialProfit < 100 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN PotentialProfit >= 100 THEN 1 ELSE 0 END)
                FROM #Predictions";
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                opportunities = reader.GetInt32(0);
                aggregateProfit = reader.GetDecimal(1);
                profitDistribution = new ProfitDistributionResponse(
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    reader.IsDBNull(5) ? 0 : reader.GetInt32(5));
            }
        }

        // Top 10 opportunities (joins back to Listings for display fields)
        var topOpportunities = new List<TopOpportunityResponse>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT TOP 10
                    l.ListingId, l.Title, l.Price, l.Currency,
                    p.AverageSoldPrice, p.PotentialProfit, p.SimilarSoldCount,
                    l.[Condition], l.Url
                FROM #Predictions p
                INNER JOIN Listings l ON l.Id = p.ListingId
                ORDER BY p.PotentialProfit DESC";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                topOpportunities.Add(new TopOpportunityResponse(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                    reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                    reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        // Top 10 jobs by opportunity count (free — reads from temp table)
        var topJobs = new List<TopJobOpportunityEntry>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT TOP 10
                    l.ScrapeJobId, sj.SearchTerm,
                    COUNT(*) AS OpportunityCount,
                    SUM(p.PotentialProfit) AS TotalProfit
                FROM #Predictions p
                INNER JOIN Listings l ON l.Id = p.ListingId
                LEFT JOIN ScrapeJobs sj ON sj.Id = l.ScrapeJobId
                GROUP BY l.ScrapeJobId, sj.SearchTerm
                ORDER BY COUNT(*) DESC";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                topJobs.Add(new TopJobOpportunityEntry(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? 0m : reader.GetDecimal(3)));
            }
        }

        // Clean up temp table
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DROP TABLE #Predictions";
            await cmd.ExecuteNonQueryAsync();
        }

        return new OpportunityData(opportunities, aggregateProfit, topOpportunities, profitDistribution, topJobs);
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

        var dailyCounts = await ExecuteQuery(db, sql, reader => new DailyCount(
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
            r.Id, r.StartedUtc,
            r.JobId.HasValue ? jobNames.GetValueOrDefault(r.JobId.Value) : null,
            r.Status, r.ListingsAddedActive, r.ListingsAddedSold,
            r.ListingsFailed));
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
            ? $"AVG(sold.Price) * (1.0 - {fee} / 100.0) - active.Price - ISNULL(active.ShippingCost, 0)"
            : "AVG(sold.Price) - active.Price";

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
        FilteredPredictions AS (
            SELECT active.Id AS ListingId,
                COUNT(*) AS SimilarSoldCount,
                AVG(sold.Price) AS AverageSoldPrice,
                {profitExpr} AS PotentialProfit,
                AVG(CASE WHEN sold.EndDateUtc > sold.CreatedUtc
                         THEN DATEDIFF(day, sold.CreatedUtc, sold.EndDateUtc)
                    END) AS EstimatedDaysToSell
            FROM ComparableSoldNeighbors csn
            INNER JOIN Listings sold ON sold.Id = csn.SoldListingId
            INNER JOIN Listings active ON active.Id = csn.ActiveListingId
            WHERE sold.Price > 0
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
