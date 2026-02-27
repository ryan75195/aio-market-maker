using System.Data;
using System.Data.Common;
using AIOMarketMaker.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AIOMarketMaker.Core.Services;

public record PredictionFilters(
    decimal PriceBand = 0,
    decimal FeePercent = 0,
    bool MatchCondition = true,
    int MinComps = 0,
    decimal MaxPrice = 0);

public record ListingPredictionResult(
    int ListingId,
    int SimilarSoldCount,
    decimal AverageSoldPrice,
    decimal PotentialProfit,
    int? EstimatedDaysToSell,
    double Confidence = 0,
    int OutliersRemoved = 0,
    decimal? MedianSoldPrice = null);

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
    double? ClassifierConfidence,
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
        string sortBy, string sortDir, int page, int pageSize,
        IEnumerable<int>? listingIds = null);
    Task<PredictionAggregates> GetAggregates(PredictionFilters filters);
}

public class ListingPredictionService : IListingPredictionService
{
    private readonly EtlDbContext _db;

    public ListingPredictionService(EtlDbContext db, IOptions<PricingOptions> pricingOptions)
    {
        _db = db;
    }

    public async Task<ListingPredictionResult?> GetPrediction(
        int listingId, PredictionFilters filters)
    {
        var prediction = await _db.ListingPredictions.FindAsync(listingId);
        if (prediction == null)
        {
            return null;
        }

        return new ListingPredictionResult(
            prediction.ListingId, prediction.SimilarSoldCount,
            prediction.AverageSoldPrice, prediction.PotentialProfit,
            prediction.EstimatedDaysToSell, prediction.Confidence,
            prediction.OutliersRemoved, prediction.MedianSoldPrice);
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
                comp.EndDateUtc, r.SimilarityScore, r.ClassifierConfidence, r.Explanation);
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
        string sortBy, string sortDir, int page, int pageSize,
        IEnumerable<int>? listingIds = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        sortDir = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        if (!AllowedSortColumns.Contains(sortBy))
        {
            sortBy = "potentialProfit";
        }

        var jobIdList = jobIds?.ToList() ?? new List<int>();
        var listingIdList = listingIds?.ToList() ?? new List<int>();

        var conditions = new List<string> { "l.ListingStatus = 'Active'" };
        if (jobIdList.Count > 0)
        {
            conditions.Add($"l.ScrapeJobId IN ({string.Join(",", jobIdList)})");
        }
        if (filters.MaxPrice > 0)
        {
            conditions.Add(FormattableString.Invariant($"l.Price <= {filters.MaxPrice}"));
        }
        if (listingIdList.Count > 0)
        {
            conditions.Add($"l.Id IN ({string.Join(",", listingIdList)})");
        }

        var joinType = filters.MinComps > 0 ? "INNER" : "LEFT";
        if (filters.MinComps > 0)
        {
            conditions.Add($"p.SimilarSoldCount >= {filters.MinComps}");
        }

        var whereClause = string.Join(" AND ", conditions);

        var countSql = $@"
            SELECT COUNT(*)
            FROM Listings l
            {joinType} JOIN ListingPredictions p ON p.ListingId = l.Id
            WHERE {whereClause}";

        var totalCount = (int)(await ExecuteScalar(countSql))!;

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

        var sql = $@"
            SELECT l.Id, p.SimilarSoldCount, p.AverageSoldPrice, p.PotentialProfit,
                   p.EstimatedDaysToSell, p.Confidence, p.OutliersRemoved, p.MedianSoldPrice
            FROM Listings l
            {joinType} JOIN ListingPredictions p ON p.ListingId = l.Id
            LEFT JOIN ScrapeJobs sj ON sj.Id = l.ScrapeJobId
            WHERE {whereClause}
            ORDER BY {orderClause}
            OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";

        var rows = await ExecuteQuery(sql, ReadPredictionResult);

