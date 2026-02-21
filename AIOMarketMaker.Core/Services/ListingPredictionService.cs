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

    public async Task<ListingPredictionResult?> GetPrediction(
        int listingId, PredictionFilters filters)
    {
        var comps = (await GetComparables(listingId, filters)).ToList();
        var listing = await _db.Listings.FindAsync(listingId);
        if (listing == null)
        {
            return null;
        }

        var pricedComps = comps
            .Where(c => c.Price.HasValue && c.Price.Value > 0)
            .ToList();

        if (pricedComps.Count == 0)
        {
            return null;
        }

        var avgSoldPrice = pricedComps.Average(c => c.Price!.Value);
        var profit = filters.FeePercent > 0
            ? avgSoldPrice * (1.0m - filters.FeePercent / 100.0m)
                - listing.Price!.Value - (listing.ShippingCost ?? 0)
            : avgSoldPrice - listing.Price!.Value;

        int? estimatedDays = null;
        var compsWithDates = comps
            .Where(c => c.SoldDateUtc.HasValue)
            .ToList();
        if (compsWithDates.Count > 0)
        {
            var soldIds = compsWithDates.Select(c => c.SoldListingId).ToList();
            var soldListings = await _db.Listings
                .Where(l => soldIds.Contains(l.Id) && l.EndDateUtc > l.CreatedUtc)
                .Select(l => new { l.Id, l.CreatedUtc, l.EndDateUtc })
                .ToListAsync();

            if (soldListings.Count > 0)
            {
                var avgDays = soldListings
                    .Average(l => (l.EndDateUtc!.Value - l.CreatedUtc).TotalDays);
                estimatedDays = (int)Math.Round(avgDays);
            }
        }

        return new ListingPredictionResult(
            listingId, pricedComps.Count, avgSoldPrice, profit, estimatedDays);
    }

    public async Task<IEnumerable<ComparableSoldListing>> GetComparables(
        int listingId, PredictionFilters filters)
    {
        var listing = await _db.Listings.FindAsync(listingId);
        if (listing == null)
        {
            return Enumerable.Empty<ComparableSoldListing>();
        }

        var relationships = await _db.ListingRelationships
            .Include(r => r.ListingA)
            .Include(r => r.ListingB)
            .Where(r => r.IsComparable && (r.ListingIdA == listingId || r.ListingIdB == listingId))
            .Where(r => r.ListingIdA == listingId
                ? r.ListingB.ListingStatus == "Sold"
                : r.ListingA.ListingStatus == "Sold")
            .ToListAsync();

        var comparables = relationships.Select(r =>
        {
            var comp = r.ListingIdA == listingId ? r.ListingB : r.ListingA;
            return new ComparableSoldListing(
                r.Id, comp.Id, comp.ListingId, comp.Title, comp.Description,
                comp.Price, comp.Condition, comp.Url, comp.Images,
                comp.EndDateUtc, r.SimilarityScore, r.Explanation);
        }).ToList();

        if (filters.MatchCondition && listing.Condition != null)
        {
            comparables = comparables
                .Where(c => c.Condition == listing.Condition)
                .ToList();
        }

        if (filters.PriceBand > 0 && listing.Price.HasValue && listing.Price.Value > 0)
        {
            var minPrice = listing.Price.Value / filters.PriceBand;
            var maxPrice = listing.Price.Value * filters.PriceBand;
            comparables = comparables
                .Where(c => c.Price.HasValue && c.Price.Value >= minPrice && c.Price.Value <= maxPrice)
                .ToList();
        }

        return comparables;
    }

    private static readonly HashSet<string> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "price", "averageSoldPrice", "potentialProfit",
        "similarSoldCount", "estimatedDaysToSell", "condition",
        "createdUtc", "searchTerm"
    };

    public async Task<PagedPredictions> GetPredictions(
        PredictionFilters filters, IEnumerable<int>? jobIds,
        string sortBy, string sortDir, int page, int pageSize)
    {
        if (IsSqlite())
        {
            return new PagedPredictions(
                Enumerable.Empty<ListingPredictionResult>(),
                Enumerable.Empty<int>(), 0, page, pageSize, 0);
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        sortDir = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        if (!AllowedSortColumns.Contains(sortBy))
        {
            sortBy = "potentialProfit";
        }

        var jobIdList = jobIds?.ToList() ?? new List<int>();
        var cte = BuildCte(filters);
        var joinType = filters.MinComps > 0 ? "INNER JOIN" : "LEFT JOIN";

        int totalCount;
        if (filters.MinComps > 0)
        {
            var jobFilterClauseCount = jobIdList.Count > 0
                ? $"AND l.ScrapeJobId IN ({string.Join(",", jobIdList)})"
                : "";
            var countSql = $@"
                {cte}
                SELECT COUNT(*)
                FROM Listings l
                INNER JOIN FilteredPredictions p ON p.ListingId = l.Id
                WHERE l.ListingStatus = 'Active'
                {jobFilterClauseCount}";
            totalCount = (int)(await ExecuteScalar(countSql))!;
        }
        else
        {
            var countQuery = _db.Listings.Where(l => l.ListingStatus == "Active");
            if (jobIdList.Count > 0)
            {
                countQuery = countQuery.Where(l => jobIdList.Contains(l.ScrapeJobId));
            }
            totalCount = await countQuery.CountAsync();
        }

        if (totalCount == 0)
        {
            return new PagedPredictions(
                Enumerable.Empty<ListingPredictionResult>(),
                Enumerable.Empty<int>(), 0, page, pageSize, 0);
        }

        var orderByColumn = sortBy.ToLowerInvariant() switch
        {
            "title" => "l.Title",
            "price" => "l.Price",
            "averagedsoldprice" or "averagesoldprice" => "p.AverageSoldPrice",
            "potentialprofit" => "p.PotentialProfit",
            "similarsoldcount" => "p.SimilarSoldCount",
            "estimateddaystosell" => "p.EstimatedDaysToSell",
            "condition" => "l.[Condition]",
            "createdutc" => "l.CreatedUtc",
            "searchterm" => "sj.SearchTerm",
            _ => "p.PotentialProfit"
        };

        var nullsLast = $"CASE WHEN {orderByColumn} IS NULL THEN 1 ELSE 0 END";
        var orderClause = $"{nullsLast}, {orderByColumn} {sortDir.ToUpperInvariant()}";
        var offset = (page - 1) * pageSize;

        var jobFilterClause = jobIdList.Count > 0
            ? $"AND l.ScrapeJobId IN ({string.Join(",", jobIdList)})"
            : "";

        var sql = $@"
            {cte}
            SELECT l.Id, p.AverageSoldPrice, p.SimilarSoldCount, p.PotentialProfit, p.EstimatedDaysToSell
            FROM Listings l
            {joinType} FilteredPredictions p ON p.ListingId = l.Id
            LEFT JOIN ScrapeJobs sj ON sj.Id = l.ScrapeJobId
            WHERE l.ListingStatus = 'Active'
            {jobFilterClause}
            ORDER BY {orderClause}
            OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";

        var rows = await ExecuteQuery(sql, reader => new ListingPredictionResult(
            reader.GetInt32(0),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(1) ? 0 : reader.GetDecimal(1),
            reader.IsDBNull(3) ? 0 : reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4)));

        var orderedIds = rows.Select(r => r.ListingId).ToList();
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        return new PagedPredictions(rows, orderedIds, totalCount, page, pageSize, totalPages);
    }

    public Task<PredictionAggregates> GetAggregates(PredictionFilters filters)
    {
        throw new NotImplementedException();
    }

    private static string BuildCte(PredictionFilters filters)
    {
        var pb = filters.PriceBand.ToString(CultureInfo.InvariantCulture);
        var fee = filters.FeePercent.ToString(CultureInfo.InvariantCulture);
        var mc = filters.MinComps.ToString(CultureInfo.InvariantCulture);

        var conditionFilter = filters.MatchCondition
            ? "AND active.[Condition] = sold.[Condition]"
            : "";

        var priceBandFilter = filters.PriceBand > 0
            ? $@"AND active.Price > 0
               AND sold.Price BETWEEN active.Price / {pb} AND active.Price * {pb}"
            : "";

        var profitExpr = filters.FeePercent > 0
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
        {priceBandFilter}
        GROUP BY active.Id, active.Price, active.ShippingCost
        HAVING COUNT(*) >= {mc}
            AND {profitExpr} > 0
    )";
    }

    private async Task<object?> ExecuteScalar(string sql)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<List<T>> ExecuteQuery<T>(string sql, Func<DbDataReader, T> map)
    {
        var conn = _db.Database.GetDbConnection();
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

    private bool IsSqlite()
    {
        return _db.Database.GetDbConnection().GetType().Name.Contains("Sqlite");
    }
}
