using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Api.Endpoints;

public record HistoryRunResponse(
    int Id, string? InstanceId, int? JobId, string? JobSearchTerm,
    string? TriggerType, DateTime? StartedUtc, DateTime? CompletedUtc,
    string? Status, int ListingsAddedActive, int ListingsAddedSold,
    int ListingsUpdated, int ListingsSkipped, int ListingsFailed,
    int ListingsFilteredPreQueue, int TotalListingsFound,
    int ListingsProcessed, string? CurrentPhase, string? ErrorMessage,
    int IssueCount);

public record HistoryIssueResponse(
    string ListingId, string Status,
    string? IssueType, string? ErrorMessage, DateTime CreatedUtc);

public record IssueCountEntry(int ScrapeRunId, int Count);

public record HistoryRunProjection(
    int Id, string? InstanceId, int? JobId, string? JobSearchTerm,
    string? TriggerType, DateTime? StartedUtc, DateTime? CompletedUtc,
    string? Status, int ListingsAddedActive, int ListingsAddedSold,
    int ListingsUpdated, int ListingsSkipped, int ListingsFailed,
    int ListingsFilteredPreQueue, int TotalListingsFound,
    int ListingsProcessed, string? CurrentPhase, string? ErrorMessage);

public static class HistoryEndpoints
{
    public static void MapHistoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/history");
        group.MapGet("/", GetHistory);
        group.MapGet("/{runId:int}/issues", GetHistoryIssues);
    }

    private static async Task<IResult> GetHistory(EtlDbContext db, int page = 1, int pageSize = 50)
    {
        if (page < 1) { page = 1; }
        if (pageSize < 1) { pageSize = 50; }
        if (pageSize > 200) { pageSize = 200; }

        var baseQuery = db.ScrapeRuns
            .OrderBy(r =>
                r.Status == "Searching" || r.Status == "Indexing" || r.Status == "Running" || r.Status == "Processing" ? 0 :
                r.Status == "Queued" ? 1 :
                2)
            .ThenByDescending(r => r.StartedUtc);

        var totalCount = await db.ScrapeRuns.CountAsync();
        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        var runs = await baseQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new HistoryRunProjection(
                r.Id, r.InstanceId, r.JobId,
                r.JobId != null
                    ? db.ScrapeJobs.Where(j => j.Id == r.JobId).Select(j => j.SearchTerm).FirstOrDefault()
                    : null,
                r.TriggerType, r.StartedUtc, r.CompletedUtc,
                r.Status, r.ListingsAddedActive, r.ListingsAddedSold,
                r.ListingsUpdated, r.ListingsSkipped, r.ListingsFailed,
                r.ListingsFilteredPreQueue, r.TotalListingsFound,
                r.ListingsProcessed, r.CurrentPhase, r.ErrorMessage))
            .ToListAsync();

        var runIds = runs.Select(r => r.Id).ToList();

        // Issue counts from ScrapeRunIssues
        var issueCounts = await db.ScrapeRunIssues
            .Where(i => runIds.Contains(i.ScrapeRunId))
            .GroupBy(i => i.ScrapeRunId)
            .Select(g => new IssueCountEntry(g.Key, g.Count()))
            .ToDictionaryAsync(x => x.ScrapeRunId, x => x.Count);

        var runsWithIssues = runs.Select(r => new HistoryRunResponse(
            r.Id, r.InstanceId, r.JobId, r.JobSearchTerm,
            r.TriggerType, r.StartedUtc, r.CompletedUtc,
            r.Status, r.ListingsAddedActive, r.ListingsAddedSold,
            r.ListingsUpdated, r.ListingsSkipped, r.ListingsFailed,
            r.ListingsFilteredPreQueue, r.TotalListingsFound,
            r.ListingsProcessed, r.CurrentPhase, r.ErrorMessage,
            issueCounts.GetValueOrDefault(r.Id, 0)));

        return Results.Ok(new { items = runsWithIssues, totalCount, totalPages, page, pageSize });
    }

    private static async Task<IResult> GetHistoryIssues(int runId, EtlDbContext db)
    {
        var runExists = await db.ScrapeRuns.AnyAsync(r => r.Id == runId);
        if (!runExists)
        {
            return Results.NotFound(new ErrorResponse($"Run {runId} not found"));
        }

        // Query ScrapeRunIssues
        var issues = await db.ScrapeRunIssues
            .Where(i => i.ScrapeRunId == runId)
            .OrderBy(i => i.CreatedUtc)
            .Select(i => new HistoryIssueResponse(
                i.ListingId,
                "Failed",
                i.IssueType,
                i.ErrorMessage,
                i.CreatedUtc))
            .ToListAsync();

        return Results.Ok(issues);
    }
}
