using System.Text.Json;
using AIOMarketMaker.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Api.Services;

public record ParsedAssignment(int ListingId, Dictionary<string, string> Cell);

public record FacetValue(string Label, int Count);

public record FacetAxis(string Name, IEnumerable<FacetValue> Values);

public record TaxonomyFacetResult(
    IEnumerable<FacetAxis> Axes,
    double Coverage,
    int TotalListings,
    int AssignedListings);

public interface ITaxonomyQueryService
{
    Task<TaxonomyFacetResult?> GetFacets(
        int jobId, Dictionary<string, string> axisFilters, CancellationToken ct = default);

    Task<IEnumerable<ParsedAssignment>> GetAssignments(
        int jobId, CancellationToken ct = default);
}

public static class TaxonomyFacets
{
    public static IEnumerable<FacetAxis> ComputeFacets(
        IEnumerable<ParsedAssignment> assignments,
        Dictionary<string, string> activeFilters)
    {
        var assignmentList = assignments.ToList();
        if (assignmentList.Count == 0)
        {
            return Enumerable.Empty<FacetAxis>();
        }

        // Discover all axis names and their possible values
        var axisValues = new Dictionary<string, HashSet<string>>();
        foreach (var a in assignmentList)
        {
            foreach (var kvp in a.Cell)
            {
                if (!axisValues.TryGetValue(kvp.Key, out var values))
                {
                    values = new HashSet<string>();
                    axisValues[kvp.Key] = values;
                }
                values.Add(kvp.Value);
            }
        }

        var axes = new List<FacetAxis>();
        foreach (var (axisName, allValues) in axisValues.OrderBy(kv => kv.Key))
        {
            // For this axis, filter by ALL OTHER active filters (not this axis)
            var otherFilters = activeFilters
                .Where(f => f.Key != axisName)
                .ToDictionary(f => f.Key, f => f.Value);

            var filtered = FilterAssignments(assignmentList, otherFilters);

            var counts = allValues.ToDictionary(v => v, _ => 0);
            foreach (var a in filtered)
            {
                if (a.Cell.TryGetValue(axisName, out var val) && counts.ContainsKey(val))
                {
                    counts[val]++;
                }
            }

            axes.Add(new FacetAxis(
                axisName,
                counts.OrderByDescending(c => c.Value)
                    .Select(c => new FacetValue(c.Key, c.Value))));
        }

        return axes;
    }

    public static IEnumerable<ParsedAssignment> FilterAssignments(
        IEnumerable<ParsedAssignment> assignments,
        Dictionary<string, string> filters)
    {
        if (filters.Count == 0)
        {
            return assignments;
        }

        return assignments.Where(a =>
            filters.All(f =>
                a.Cell.TryGetValue(f.Key, out var val) && val == f.Value));
    }
}

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