        var orderedIds = rows.Select(r => r.ListingId).ToList();
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        return new PagedPredictions(rows, orderedIds, totalCount, page, pageSize, totalPages);
    }

    public async Task<PredictionAggregates> GetAggregates(PredictionFilters filters)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        int opportunities = 0;
        decimal aggregateProfit = 0m;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT COUNT(*), ISNULL(SUM(PotentialProfit), 0)
                FROM ListingPredictions";
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                opportunities = reader.GetInt32(0);
                aggregateProfit = reader.GetDecimal(1);
            }
        }

        var topOpportunities = new List<TopOpportunity>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT TOP 10
                    l.ListingId, l.Title, l.Price, l.Currency,
                    p.AverageSoldPrice, p.PotentialProfit, p.SimilarSoldCount,
                    l.[Condition], l.Url
                FROM ListingPredictions p
                INNER JOIN Listings l ON l.Id = p.ListingId
                ORDER BY p.PotentialProfit DESC";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                topOpportunities.Add(new TopOpportunity(
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

        var topJobs = new List<TopJobOpportunity>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT TOP 10
                    l.ScrapeJobId, sj.SearchTerm,
                    COUNT(*) AS OpportunityCount,
                    SUM(p.PotentialProfit) AS TotalProfit
                FROM ListingPredictions p
                INNER JOIN Listings l ON l.Id = p.ListingId
                LEFT JOIN ScrapeJobs sj ON sj.Id = l.ScrapeJobId
                GROUP BY l.ScrapeJobId, sj.SearchTerm
                ORDER BY COUNT(*) DESC";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                topJobs.Add(new TopJobOpportunity(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? 0m : reader.GetDecimal(3)));
            }
        }

        var avgProfitByCondition = new List<ConditionProfit>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT ISNULL(l.[Condition], 'Unknown'), AVG(p.PotentialProfit), COUNT(*)
                FROM ListingPredictions p
                INNER JOIN Listings l ON l.Id = p.ListingId
                GROUP BY l.[Condition]
                ORDER BY AVG(p.PotentialProfit) DESC";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                avgProfitByCondition.Add(new ConditionProfit(
                    reader.GetString(0),
                    reader.GetDecimal(1),
                    reader.GetInt32(2)));
            }
        }

        var avgDaysToSell = new List<DaysToSell>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT TOP 10
                    l.ScrapeJobId, sj.SearchTerm,
                    AVG(p.EstimatedDaysToSell)
                FROM ListingPredictions p
                INNER JOIN Listings l ON l.Id = p.ListingId
                LEFT JOIN ScrapeJobs sj ON sj.Id = l.ScrapeJobId
                WHERE p.EstimatedDaysToSell IS NOT NULL
                GROUP BY l.ScrapeJobId, sj.SearchTerm
                ORDER BY AVG(p.EstimatedDaysToSell) ASC";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                avgDaysToSell.Add(new DaysToSell(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : (decimal?)reader.GetInt32(2)));
            }
        }

        var priceVsProfit = new List<PriceVsProfit>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT
                    l.Price, p.PotentialProfit, l.[Condition]
                FROM ListingPredictions p
                INNER JOIN Listings l ON l.Id = p.ListingId
                WHERE l.Price IS NOT NULL";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                priceVsProfit.Add(new PriceVsProfit(
                    reader.GetDecimal(0),
                    reader.GetDecimal(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        return new PredictionAggregates(
            opportunities, aggregateProfit, topOpportunities, topJobs,
            avgProfitByCondition, avgDaysToSell, priceVsProfit);
    }

    private static ListingPredictionResult ReadPredictionResult(DbDataReader reader)
    {
        return new ListingPredictionResult(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : SafeGetDecimal(reader, 2),
            reader.IsDBNull(3) ? 0 : SafeGetDecimal(reader, 3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
            reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : SafeGetDecimal(reader, 7));
    }

    private static decimal SafeGetDecimal(DbDataReader reader, int ordinal)
    {
        var fieldType = reader.GetFieldType(ordinal);
        if (fieldType == typeof(double))
        {
            return (decimal)reader.GetDouble(ordinal);
        }
        return reader.GetDecimal(ordinal);
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

}
