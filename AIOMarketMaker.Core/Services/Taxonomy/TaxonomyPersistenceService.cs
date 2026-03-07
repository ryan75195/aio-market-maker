using System.Text.Json;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Core.Services.Taxonomy;

public record PersistedTaxonomyRun(int RunId, int AxisCount, int AssignedListings, double CoveragePercent);

public interface ITaxonomyPersistenceService
{
    Task<PersistedTaxonomyRun> Save(
        int scrapeJobId,
        TaxonomyResult result,
        IEnumerable<int> listingIds,
        int durationMs,
        CancellationToken ct = default);
}

public class TaxonomyPersistenceService : ITaxonomyPersistenceService
{
    private const int AssignmentBatchSize = 500;

    private readonly EtlDbContext _dbContext;

    public TaxonomyPersistenceService(EtlDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PersistedTaxonomyRun> Save(
        int scrapeJobId,
        TaxonomyResult result,
        IEnumerable<int> listingIds,
        int durationMs,
        CancellationToken ct = default)
    {
        var listingIdList = listingIds.ToList();
        var assignmentsList = result.Assignments.ToList();
        var axesList = result.Axes.ToList();

        // Delete previous taxonomy for this job (cascade handles children)
        var existing = await _dbContext.TaxonomyRuns
            .Where(r => r.ScrapeJobId == scrapeJobId)
            .ToListAsync(ct);
        _dbContext.TaxonomyRuns.RemoveRange(existing);
        await _dbContext.SaveChangesAsync(ct);

        var assignedCount = assignmentsList.Count(a => a.Cell.Count > 0);

        var run = new TaxonomyRun
        {
            ScrapeJobId = scrapeJobId,
            CoveragePercent = result.CoveragePercent,
            ConflictPercent = result.ConflictPercent,
            TotalListings = listingIdList.Count,
            AssignedListings = assignedCount,
            AxisCount = axesList.Count,
            DurationMs = durationMs,
            CreatedUtc = DateTime.UtcNow
        };
        _dbContext.TaxonomyRuns.Add(run);
        await _dbContext.SaveChangesAsync(ct);

        // Save axes and values
        for (var axisIdx = 0; axisIdx < axesList.Count; axisIdx++)
        {
            var axis = axesList[axisIdx];
            var dbAxis = new TaxonomyAxis
            {
                TaxonomyRunId = run.Id,
                Name = axis.Name,
                SortOrder = axisIdx
            };
            _dbContext.TaxonomyAxes.Add(dbAxis);
            await _dbContext.SaveChangesAsync(ct);

            var values = axis.Values.ToList();
            for (var valIdx = 0; valIdx < values.Count; valIdx++)
            {
                var value = values[valIdx];
                var ngramsData = value.Ngrams.Select(n => new
                {
                    n.Canonical,
                    n.Forms,
                    n.Frequency
                });
                _dbContext.TaxonomyAxisValues.Add(new TaxonomyAxisValue
                {
                    TaxonomyAxisId = dbAxis.Id,
                    Label = value.Label,
                    NgramsJson = JsonSerializer.Serialize(ngramsData),
                    SortOrder = valIdx
                });
            }
        }

        await _dbContext.SaveChangesAsync(ct);

        // Save assignments in batches to avoid tracker bloat
        for (var i = 0; i < assignmentsList.Count; i += AssignmentBatchSize)
        {
            var batchEnd = Math.Min(i + AssignmentBatchSize, assignmentsList.Count);
            for (var j = i; j < batchEnd; j++)
            {
                var assignment = assignmentsList[j];
                if (assignment.ListingIndex >= listingIdList.Count)
                {
                    continue;
                }

                _dbContext.TaxonomyListingAssignments.Add(new TaxonomyListingAssignment
                {
                    TaxonomyRunId = run.Id,
                    ListingId = listingIdList[assignment.ListingIndex],
                    CellJson = JsonSerializer.Serialize(assignment.Cell),
                    HasConflict = assignment.HasConflict
                });
            }

            await _dbContext.SaveChangesAsync(ct);
            _dbContext.ChangeTracker.Clear();
        }

        return new PersistedTaxonomyRun(run.Id, axesList.Count, assignedCount, result.CoveragePercent);
    }
}
