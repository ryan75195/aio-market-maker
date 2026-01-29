using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using AIOMarketMaker.Etl.Orchestrators;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Etl.Triggers;

public class StartScrapeTrigger
{
    private readonly ILogger<StartScrapeTrigger> _logger;
    private readonly EtlDbContext _dbContext;

    public StartScrapeTrigger(ILogger<StartScrapeTrigger> logger, EtlDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    [Function("StartScrape")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "scrape/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        _logger.LogInformation("Scrape start trigger fired at {Time}", DateTime.UtcNow);

        // Parse optional request body
        StartScrapeRequest? scrapeRequest = null;
        var requestBody = await req.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(requestBody))
        {
            try
            {
                scrapeRequest = JsonSerializer.Deserialize<StartScrapeRequest>(requestBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse request body, using defaults");
            }
        }

        // Get all enabled jobs
        var enabledJobs = await _dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .Select(j => new { j.Id, j.SearchTerm })
            .ToListAsync();

        if (enabledJobs.Count == 0)
        {
            _logger.LogInformation("No enabled jobs found");
            var noJobsResponse = req.CreateResponse(HttpStatusCode.OK);
            await noJobsResponse.WriteAsJsonAsync(new { message = "No enabled jobs", runs = Array.Empty<object>() });
            return noJobsResponse;
        }

        // Create a ScrapeRun for each enabled job
        var startedRuns = new List<object>();
        foreach (var job in enabledJobs)
        {
            var scrapeRun = new ScrapeRun
            {
                JobId = job.Id,
                TriggerType = "Manual",
                StartedUtc = DateTime.UtcNow,
                Status = "Running"
            };
            _dbContext.ScrapeRuns.Add(scrapeRun);
            await _dbContext.SaveChangesAsync();

            // Start orchestration for this job
            var instanceId = $"scrape-run-{scrapeRun.Id}";
            var orchestratorInput = new JobOrchestratorInput(
                job.Id,
                instanceId,
                scrapeRequest?.MaxSoldListings,
                scrapeRequest?.MaxActiveListings,
                scrapeRequest?.LookbackDays);

            await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(JobOrchestrator), orchestratorInput,
                new StartOrchestrationOptions { InstanceId = instanceId });

            scrapeRun.InstanceId = instanceId;
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Started JobOrchestrator {InstanceId} for job {JobId}: {SearchTerm}",
                instanceId, job.Id, job.SearchTerm);

            startedRuns.Add(new { runId = scrapeRun.Id, instanceId, jobId = job.Id, searchTerm = job.SearchTerm });
        }

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { runs = startedRuns });
        return response;
    }

    /// <summary>
    /// HTTP trigger to terminate a specific orchestration.
    /// DELETE /api/orchestration/{instanceId}
    /// </summary>
    [Function("TerminateOrchestration")]
    public async Task<HttpResponseData> TerminateOrchestration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "orchestration/{instanceId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        string instanceId)
    {
        _logger.LogInformation("Terminating orchestration: {InstanceId}", instanceId);

        try
        {
            await client.TerminateInstanceAsync(instanceId, "Terminated via API");

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { instanceId, status = "Terminated" });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to terminate orchestration: {InstanceId}", instanceId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// HTTP trigger to purge all orchestrations (running and completed).
    /// POST /api/orchestration/purge
    /// </summary>
    [Function("PurgeOrchestrations")]
    public async Task<HttpResponseData> PurgeOrchestrations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orchestration/purge")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        _logger.LogInformation("Purging orchestrations...");

        try
        {
            // Purge ALL orchestration statuses at once
            var allStatuses = new[]
            {
                OrchestrationRuntimeStatus.Completed,
                OrchestrationRuntimeStatus.Failed,
                OrchestrationRuntimeStatus.Terminated,
                OrchestrationRuntimeStatus.Running,
                OrchestrationRuntimeStatus.Pending,
                OrchestrationRuntimeStatus.Suspended
            };

            var purgeResult = await client.PurgeAllInstancesAsync(
                new PurgeInstancesFilter(
                    CreatedFrom: DateTime.UtcNow.AddDays(-7),
                    CreatedTo: DateTime.UtcNow,
                    Statuses: allStatuses));

            _logger.LogInformation("Purge completed: {Purged} instances purged",
                purgeResult.PurgedInstanceCount);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                purged = purgeResult.PurgedInstanceCount,
                status = "Purge completed"
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to purge orchestrations");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }
}

public record StartScrapeRequest(int? MaxSoldListings, int? MaxActiveListings, int? LookbackDays);
