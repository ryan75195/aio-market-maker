using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Activities;

namespace AIOMarketMaker.Functions.Functions;

public class ScrapeOrchestrator
{
    /// <summary>
    /// Orchestrator function that coordinates scrape job execution.
    /// Uses fan-out/fan-in pattern to process jobs in parallel.
    /// </summary>
    [Function(nameof(ScrapeOrchestrator))]
    public async Task<OrchestratorResult> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<ScrapeOrchestrator>();
        var startTime = context.CurrentUtcDateTime;

        logger.LogInformation("Starting scrape orchestration at {Time}", startTime);

        // Get all enabled jobs
        var jobs = await context.CallActivityAsync<List<ScrapeJobInfo>>(
            nameof(GetEnabledJobsActivity));

        if (jobs.Count == 0)
        {
            logger.LogInformation("No enabled jobs found");
            return new OrchestratorResult(0, 0, 0, TimeSpan.Zero, new List<string>());
        }

        logger.LogInformation("Found {Count} enabled jobs to process", jobs.Count);

        // Fan-out: process each job using sub-orchestrations
        // This allows each job to break down into smaller activities that won't timeout
        var tasks = jobs.Select(job =>
            context.CallSubOrchestratorAsync<JobResult>(
                nameof(JobOrchestrator),
                job.Id));

        var results = await Task.WhenAll(tasks);

        // Fan-in: aggregate results
        var succeeded = results.Count(r => r.Success);
        var failed = results.Count(r => !r.Success);
        var totalListings = results.Sum(r => r.ListingsFound);
        var duration = context.CurrentUtcDateTime - startTime;

        // Log errors from failed jobs
        foreach (var result in results.Where(r => !r.Success))
        {
            logger.LogError("Job {JobId} failed: {Error}", result.JobId, result.Error ?? "Unknown error");
        }

        logger.LogInformation(
            "Orchestration complete: {Succeeded}/{Total} jobs succeeded, {Listings} listings found, duration: {Duration}",
            succeeded, jobs.Count, totalListings, duration);

        var errors = results.Where(r => !r.Success)
            .Select(r => $"Job {r.JobId}: {r.Error ?? "Unknown"}")
            .ToList();

        return new OrchestratorResult(succeeded, failed, totalListings, duration, errors);
    }
}

public record OrchestratorResult(
    int SucceededJobs,
    int FailedJobs,
    int TotalListingsFound,
    TimeSpan Duration,
    List<string> Errors);

public record ScrapeJobInfo(int Id, string SearchTerm);

public record JobResult(int JobId, bool Success, int ListingsFound, string? Error);
