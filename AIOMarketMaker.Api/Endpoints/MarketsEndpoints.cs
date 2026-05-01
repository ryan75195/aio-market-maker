using System.Text.RegularExpressions;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Taxonomy;
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

public record TaxonomyResponse(
    IEnumerable<FacetAxis> Axes,
    double Coverage,
    int TotalListings,
    int AssignedListings);

public record PricingResponse(
    IEnumerable<CellPricingEntry> Cells,
    IEnumerable<OpportunityEntry> Opportunities,
    int TotalListings,
    int CoveredListings);

public record CellPricingEntry(
    string CellKey,
    int ActiveCount,
    int SoldCount,
    decimal? MedianActivePrice,
    decimal? MedianSoldPrice,
    decimal? Spread);

public record OpportunityEntry(
    int ListingId,
    string Title,
    decimal AskPrice,
    decimal MedianSoldPrice,
    decimal EstimatedProfit,
    double MarginPercent,
    int SoldComps,
    string CellKey);

public static class MarketsEndpoints
{
    public static void MapMarketsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/markets", GetMarkets);
        app.MapGet("/api/markets/{jobId:int}/listings", GetJobListings);
        app.MapGet("/api/markets/{jobId:int}/taxonomy", GetTaxonomy);
        app.MapGet("/api/markets/{jobId:int}/pricing", GetPricing);
        app.MapGet("/api/markets/{jobId:int}/conditions", GetConditions);
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

    private static async Task<IResult> GetJobListings(
        int jobId,
        IMarketListingsQueryService queryService,
        HttpContext httpContext,
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
        try
        {
            var axisFilters = ParseAxisFilters(httpContext.Request.Query);
            var result = await queryService.Query(new ListingsQueryParams(
                jobId, status, search, condition, minPrice, maxPrice,
                minDays, maxDays, regex, sortBy, sortDir, page, pageSize,
                axisFilters.Count > 0 ? axisFilters : null));

            return Results.Ok(result);
        }
        catch (RegexParseException)
        {
            return Results.BadRequest(new { error = "Invalid regex pattern" });
        }
    }

    private static async Task<IResult> GetTaxonomy(
        int jobId,
        ITaxonomyQueryService taxonomyService,
        HttpContext httpContext,
        CancellationToken ct)
    {
        var axisFilters = ParseAxisFilters(httpContext.Request.Query);
        var result = await taxonomyService.GetFacets(jobId, axisFilters, ct);

        if (result == null)
        {
            return Results.Ok(new TaxonomyResponse(
                Enumerable.Empty<FacetAxis>(), 0, 0, 0));
        }

        return Results.Ok(new TaxonomyResponse(
            result.Axes, result.Coverage, result.TotalListings, result.AssignedListings));
    }

    private static async Task<IResult> GetPricing(
        int jobId,
        ITaxonomyQueryService taxonomyService,
        ICellPricingService cellPricingService,
        IDbContextFactory<EtlDbContext> dbFactory,
        HttpContext httpContext,
        double fee = 13.25,
        int minComps = 3,
        CancellationToken ct = default)
    {
        var axisFilters = ParseAxisFilters(httpContext.Request.Query);
        var assignments = await taxonomyService.GetAssignments(jobId, ct);
        var assignmentList = assignments.ToList();

        if (assignmentList.Count == 0)
        {
            return Results.Ok(new PricingResponse(
                Enumerable.Empty<CellPricingEntry>(),
                Enumerable.Empty<OpportunityEntry>(),
                0, 0));
        }

        // Filter assignments by selected axes
        var filtered = TaxonomyFacets.FilterAssignments(assignmentList, axisFilters).ToList();
        var filteredListingIds = filtered.Select(a => a.ListingId).ToHashSet();

        // Load listings with prices
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listings = await db.Listings
            .AsNoTracking()
            .Where(l => l.ScrapeJobId == jobId && filteredListingIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Title, l.Price, l.ListingStatus, l.Condition })
            .ToListAsync(ct);

        // Build inputs for CellPricingService using ListingId as index
        var listingIndex = new Dictionary<int, int>(); // ListingId -> index
        var pricedListings = new List<PricedListing>();
        var idx = 0;
        foreach (var l in listings)
        {
            listingIndex[l.Id] = idx;
            var isSold = l.ListingStatus is "Sold" or "Ended";
            pricedListings.Add(new PricedListing(l.Id, l.Title ?? "", l.Price ?? 0, isSold, idx, l.Condition));
            idx++;
        }

        // Build CellAssignments with matching indices
        var cellAssignments = new List<CellAssignment>();
        foreach (var a in filtered)
        {
            if (listingIndex.TryGetValue(a.ListingId, out var i))
            {
                cellAssignments.Add(new CellAssignment(i, a.Cell, false));
            }
        }

        var taxonomy = new TaxonomyResult(
            Enumerable.Empty<Axis>(), cellAssignments, Enumerable.Empty<CellStats>(), 0, 0);

        var pricing = cellPricingService.Compute(taxonomy, pricedListings, fee, minComps);

        return Results.Ok(new PricingResponse(
            pricing.Cells.Select(c => new CellPricingEntry(
                c.CellKey, c.ActiveCount, c.SoldCount,
                c.MedianActivePrice, c.MedianSoldPrice, c.Spread)),
            pricing.Opportunities.Select(o => new OpportunityEntry(
                o.ListingId, o.Title, o.AskPrice, o.MedianSoldPrice,
                o.EstimatedProfit, o.MarginPercent, o.SoldComps, o.CellKey)),
            pricing.TotalListings,
            pricing.CoveredListings));
    }

    private static async Task<IResult> GetConditions(
        int jobId,
        EtlDbContext db,
        CancellationToken ct)
    {
        var conditions = await db.Listings
            .AsNoTracking()
            .Where(l => l.ScrapeJobId == jobId && l.Condition != null)
            .Select(l => l.Condition!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);

        return Results.Ok(conditions);
    }

    private static Dictionary<string, string> ParseAxisFilters(IQueryCollection query)
    {
        var filters = new Dictionary<string, string>();
        foreach (var key in query.Keys)
        {
            if (key.StartsWith("axis", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(query[key]))
            {
                var axisName = key[4..];
                filters[axisName] = query[key]!;
            }
        }
        return filters;
    }
}
