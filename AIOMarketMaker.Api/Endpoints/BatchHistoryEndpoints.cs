using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Api.Endpoints;

public record BatchRunResponse(
    int Id, int? JobId, string? JobSearchTerm,
    string? Status, string? CurrentPhase,
    int TotalListingsFound, int ListingsProcessed,
    int ListingsAddedActive, int ListingsAddedSold,
    int ListingsUpdated, int ListingsSkipped,
    int ListingsFailed, int ListingsFilteredPreQueue,
    int IssueCount);

public record BatchResponse(
    Guid BatchId, string? TriggerType,
    DateTime StartedUtc, DateTime? CompletedUtc,
    string Status, int RunCount,
    int TotalListingsFound, int TotalListingsProcessed,
    IEnumerable<BatchRunResponse> Runs);

public static class BatchStatusDeriver
{
    private static readonly HashSet<string> ActiveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Running", "Searching", "Indexing", "Processing"
    };

    public static string Derive(IEnumerable<string> runStatuses)
    {
        var statuses = runStatuses.ToList();

        if (statuses.Any(s => ActiveStatuses.Contains(s)))
        {
            return "Running";
        }

        if (statuses.All(s => string.Equals(s, "Queued", StringComparison.OrdinalIgnoreCase)))
        {
            return "Queued";
        }

        if (statuses.Any(s => string.Equals(s, "Queued", StringComparison.OrdinalIgnoreCase)))
        {
            return "Running";
        }

        if (statuses.All(s => string.Equals(s, "Completed", StringComparison.OrdinalIgnoreCase)))
        {
            return "Completed";
        }

        if (statuses.All(s => string.Equals(s, "Failed", StringComparison.OrdinalIgnoreCase)))
        {
            return "Failed";
        }

        return "PartialFailure";
    }
}

public static class BatchHistoryEndpoints
{
    public static void MapBatchHistoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/history");
        group.MapGet("/batches", GetBatches);
    }

    private static async Task<IResult> GetBatches(EtlDbContext db, int page = 1, int pageSize = 20)
    {
        if (page < 1) { page = 1; }
        if (pageSize < 1) { pageSize = 20; }
        if (pageSize > 100) { pageSize = 100; }

        // Get distinct batch IDs with their earliest StartedUtc for ordering
        var batchQuery = db.ScrapeRuns
            .Where(r => r.BatchId != null)
            .GroupBy(r => r.BatchId!.Value)
            .Select(g => new
            {
                BatchId = g.Key,
                EarliestStartedUtc = g.Min(r => r.StartedUtc)
            });

        var totalCount = await batchQuery.CountAsync();
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        var batchIds = await batchQuery
            .OrderByDescending(b => b.EarliestStartedUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(b => b.BatchId)
            .ToListAsync();

        // Load all runs for these batches
        var runs = await db.ScrapeRuns
            .Where(r => r.BatchId != null && batchIds.Contains(r.BatchId.Value))
            .ToListAsync();

        // Load job names
        var jobIds = runs
            .Where(r => r.JobId != null)
            .Select(r => r.JobId!.Value)
            .Distinct()
            .ToList();

        var jobNames = await db.ScrapeJobs
            .Where(j => jobIds.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, j => j.SearchTerm);

        // Load issue counts
        var runIds = runs.Select(r => r.Id).ToList();
        var issueCounts = await db.ScrapeRunIssues
            .Where(i => runIds.Contains(i.ScrapeRunId))
            .GroupBy(i => i.ScrapeRunId)
            .Select(g => new IssueCountEntry(g.Key, g.Count()))
            .ToDictionaryAsync(x => x.ScrapeRunId, x => x.Count);

        // Build batch responses
        var batches = batchIds.Select(batchId =>
        {
            var batchRuns = runs.Where(r => r.BatchId == batchId).ToList();
            var runStatuses = batchRuns.Select(r => r.Status).ToList();
            var status = BatchStatusDeriver.Derive(runStatuses);

            var allCompleted = batchRuns.All(r => r.CompletedUtc != null);
            DateTime? completedUtc = allCompleted && batchRuns.Count > 0
                ? batchRuns.Max(r => r.CompletedUtc)
                : null;

            var batchRunResponses = batchRuns.Select(r => new BatchRunResponse(
                r.Id, r.JobId,
                r.JobId != null && jobNames.TryGetValue(r.JobId.Value, out var name) ? name : null,
                r.Status, r.CurrentPhase,
                r.TotalListingsFound, r.ListingsProcessed,
                r.ListingsAddedActive, r.ListingsAddedSold,
                r.ListingsUpdated, r.ListingsSkipped,
                r.ListingsFailed, r.ListingsFilteredPreQueue,
                issueCounts.GetValueOrDefault(r.Id, 0)));

            return new BatchResponse(
                batchId,
                batchRuns.FirstOrDefault()?.TriggerType,
                batchRuns.Min(r => r.StartedUtc),
                completedUtc,
                status,
                batchRuns.Count,
                batchRuns.Sum(r => r.TotalListingsFound),
                batchRuns.Sum(r => r.ListingsProcessed),
                batchRunResponses);
        });

        return Results.Ok(new { items = batches, totalCount, totalPages, page, pageSize });
    }
}
