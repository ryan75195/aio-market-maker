using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Azure.Storage.Blobs;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Api.Endpoints;

public record OpportunityListing(
    int Id, string ListingId, string? Title, decimal? Price,
    string? Currency, decimal? ShippingCost, string? Url,
    string? Condition, string? ListingStatus, DateTime? EndDateUtc,
    DateTime CreatedUtc, string? SearchTerm, string? Images,
    decimal? AverageSoldPrice, int SimilarSoldCount,
    int? EstimatedDaysToSell, decimal? PotentialProfit);

public record PagedResponse<T>(
    IEnumerable<T> Items, int TotalCount, int Page, int PageSize, int TotalPages);

public record PricingAggregate(decimal? AvgPrice, int Count, int? AvgDaysToSell);
public record DeletedResponse(int Deleted);
public record ClearListingsResponse(int Deleted, bool IndexCleared);
public record ClearHistoryResponse(int Deleted);
public record ClearDataResponse(int DeletedListings, int DeletedRuns, bool BlobsCleared, bool IndexCleared);
public record ListingStatsEntry(string? Currency, int Total, int NullPrice, int NullTitle);
public record InvalidListingResponse(int Id, string ListingId, string? Title, decimal? Price, string? Currency, string? Url, DateTime CreatedUtc);

