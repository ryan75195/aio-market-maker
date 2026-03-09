using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Api.Endpoints;

public record OpportunityResponse(
    int Id, string? ListingId, string? Title, decimal AskPrice,
    string? Currency, string? Url, string? Condition,
    string? SearchTerm, string CellKey,
    decimal MedianSoldPrice, decimal EstimatedProfit,
    double MarginPercent, int SoldComps, int? AvgDaysToSell,
    DateTime CreatedUtc);

public record OpportunityPagedResponse(
    IEnumerable<OpportunityResponse> Items,
    int TotalCount, int Page, int PageSize, int TotalPages,
    decimal AggregateProfit, int TotalOpportunities);

public static class OpportunityEndpoints
{
    public static void MapOpportunityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/opportunities", GetOpportunities);
    }

    private static async Task<IResult> GetOpportunities(
        EtlDbContext db,
        int page = 1,
        int pageSize = 50,
        string sortBy = "estimatedProfit",
        string sortDir = "desc",
        string? jobIds = null,
        string? categoryIds = null,
        decimal maxPrice = 0,
        string? search = null,
        int minComps = 0)
    {
        if (page < 1)
        {
            page = 1;
        }
        if (pageSize < 1)
        {
            pageSize = 50;
        }
        if (pageSize > 200)
        {
            pageSize = 200;
        }

        var query = db.TaxonomyOpportunities
            .Include(o => o.Listing)
            .Include(o => o.ScrapeJob)
            .AsNoTracking()
            .AsQueryable();

        // Filter by job IDs
        if (!string.IsNullOrEmpty(jobIds))
        {
            var ids = jobIds.Split(',')
                .Select(s => int.TryParse(s.Trim(), out var id) ? id : (int?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();
            if (ids.Count > 0)
            {
                query = query.Where(o => ids.Contains(o.ScrapeJobId));
            }
        }

        // Filter by category IDs (resolve to job IDs)
        if (!string.IsNullOrEmpty(categoryIds))
        {
            var catIds = categoryIds.Split(',')
                .Select(s => int.TryParse(s.Trim(), out var id) ? id : (int?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToList();
            if (catIds.Count > 0)
            {
                var catJobIds = await db.JobCategories
                    .Where(jc => catIds.Contains(jc.CategoryId))
                    .Select(jc => jc.JobId)
                    .Distinct()
                    .ToListAsync();
                query = query.Where(o => catJobIds.Contains(o.ScrapeJobId));
            }
        }

        // Budget filter
        if (maxPrice > 0)
        {
            query = query.Where(o => o.AskPrice <= maxPrice);
        }

        // Text search on title
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(o => o.Listing!.Title != null && o.Listing.Title.Contains(search));
        }

        // Min comps filter
        if (minComps > 0)
        {
            query = query.Where(o => o.SoldComps >= minComps);
        }

        // Get aggregates before pagination
        var totalCount = await query.CountAsync();
        // Use double for SumAsync to avoid SQLite decimal limitation
        var aggregateProfit = totalCount > 0
            ? (decimal)await query.SumAsync(o => (double)o.EstimatedProfit)
            : 0;

        // Sort — cast decimal to double for SQLite compatibility
        query = sortBy?.ToLowerInvariant() switch
        {
            "title" => sortDir == "asc"
                ? query.OrderBy(o => o.Listing!.Title)
                : query.OrderByDescending(o => o.Listing!.Title),
            "searchterm" => sortDir == "asc"
                ? query.OrderBy(o => o.ScrapeJob!.SearchTerm)
                : query.OrderByDescending(o => o.ScrapeJob!.SearchTerm),
            "askprice" or "price" => sortDir == "asc"
                ? query.OrderBy(o => (double)o.AskPrice)
                : query.OrderByDescending(o => (double)o.AskPrice),
            "mediansoldprice" or "averagesoldprice" => sortDir == "asc"
                ? query.OrderBy(o => (double)o.MedianSoldPrice)
                : query.OrderByDescending(o => (double)o.MedianSoldPrice),
            "soldcomps" or "similarsoldcount" => sortDir == "asc"
                ? query.OrderBy(o => o.SoldComps)
                : query.OrderByDescending(o => o.SoldComps),
            "marginpercent" => sortDir == "asc"
                ? query.OrderBy(o => o.MarginPercent)
                : query.OrderByDescending(o => o.MarginPercent),
            "avgdaystosell" or "estimateddaystosell" => sortDir == "asc"
                ? query.OrderBy(o => o.AvgDaysToSell)
                : query.OrderByDescending(o => o.AvgDaysToSell),
            "condition" => sortDir == "asc"
                ? query.OrderBy(o => o.Listing!.Condition)
                : query.OrderByDescending(o => o.Listing!.Condition),
            "createdutc" => sortDir == "asc"
                ? query.OrderBy(o => o.Listing!.CreatedUtc)
                : query.OrderByDescending(o => o.Listing!.CreatedUtc),
            _ => sortDir == "asc"
                ? query.OrderBy(o => (double)o.EstimatedProfit)
                : query.OrderByDescending(o => (double)o.EstimatedProfit),
        };

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new OpportunityResponse(
                o.Listing!.Id,
                o.Listing.ListingId,
                o.Listing.Title,
                o.AskPrice,
                o.Listing.Currency,
                o.Listing.Url,
                o.Listing.Condition,
                o.ScrapeJob!.SearchTerm,
                o.CellKey,
                o.MedianSoldPrice,
                o.EstimatedProfit,
                o.MarginPercent,
                o.SoldComps,
                o.AvgDaysToSell,
                o.Listing.CreatedUtc))
            .ToListAsync();

        return Results.Ok(new OpportunityPagedResponse(
            items, totalCount, page, pageSize, totalPages,
            aggregateProfit, totalCount));
    }
}
