using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Orchestrators;

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

        // Get optional input for runtime overrides
        var input = context.GetInput<ScrapeOrchestratorInput>();
        if (input?.MaxSoldListings.HasValue == true || input?.MaxActiveListings.HasValue == true || input?.LookbackDays.HasValue == true)
        {
            logger.LogInformation("Scrape with overrides: MaxSold={MaxSold}, MaxActive={MaxActive}, LookbackDays={Days}",
                input?.MaxSoldListings, input?.MaxActiveListings, input?.LookbackDays);
        }

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

        // Process jobs sequentially to avoid creating too many concurrent sub-orchestrators
        // Each job can create many FetchListingOrchestrators which create ScrapeUrlOrchestrators
        // Running jobs in parallel would overwhelm the system with thousands of orchestrations
        var results = new List<JobResult>();
        foreach (var job in jobs)
        {
            logger.LogInformation("Starting job {JobId}: {SearchTerm}", job.Id, job.SearchTerm);

            try
            {
                var result = await context.CallSubOrchestratorAsync<JobResult>(
                    nameof(JobOrchestrator),
                    new JobOrchestratorInput(job.Id, context.InstanceId, input?.MaxSoldListings, input?.MaxActiveListings, input?.LookbackDays));
                results.Add(result);
                logger.LogInformation("Job {JobId} completed: {Success}, {ListingsFound} listings",
                    job.Id, result.Success, result.ListingsFound);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job {JobId} failed with exception", job.Id);
                results.Add(new JobResult(job.Id, false, 0, ex.Message));
            }
        }

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

        // Update the ScrapeRun record with results
        var instanceId = context.InstanceId;
        await context.CallActivityAsync(
            nameof(UpdateScrapeRunActivity),
            new UpdateScrapeRunInput(
                instanceId,
                failed == 0,
                errors.Count > 0 ? string.Join("; ", errors) : null));

        return new OrchestratorResult(succeeded, failed, totalListings, duration, errors);
    }
}
