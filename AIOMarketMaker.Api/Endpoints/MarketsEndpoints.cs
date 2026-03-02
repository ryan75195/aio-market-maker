using System.Text.RegularExpressions;
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

public record JobListingEntry(
    int Id,
    string? ListingId,
    string? Title,
    decimal? Price,
    string? Currency,
    string? ListingStatus,
    string? Condition,
    int DaysOnMarket,
    DateTime? EndDateUtc,
    DateTime CreatedUtc,
    string? Url);

public record ListingsAggregateStats(
    int ActiveCount,
    int SoldCount,
    int SellThrough,
    int AvgDaysToSell,
    decimal AvgPrice,
    decimal MinPrice,
    decimal MaxPrice);

public record JobListingsResponse(
    IEnumerable<JobListingEntry> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages,
    ListingsAggregateStats Stats);

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

    public static int DaysOnMarket(DateTime createdUtc, DateTime? endDate)
    {
        var end = endDate ?? DateTime.UtcNow;
        return Math.Max(0, (int)(end.Date - createdUtc.Date).TotalDays);
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

    private static readonly HashSet<string> AllowedListingSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "price", "listingStatus", "condition", "daysOnMarket", "createdUtc"
    };

    private static async Task<IResult> GetJobListings(
        int jobId,
        EtlDbContext db,
        string? status = null,
        string? search = null,
        string? condition = null,
        decimal? minPrice = null,
        decimal? maxPrice = null,
        int? minDays = null,
        int? maxDays = null,
        string? regex = null,
        string sortBy = "daysOnMarket",
        string sortDir = "asc",
        int page = 1,
        int pageSize = 50)
    {
        if (!AllowedListingSorts.Contains(sortBy))
        {
            sortBy = "daysOnMarket";
        }

        var descending = sortDir.Equals("desc", StringComparison.OrdinalIgnoreCase);
        var direction = descending ? "DESC" : "ASC";
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (page - 1) * pageSize;

        // Build WHERE conditions
        var conditions = new List<string> { $"l.ScrapeJobId = {jobId}" };

        if (!string.IsNullOrWhiteSpace(status))
        {
            var validStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Active", "Sold", "Ended", "OutOfStock" };
            if (validStatuses.Contains(status))
            {
                conditions.Add($"l.ListingStatus = '{status}'");
            }
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var escaped = search.Replace("'", "''");
            conditions.Add($"l.Title LIKE '%{escaped}%'");
        }

        if (!string.IsNullOrWhiteSpace(condition))
        {
            var escaped = condition.Replace("'", "''");
            conditions.Add($"l.Condition LIKE '%{escaped}%'");
        }

        if (minPrice.HasValue)
        {
            conditions.Add($"l.Price >= {minPrice.Value}");
        }

        if (maxPrice.HasValue)
        {
            conditions.Add($"l.Price <= {maxPrice.Value}");
        }

        if (minDays.HasValue)
        {
            conditions.Add($"DATEDIFF(DAY, l.CreatedUtc, ISNULL(l.EndDateUtc, GETUTCDATE())) >= {minDays.Value}");
        }

        if (maxDays.HasValue)
        {
            conditions.Add($"DATEDIFF(DAY, l.CreatedUtc, ISNULL(l.EndDateUtc, GETUTCDATE())) <= {maxDays.Value}");
        }

        var where = string.Join(" AND ", conditions);

        var sortColumn = sortBy switch
        {
            "daysOnMarket" => "DATEDIFF(DAY, l.CreatedUtc, ISNULL(l.EndDateUtc, GETUTCDATE()))",
            "title" => "l.Title",
            "price" => "l.Price",
            "listingStatus" => "l.ListingStatus",
            "condition" => "l.Condition",
            "createdUtc" => "l.CreatedUtc",
            _ => "DATEDIFF(DAY, l.CreatedUtc, ISNULL(l.EndDateUtc, GETUTCDATE()))"
        };

        // When regex is provided, fetch all rows and filter/paginate in memory
        var useRegex = !string.IsNullOrWhiteSpace(regex);
        Regex? rx = null;
        if (useRegex)
        {
            try
            {
                rx = new Regex(regex!, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            }
            catch (RegexParseException)
            {
                return Results.BadRequest(new { error = "Invalid regex pattern" });
            }
        }

        var fetchSql = $@"
            SELECT l.Id, l.ListingId, l.Title, l.Price, l.Currency,
                   l.ListingStatus, l.Condition, l.EndDateUtc, l.CreatedUtc, l.Url
            FROM Listings l
            WHERE {where}
            ORDER BY {sortColumn} {direction}";

        if (!useRegex)
        {
            fetchSql += $"\n            OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";
        }

        var allListings = await DbQueryHelper.ExecuteQuery(db, fetchSql, ReadListingEntry);

        List<JobListingEntry> listings;
        int totalCount;

        if (useRegex)
        {
            var filtered = allListings.Where(l => l.Title != null && rx!.IsMatch(l.Title)).ToList();
            totalCount = filtered.Count;
            listings = filtered.Skip(offset).Take(pageSize).ToList();
        }
        else
        {
            listings = allListings;
            var countSql = $"SELECT COUNT(*) FROM Listings l WHERE {where}";
            var countResult = await DbQueryHelper.ExecuteScalar(db, countSql);
            totalCount = Convert.ToInt32(countResult ?? 0);
        }

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        // Aggregate stats over the full filtered set
        ListingsAggregateStats stats;
        if (useRegex)
        {
            // Compute stats from the in-memory regex-filtered set (all pages, not just current)
            var regexFiltered = allListings.Where(l => l.Title != null && rx!.IsMatch(l.Title)).ToList();
            var activeCount = regexFiltered.Count(l => l.ListingStatus == "Active");
            var soldCount = regexFiltered.Count(l => l.ListingStatus is "Sold" or "Ended");
            var withPrice = regexFiltered.Where(l => l.Price is > 0).ToList();
            var soldWithDays = regexFiltered
                .Where(l => l.ListingStatus is "Sold" or "Ended" && l.EndDateUtc.HasValue)
                .Select(l => l.DaysOnMarket)
                .ToList();

            stats = new ListingsAggregateStats(
                activeCount,
                soldCount,
                MarketsCalc.SellThrough(activeCount, soldCount),
                soldWithDays.Count > 0 ? (int)soldWithDays.Average() : 0,
                withPrice.Count > 0 ? Math.Round(withPrice.Average(l => l.Price!.Value), 2) : 0m,
                withPrice.Count > 0 ? withPrice.Min(l => l.Price!.Value) : 0m,
                withPrice.Count > 0 ? withPrice.Max(l => l.Price!.Value) : 0m);
        }
        else
        {
            var statsSql = $@"
                SELECT
                    COUNT(CASE WHEN l.ListingStatus = 'Active' THEN 1 END),
                    COUNT(CASE WHEN l.ListingStatus IN ('Sold', 'Ended') THEN 1 END),
                    AVG(CASE WHEN l.ListingStatus IN ('Sold', 'Ended') AND l.EndDateUtc IS NOT NULL
                        THEN DATEDIFF(DAY, l.CreatedUtc, l.EndDateUtc) END),
                    AVG(CASE WHEN l.Price > 0 THEN l.Price END),
                    MIN(CASE WHEN l.Price > 0 THEN l.Price END),
                    MAX(CASE WHEN l.Price > 0 THEN l.Price END)
                FROM Listings l
                WHERE {where}";

            var statsRows = await DbQueryHelper.ExecuteQuery(db, statsSql, reader =>
            {
                var ac = reader.GetInt32(0);
                var sc = reader.GetInt32(1);
                return new ListingsAggregateStats(
                    ac,
                    sc,
                    MarketsCalc.SellThrough(ac, sc),
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    reader.IsDBNull(3) ? 0m : DbQueryHelper.SafeGetDecimal(reader, 3),
                    reader.IsDBNull(4) ? 0m : DbQueryHelper.SafeGetDecimal(reader, 4),
                    reader.IsDBNull(5) ? 0m : DbQueryHelper.SafeGetDecimal(reader, 5));
            });

            stats = statsRows.FirstOrDefault()
                ?? new ListingsAggregateStats(0, 0, 0, 0, 0m, 0m, 0m);
        }

        return Results.Ok(new JobListingsResponse(listings, totalCount, page, pageSize, totalPages, stats));
    }

    private static JobListingEntry ReadListingEntry(System.Data.Common.DbDataReader reader)
    {
        var createdUtc = reader.GetDateTime(8);
        var endDateUtc = reader.IsDBNull(7) ? (DateTime?)null : reader.GetDateTime(7);

        return new JobListingEntry(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : DbQueryHelper.SafeGetDecimal(reader, 3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            MarketsCalc.DaysOnMarket(createdUtc, endDateUtc),
            endDateUtc,
            createdUtc,
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }
}