public static class ListingEndpoints
{
    public static void MapListingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/listings/active", GetActiveListings);
        app.MapGet("/api/listings/stats", GetListingStats);
        app.MapGet("/api/listings/invalid", GetInvalidListings);
        app.MapDelete("/api/listings/invalid", DeleteInvalidListings);
        app.MapDelete("/api/listings/all", ClearAllListings);
        app.MapDelete("/api/history/all", ClearAllHistory);
        app.MapDelete("/api/data/all", ClearAllData);
    }

    private record ListingIdWithPrediction(
        int Id, decimal? AverageSoldPrice, int? SimilarSoldCount, decimal? PotentialProfit,
        int? EstimatedDaysToSell);

    private static readonly HashSet<string> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "price", "averageSoldPrice", "potentialProfit",
        "similarSoldCount", "estimatedDaysToSell", "condition",
        "createdUtc", "searchTerm"
    };

    private static async Task<IResult> GetActiveListings(
        EtlDbContext db,
        int page = 1,
        int pageSize = 50,
        string sortBy = "potentialProfit",
        string sortDir = "desc",
        string? jobIds = null,
        int minComps = 0,
        decimal priceBand = 0,
        decimal feePercent = 0,
        bool matchCondition = true)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        sortDir = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        if (!AllowedSortColumns.Contains(sortBy))
        {
            sortBy = "potentialProfit";
        }

        var jobIdFilter = ParseJobIds(jobIds);
        var cte = BuildFilteredPredictionsCte(priceBand, feePercent, minComps, matchCondition);
        var joinType = minComps > 0 ? "INNER JOIN" : "LEFT JOIN";

        // Count total matching active listings
        int totalCount;
        if (minComps > 0)
        {
            var jobFilterClauseCount = jobIdFilter.Count > 0
                ? $"AND l.ScrapeJobId IN ({string.Join(",", jobIdFilter)})"
                : "";
            var countSql = $@"
                {cte}
                SELECT COUNT(*)
                FROM Listings l
                INNER JOIN FilteredPredictions p ON p.ListingId = l.Id
                WHERE l.ListingStatus = 'Active'
                {jobFilterClauseCount}";
            totalCount = (int)(await ExecuteScalar(db, countSql))!;
        }
        else
        {
            var countQuery = db.Listings.Where(l => l.ListingStatus == "Active");
            if (jobIdFilter.Count > 0)
            {
                countQuery = countQuery.Where(l => jobIdFilter.Contains(l.ScrapeJobId));
            }
            totalCount = await countQuery.CountAsync();
        }

        if (totalCount == 0)
        {
            return Results.Ok(new PagedResponse<OpportunityListing>(
                Enumerable.Empty<OpportunityListing>(), 0, page, pageSize, 0));
        }

        // Sort + paginate via raw SQL with inline CTE
        var orderByColumn = sortBy.ToLowerInvariant() switch
        {
            "title" => "l.Title",
            "price" => "l.Price",
            "averagedsoldprice" or "averageSoldPrice" => "p.AverageSoldPrice",
            "potentialprofit" or "potentialProfit" => "p.PotentialProfit",
            "similarsoldcount" or "similarSoldCount" => "p.SimilarSoldCount",
            "estimateddaystosell" or "estimatedDaysToSell" => "p.EstimatedDaysToSell",
            "condition" => "l.[Condition]",
            "createdutc" or "createdUtc" => "l.CreatedUtc",
            "searchterm" or "searchTerm" => "sj.SearchTerm",
            _ => "p.PotentialProfit"
        };

        var nullsLast = $"CASE WHEN {orderByColumn} IS NULL THEN 1 ELSE 0 END";
        var orderClause = $"{nullsLast}, {orderByColumn} {sortDir.ToUpperInvariant()}";
        var offset = (page - 1) * pageSize;

        var jobFilterClause = jobIdFilter.Count > 0
            ? $"AND l.ScrapeJobId IN ({string.Join(",", jobIdFilter)})"
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

        var rows = await ExecuteQuery(db, sql, reader => new ListingIdWithPrediction(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetDecimal(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4)));

        if (rows.Count == 0)
        {
            var totalPages0 = (int)Math.Ceiling((double)totalCount / pageSize);
            return Results.Ok(new PagedResponse<OpportunityListing>(
                Enumerable.Empty<OpportunityListing>(), totalCount, page, pageSize, totalPages0));
        }

        var ids = rows.Select(r => r.Id).ToList();

        // Build predictions dictionary from CTE results (no second query needed)
        var predictions = rows
            .Where(r => r.AverageSoldPrice.HasValue)
            .ToDictionary(
                r => r.Id,
                r => new PricingAggregate(r.AverageSoldPrice, r.SimilarSoldCount ?? 0, r.EstimatedDaysToSell));

        // Pass through fee-adjusted profit from CTE when fees are applied
        Dictionary<int, decimal>? profitOverrides = null;
        if (feePercent > 0 || priceBand > 0)
        {
            profitOverrides = rows
                .Where(r => r.PotentialProfit.HasValue)
                .ToDictionary(r => r.Id, r => r.PotentialProfit!.Value);
        }

        // Load full entities preserving SQL sort order
        var listings = await db.Listings
            .Include(l => l.ScrapeJob)
            .Where(l => ids.Contains(l.Id))
            .ToListAsync();

        var idOrder = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        listings.Sort((a, b) => idOrder.GetValueOrDefault(a.Id, int.MaxValue)
            .CompareTo(idOrder.GetValueOrDefault(b.Id, int.MaxValue)));

        var items = listings.Select(l => ToOpportunityListing(l, predictions, profitOverrides));
        var totalPagesCalc = (int)Math.Ceiling((double)totalCount / pageSize);

        return Results.Ok(new PagedResponse<OpportunityListing>(
            items, totalCount, page, pageSize, totalPagesCalc));
    }

    private static string BuildFilteredPredictionsCte(
        decimal priceBand, decimal feePercent, int minComps, bool matchCondition)
    {
        var pb = priceBand.ToString(CultureInfo.InvariantCulture);
        var fee = feePercent.ToString(CultureInfo.InvariantCulture);
        var mc = minComps.ToString(CultureInfo.InvariantCulture);

        var conditionFilter = matchCondition
            ? "AND active.[Condition] = sold.[Condition]"
            : "";

        var priceBandFilter = priceBand > 0
            ? $@"AND active.Price > 0
                   AND sp.SoldPrice BETWEEN active.Price / {pb} AND active.Price * {pb}"
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
            {priceBandFilter}
            GROUP BY active.Id, active.Price, active.ShippingCost
            HAVING COUNT(*) >= {mc}
                AND {profitExpr} > 0
        )";
    }

    private static async Task<object?> ExecuteScalar(EtlDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
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

    private static List<int> ParseJobIds(string? jobIds)
    {
        if (string.IsNullOrWhiteSpace(jobIds))
        {
            return new List<int>();
        }

        return jobIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
    }

    private static async Task<IResult> GetListingStats(EtlDbContext db)
    {
        var twoHoursAgo = DateTime.UtcNow.AddHours(-2);
        var stats = await db.Listings
            .Where(l => l.CreatedUtc > twoHoursAgo)
            .GroupBy(l => l.Currency)
            .Select(g => new ListingStatsEntry(
                g.Key,
                g.Count(),
                g.Count(x => x.Price == null),
                g.Count(x => x.Title == null)))
            .ToListAsync();

        return Results.Ok(stats);
    }

    private static async Task<IResult> GetInvalidListings(EtlDbContext db)
    {
        var invalidListings = await db.Listings
            .Where(l => l.Title == null || l.Price == null)
            .OrderByDescending(l => l.CreatedUtc)
            .Take(100)
            .Select(l => new InvalidListingResponse(
                l.Id, l.ListingId, l.Title, l.Price,
                l.Currency, l.Url, l.CreatedUtc))
            .ToListAsync();

        return Results.Ok(invalidListings);
    }

    private static async Task<IResult> DeleteInvalidListings(
        EtlDbContext db, ILogger<Program> logger)
    {
        var invalidListings = await db.Listings
            .Where(l => l.Title == null || l.Price == null)
            .ToListAsync();

        var count = invalidListings.Count;
        db.Listings.RemoveRange(invalidListings);
        await db.SaveChangesAsync();

        logger.LogInformation("Deleted {Count} invalid listings (missing title or price)", count);

        return Results.Ok(new DeletedResponse(count));
    }

    private static async Task<IResult> ClearAllListings(
        EtlDbContext db, IVectorIndex vectorIndex, ILogger<Program> logger)
    {
        var count = await db.Listings.CountAsync();

        if (count > 0)
        {
            // Delete relationships first (NoAction FK to Listings). Predictions are a live view.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM ListingRelationships");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Listings");
            logger.LogInformation("Cleared {Count} listings from database", count);
        }

        bool indexCleared = ClearVectorIndex(vectorIndex, logger);

        return Results.Ok(new ClearListingsResponse(count, indexCleared));
    }

    private static async Task<IResult> ClearAllHistory(
        EtlDbContext db, ILogger<Program> logger)
    {
        var count = await db.ScrapeRuns.CountAsync();

        if (count > 0)
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM ScrapeRuns");
            logger.LogInformation("Cleared {Count} scrape runs from database", count);
        }

        return Results.Ok(new ClearHistoryResponse(count));
    }

    private static async Task<IResult> ClearAllData(
        EtlDbContext db, BlobServiceClient blobService, IVectorIndex vectorIndex,
        ILogger<Program> logger)
    {
        var listingsCount = await db.Listings.CountAsync();
        var runsCount = await db.ScrapeRuns.CountAsync();

        // Delete in correct order: relationships first (NoAction FK), then Listings, then ScrapeRuns (cascades)
        await db.Database.ExecuteSqlRawAsync("DELETE FROM ListingRelationships");
        if (listingsCount > 0)
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Listings");
        }
        if (runsCount > 0)
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM ScrapeRuns");
        }

        // Clear blob storage (HTML files) - delete and recreate container for speed
        bool blobsCleared = false;
        try
        {
            var containerClient = blobService.GetBlobContainerClient("html");
            if (await containerClient.ExistsAsync())
            {
                await containerClient.DeleteAsync();
                await containerClient.CreateIfNotExistsAsync();
                blobsCleared = true;
            }
            logger.LogInformation("Cleared html blob container");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear blob storage (non-fatal)");
        }

        // Clear local vector index
        bool indexCleared = ClearVectorIndex(vectorIndex, logger);

        logger.LogInformation(
            "Cleared all data: {Listings} listings, {Runs} scrape runs, blobs cleared: {BlobsCleared}, index cleared: {IndexCleared}",
            listingsCount, runsCount, blobsCleared, indexCleared);

        return Results.Ok(new ClearDataResponse(listingsCount, runsCount, blobsCleared, indexCleared));
    }

    private static bool ClearVectorIndex(IVectorIndex vectorIndex, ILogger logger)
    {
        try
        {
            var count = vectorIndex.Count;
            vectorIndex.Clear();
            vectorIndex.Save();
            logger.LogInformation("Cleared {Count} vectors from local index", count);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear vector index (non-fatal)");
            return false;
        }
    }

    private static OpportunityListing ToOpportunityListing(
        Listing l,
        Dictionary<int, PricingAggregate> grouped,
        Dictionary<int, decimal>? profitOverrides = null)
    {
        grouped.TryGetValue(l.Id, out var agg);

        decimal? profit = null;
        if (profitOverrides != null && profitOverrides.TryGetValue(l.Id, out var overrideProfit))
        {
            profit = overrideProfit;
        }
        else if (agg?.AvgPrice != null && l.Price.HasValue)
        {
            profit = agg.AvgPrice.Value - l.Price.Value;
        }

        return new OpportunityListing(
            l.Id,
            l.ListingId,
            l.Title,
            l.Price,
            l.Currency,
            l.ShippingCost,
            l.Url,
            l.Condition,
            l.ListingStatus,
            l.EndDateUtc,
            l.CreatedUtc,
            l.ScrapeJob?.SearchTerm,
            l.Images,
            agg?.AvgPrice,
            agg?.Count ?? 0,
            agg?.AvgDaysToSell,
            profit);
    }
}
