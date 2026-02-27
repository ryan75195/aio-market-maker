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

public record BatchSummaryResponse(
    Guid BatchId, string? TriggerType,
    DateTime StartedUtc, DateTime? CompletedUtc,
    string Status, string? BatchPhase, int RunCount,
    int TotalListingsFound, int TotalListingsProcessed,
    int TotalListingsAddedActive, int TotalListingsAddedSold,
    int TotalListingsUpdated, int TotalListingsSkipped,
    int TotalListingsFailed, int TotalListingsFilteredPreQueue,
    int SearchedJobCount, int CompletedJobCount, int FailedJobCount,
    DateTime? SearchCompletedUtc, DateTime? ProcessingStartedUtc);

public record BatchDetailResponse(
    Guid BatchId, string? TriggerType,
    DateTime StartedUtc, DateTime? CompletedUtc,
    string Status, string? BatchPhase, int RunCount,
    int TotalListingsFound, int TotalListingsProcessed,
    DateTime? SearchCompletedUtc, DateTime? ProcessingStartedUtc,
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
        group.MapGet("/batches/{batchId:guid}", GetBatchDetail);
    }

    private static async Task<IResult> GetBatches(EtlDbContext db, int page = 1, int pageSize = 20)
    {
        if (page < 1) { page = 1; }
        if (pageSize < 1) { pageSize = 20; }
        if (pageSize > 100) { pageSize = 100; }

        // Server-side aggregation — no runs returned, just totals per batch
        var batchQuery = db.ScrapeRuns
            .Where(r => r.BatchId != null)
            .GroupBy(r => r.BatchId!.Value)
            .Select(g => new
            {
                BatchId = g.Key,
                TriggerType = g.Min(r => r.TriggerType),
                StartedUtc = g.Min(r => r.StartedUtc),
                CompletedUtc = g.All(r => r.CompletedUtc != null) ? g.Max(r => r.CompletedUtc) : (DateTime?)null,
                BatchPhase = g.Min(r => r.BatchPhase),
                RunCount = g.Count(),
                TotalListingsFound = g.Sum(r => r.TotalListingsFound),
                TotalListingsProcessed = g.Sum(r => r.ListingsProcessed),
                TotalListingsAddedActive = g.Sum(r => r.ListingsAddedActive),
                TotalListingsAddedSold = g.Sum(r => r.ListingsAddedSold),
                TotalListingsUpdated = g.Sum(r => r.ListingsUpdated),
                TotalListingsSkipped = g.Sum(r => r.ListingsSkipped),
                TotalListingsFailed = g.Sum(r => r.ListingsFailed),
                TotalListingsFilteredPreQueue = g.Sum(r => r.ListingsFilteredPreQueue),
                SearchedJobCount = g.Count(r => r.TotalListingsFound > 0),
                CompletedJobCount = g.Count(r => r.Status == "Completed"),
                FailedJobCount = g.Count(r => r.Status == "Failed"),
                SearchCompletedUtc = g.Min(r => r.SearchCompletedUtc),
                ProcessingStartedUtc = g.Min(r => r.ProcessingStartedUtc),
                Statuses = g.Select(r => r.Status).ToList()
            });

        var totalCount = await batchQuery.CountAsync();
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        var batchData = await batchQuery
            .OrderByDescending(b => b.StartedUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var batches = batchData.Select(b => new BatchSummaryResponse(
            b.BatchId, b.TriggerType,
            b.StartedUtc, b.CompletedUtc,
            BatchStatusDeriver.Derive(b.Statuses),
            b.BatchPhase, b.RunCount,
            b.TotalListingsFound, b.TotalListingsProcessed,
            b.TotalListingsAddedActive, b.TotalListingsAddedSold,
            b.TotalListingsUpdated, b.TotalListingsSkipped,
            b.TotalListingsFailed, b.TotalListingsFilteredPreQueue,
            b.SearchedJobCount, b.CompletedJobCount, b.FailedJobCount,
            b.SearchCompletedUtc, b.ProcessingStartedUtc));

        return Results.Ok(new { items = batches, totalCount, totalPages, page, pageSize });
    }

    private static async Task<IResult> GetBatchDetail(EtlDbContext db, Guid batchId)
    {
        var runs = await db.ScrapeRuns
            .Where(r => r.BatchId == batchId)
            .ToListAsync();

        if (runs.Count == 0)
        {
            return Results.NotFound();
        }

        var jobIds = runs.Where(r => r.JobId != null).Select(r => r.JobId!.Value).Distinct().ToList();
        var jobNames = await db.ScrapeJobs
            .Where(j => jobIds.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, j => j.SearchTerm);

        var runIds = runs.Select(r => r.Id).ToList();
        var issueCounts = await db.ScrapeRunIssues
            .Where(i => runIds.Contains(i.ScrapeRunId))
            .GroupBy(i => i.ScrapeRunId)
            .Select(g => new IssueCountEntry(g.Key, g.Count()))
            .ToDictionaryAsync(x => x.ScrapeRunId, x => x.Count);

        var runStatuses = runs.Select(r => r.Status).ToList();
        var status = BatchStatusDeriver.Derive(runStatuses);

        var allCompleted = runs.All(r => r.CompletedUtc != null);
        DateTime? completedUtc = allCompleted ? runs.Max(r => r.CompletedUtc) : null;

        var batchRunResponses = runs.Select(r => new BatchRunResponse(
            r.Id, r.JobId,
            r.JobId != null && jobNames.TryGetValue(r.JobId.Value, out var name) ? name : null,
            r.Status, r.CurrentPhase,
            r.TotalListingsFound, r.ListingsProcessed,
            r.ListingsAddedActive, r.ListingsAddedSold,
            r.ListingsUpdated, r.ListingsSkipped,
            r.ListingsFailed, r.ListingsFilteredPreQueue,
            issueCounts.GetValueOrDefault(r.Id, 0)));

        return Results.Ok(new BatchDetailResponse(
            batchId,
            runs.FirstOrDefault()?.TriggerType,
            runs.Min(r => r.StartedUtc),
            completedUtc,
            status,
            runs.FirstOrDefault()?.BatchPhase,
            runs.Count,
            runs.Sum(r => r.TotalListingsFound),
            runs.Sum(r => r.ListingsProcessed),
            runs.FirstOrDefault()?.SearchCompletedUtc,
            runs.FirstOrDefault()?.ProcessingStartedUtc,
            batchRunResponses));
    }
}
