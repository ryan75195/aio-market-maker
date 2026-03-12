using System.Text.Json;
using AIOMarketMaker.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public class TaxonomyQueryService : ITaxonomyQueryService
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;

    public TaxonomyQueryService(IDbContextFactory<EtlDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<TaxonomyFacetResult?> GetFacets(
        int jobId, Dictionary<string, string> axisFilters, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var run = await db.TaxonomyRuns
            .AsNoTracking()
            .Where(r => r.ScrapeJobId == jobId)
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefaultAsync(ct);

        if (run == null)
        {
            return null;
        }

        var assignments = await LoadAssignments(db, run.Id, ct);
        var facets = TaxonomyFacets.ComputeFacets(assignments, axisFilters);

        return new TaxonomyFacetResult(
            facets, run.CoveragePercent, run.TotalListings, run.AssignedListings);
    }

    public async Task<IEnumerable<ParsedAssignment>> GetAssignments(
        int jobId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var run = await db.TaxonomyRuns
            .AsNoTracking()
            .Where(r => r.ScrapeJobId == jobId)
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefaultAsync(ct);

        if (run == null)
        {
            return Enumerable.Empty<ParsedAssignment>();
        }

        return await LoadAssignments(db, run.Id, ct);
    }

    public async Task<IEnumerable<TaxonomyCellComparable>> GetCellComparables(
        int listingId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Find the taxonomy opportunity for this listing
        var opportunity = await db.TaxonomyOpportunities
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.ListingId == listingId, ct);

        if (opportunity == null)
        {
            return Enumerable.Empty<TaxonomyCellComparable>();
        }

        // Get assignments for this job
        var assignments = await GetAssignmentsInternal(db, opportunity.ScrapeJobId, ct);
        if (assignments.Count == 0)
        {
            return Enumerable.Empty<TaxonomyCellComparable>();
        }

        // Find listing IDs in the same cell
        var assignedListingIds = assignments.Select(a => a.ListingId).ToHashSet();

        // Load listings to get condition for cell key computation
        var listings = await db.Listings
            .AsNoTracking()
            .Where(l => assignedListingIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, ct);

        // Match assignments to the same cell key
        var cellComps = new List<TaxonomyCellComparable>();
        foreach (var assignment in assignments)
        {
            if (assignment.ListingId == listingId)
            {
                continue;
            }

            if (!listings.TryGetValue(assignment.ListingId, out var listing))
            {
                continue;
            }

            if (!string.Equals(listing.ListingStatus, "Sold", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Build cell key the same way CellPricingService does
            var cell = new Dictionary<string, string>(assignment.Cell);
            if (listing.Condition != null)
            {
                cell["condition"] = listing.Condition;
            }

            var cellKey = string.Join(" | ", cell.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            if (cellKey != opportunity.CellKey)
            {
                continue;
            }

            cellComps.Add(new TaxonomyCellComparable(
                listing.Id,
                listing.ListingId,
                listing.Title,
                listing.Description,
                listing.Price,
                listing.Condition,
                listing.Url,
                listing.Images,
                listing.EndDateUtc,
                cellKey));
        }

        return cellComps;
    }

    private async Task<List<ParsedAssignment>> GetAssignmentsInternal(
        EtlDbContext db, int jobId, CancellationToken ct)
    {
        var run = await db.TaxonomyRuns
            .AsNoTracking()
            .Where(r => r.ScrapeJobId == jobId)
            .OrderByDescending(r => r.CreatedUtc)
            .FirstOrDefaultAsync(ct);

        if (run == null)
        {
            return new List<ParsedAssignment>();
        }

        return await LoadAssignments(db, run.Id, ct);
    }

    private static async Task<List<ParsedAssignment>> LoadAssignments(
        EtlDbContext db, int runId, CancellationToken ct)
    {
        var raw = await db.TaxonomyListingAssignments
            .AsNoTracking()
            .Where(a => a.TaxonomyRunId == runId)
            .Select(a => new { a.ListingId, a.CellJson })
            .ToListAsync(ct);

        return raw
            .Select(a =>
            {
                var cell = JsonSerializer.Deserialize<Dictionary<string, string>>(a.CellJson)
                    ?? new Dictionary<string, string>();
                return new ParsedAssignment(a.ListingId, cell);
            })
            .ToList();
    }
}
