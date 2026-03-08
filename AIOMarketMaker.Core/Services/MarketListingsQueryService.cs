using System.Text.RegularExpressions;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Core.Services;

public record ListingsQueryParams(
    int JobId,
    string? Status = null,
    string? Search = null,
    string? Condition = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null,
    int? MinDays = null,
    int? MaxDays = null,
    string? Regex = null,
    string SortBy = "daysOnMarket",
    string SortDir = "asc",
    int Page = 1,
    int PageSize = 50,
    Dictionary<string, string>? AxisFilters = null);

public record ListingsQueryResult(
    IEnumerable<JobListingEntry> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages,
    ListingsAggregateStats Stats);

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

public interface IMarketListingsQueryService
{
    Task<ListingsQueryResult> Query(ListingsQueryParams p);
}

public class MarketListingsQueryService : IMarketListingsQueryService
{
    private static readonly HashSet<string> AllowedSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "price", "listingStatus", "condition", "daysOnMarket", "createdUtc"
    };

    private readonly EtlDbContext _db;

    public MarketListingsQueryService(EtlDbContext db)
    {
        _db = db;
    }

    public async Task<ListingsQueryResult> Query(ListingsQueryParams p)
    {
        var sortBy = AllowedSorts.Contains(p.SortBy) ? p.SortBy : "daysOnMarket";
        var descending = p.SortDir.Equals("desc", StringComparison.OrdinalIgnoreCase);
        var direction = descending ? "DESC" : "ASC";
        var page = Math.Max(1, p.Page);
        var pageSize = Math.Clamp(p.PageSize, 1, 200);
        var offset = (page - 1) * pageSize;

        var conditions = new List<string> { $"l.ScrapeJobId = {p.JobId}" };

        if (!string.IsNullOrWhiteSpace(p.Status))
        {
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "Active", "Sold", "Ended", "OutOfStock" };
            if (valid.Contains(p.Status))
            {
                conditions.Add($"l.ListingStatus = '{p.Status}'");
            }
        }

        if (!string.IsNullOrWhiteSpace(p.Search))
        {
            var escaped = p.Search.Replace("'", "''");
            conditions.Add($"l.Title LIKE '%{escaped}%'");
        }

        if (!string.IsNullOrWhiteSpace(p.Condition))
        {
            var escaped = p.Condition.Replace("'", "''");
            conditions.Add($"UPPER(l.Condition) = UPPER('{escaped}')");
        }

        if (p.MinPrice.HasValue)
        {
            conditions.Add($"l.Price >= {p.MinPrice.Value}");
        }

        if (p.MaxPrice.HasValue)
        {
            conditions.Add($"l.Price <= {p.MaxPrice.Value}");
        }

        if (p.MinDays.HasValue)
        {
            conditions.Add($"DATEDIFF(DAY, l.CreatedUtc, ISNULL(l.EndDateUtc, GETUTCDATE())) >= {p.MinDays.Value}");
        }

        if (p.MaxDays.HasValue)
        {
            conditions.Add($"DATEDIFF(DAY, l.CreatedUtc, ISNULL(l.EndDateUtc, GETUTCDATE())) <= {p.MaxDays.Value}");
        }

        if (p.AxisFilters?.Count > 0)
        {
            var runIdQuery = await _db.TaxonomyRuns
                .Where(r => r.ScrapeJobId == p.JobId)
                .OrderByDescending(r => r.CreatedUtc)
                .Select(r => r.Id)
                .FirstOrDefaultAsync();

            if (runIdQuery > 0)
            {
                var subquery = $"l.Id IN (SELECT tla.ListingId FROM TaxonomyListingAssignments tla WHERE tla.TaxonomyRunId = {runIdQuery}";

                foreach (var (axisName, axisValue) in p.AxisFilters)
                {
                    var escapedName = axisName.Replace("'", "''");
                    var escapedValue = axisValue.Replace("'", "''");
                    subquery += $" AND JSON_VALUE(tla.CellJson, '$.\"{ escapedName }\"') = '{escapedValue}'";
                }

                subquery += ")";
                conditions.Add(subquery);
            }
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

        var useRegex = !string.IsNullOrWhiteSpace(p.Regex);
        Regex? rx = null;
        if (useRegex)
        {
            rx = new Regex(p.Regex!, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
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

        var allListings = await DbQueryHelper.ExecuteQuery(_db, fetchSql, ReadListingEntry);

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
            var countResult = await DbQueryHelper.ExecuteScalar(_db, countSql);
            totalCount = Convert.ToInt32(countResult ?? 0);
        }

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        ListingsAggregateStats stats;
        if (useRegex)
        {
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

            var statsRows = await DbQueryHelper.ExecuteQuery(_db, statsSql, reader =>
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

        return new ListingsQueryResult(listings, totalCount, page, pageSize, totalPages, stats);
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
