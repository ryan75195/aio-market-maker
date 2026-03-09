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
