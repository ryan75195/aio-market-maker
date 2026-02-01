using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Azure.Storage.Queues;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Triggers;

/// <summary>
/// HTTP and timer triggers for initiating scrape jobs.
/// Uses fire-and-forget pattern - enqueues jobs to scrape-jobs queue and returns immediately.
/// Actual scrape processing is handled by ScrapeJobQueueTrigger.
/// </summary>
public class SimplifiedScrapeTrigger
{
    private readonly ILogger<SimplifiedScrapeTrigger> _logger;
    private readonly EtlDbContext _dbContext;
    private readonly QueueClient _jobQueueClient;

    public SimplifiedScrapeTrigger(
        ILogger<SimplifiedScrapeTrigger> logger,
        EtlDbContext dbContext,
        QueueServiceClient queueService)
    {
        _logger = logger;
        _dbContext = dbContext;
        _jobQueueClient = queueService.GetQueueClient("scrape-jobs");
    }

    /// <summary>
    /// Timer trigger that runs nightly at 2 AM UTC to scrape all enabled jobs.
    /// Uses fire-and-forget queue pattern - enqueues jobs and returns immediately.
    /// </summary>
    [Function("NightlyScrape")]
    public async Task RunNightly([TimerTrigger("0 0 2 * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Nightly scrape trigger fired at {Time}", DateTime.UtcNow);

        // Get all enabled jobs
        var enabledJobs = await _dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .Select(j => new { j.Id, j.SearchTerm })
            .ToListAsync();

        if (enabledJobs.Count == 0)
        {
            _logger.LogInformation("No enabled jobs found for nightly scrape");
            return;
        }

        _logger.LogInformation("Found {Count} enabled jobs for nightly scrape", enabledJobs.Count);

        // Enqueue all jobs (fire-and-forget)
        foreach (var job in enabledJobs)
        {
            var scrapeRun = new ScrapeRun
            {
                JobId = job.Id,
                Status = "Queued",
                CurrentPhase = "Queued",
                TriggerType = "Nightly",
                StartedUtc = DateTime.UtcNow,
                InstanceId = Guid.NewGuid().ToString()
            };
            _dbContext.ScrapeRuns.Add(scrapeRun);
            await _dbContext.SaveChangesAsync();

            var message = new ScrapeJobMessage(scrapeRun.Id, job.Id, job.SearchTerm, "Nightly");
            var messageJson = JsonSerializer.Serialize(message);
            await _jobQueueClient.SendMessageAsync(messageJson);

            _logger.LogInformation("Enqueued nightly scrape for {SearchTerm} (RunId: {RunId})",
                job.SearchTerm, scrapeRun.Id);
        }

        _logger.LogInformation("Enqueued {Count} jobs for nightly scrape", enabledJobs.Count);
    }

    /// <summary>
    /// HTTP trigger for manual scrape invocation.
    /// Creates ScrapeRun records and enqueues jobs for background processing.
    /// Returns immediately with run IDs.
    /// </summary>
    [Function("ManualScrape")]
    public async Task<HttpResponseData> RunManual(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "scrape/start")] HttpRequestData req)
    {
        _logger.LogInformation("Manual scrape trigger fired at {Time}", DateTime.UtcNow);

        // Parse optional request body
        ManualScrapeRequest? scrapeRequest = null;
        var requestBody = await req.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(requestBody))
        {
            try
            {
                scrapeRequest = JsonSerializer.Deserialize<ManualScrapeRequest>(requestBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse request body, using defaults");
            }
        }

        // Determine which jobs to run
        IEnumerable<(int Id, string SearchTerm)> jobsToRun;

        if (scrapeRequest?.JobId != null)
        {
            // Run specific job
            var job = await _dbContext.ScrapeJobs
                .Where(j => j.Id == scrapeRequest.JobId)
                .Select(j => new { j.Id, j.SearchTerm })
                .FirstOrDefaultAsync();

            if (job == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteAsJsonAsync(new { error = $"Job {scrapeRequest.JobId} not found" });
                return notFoundResponse;
            }

            jobsToRun = new[] { (job.Id, job.SearchTerm) };
        }
        else
        {
            // Run all enabled jobs
            var enabledJobs = await _dbContext.ScrapeJobs
                .Where(j => j.IsEnabled)
                .Select(j => new { j.Id, j.SearchTerm })
                .ToListAsync();

            if (enabledJobs.Count == 0)
            {
                _logger.LogInformation("No enabled jobs found");
                var noJobsResponse = req.CreateResponse(HttpStatusCode.OK);
                await noJobsResponse.WriteAsJsonAsync(new { message = "No enabled jobs", results = Array.Empty<object>() });
                return noJobsResponse;
            }

            jobsToRun = enabledJobs.Select(j => (j.Id, j.SearchTerm));
        }

        // Create ScrapeRuns and enqueue jobs (fire-and-forget)
        var results = new List<object>();
        int? firstRunId = null;
        string? firstInstanceId = null;

        foreach (var (jobId, searchTerm) in jobsToRun)
        {
            // Create ScrapeRun with Queued status
            var scrapeRun = new ScrapeRun
            {
                JobId = jobId,
                Status = "Queued",
                CurrentPhase = "Queued",
                TriggerType = "Manual",
                StartedUtc = DateTime.UtcNow,
                InstanceId = Guid.NewGuid().ToString()
            };
            _dbContext.ScrapeRuns.Add(scrapeRun);
            await _dbContext.SaveChangesAsync();

            // Enqueue job message
            var message = new ScrapeJobMessage(scrapeRun.Id, jobId, searchTerm, "Manual");
            var messageJson = JsonSerializer.Serialize(message);
            await _jobQueueClient.SendMessageAsync(messageJson);

            _logger.LogInformation("Enqueued scrape job for {SearchTerm} (RunId: {RunId})", searchTerm, scrapeRun.Id);

            firstRunId ??= scrapeRun.Id;
            firstInstanceId ??= scrapeRun.InstanceId;

            results.Add(new { jobId, searchTerm, runId = scrapeRun.Id, status = "Queued" });
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            instanceId = firstInstanceId ?? Guid.NewGuid().ToString(),
            runId = firstRunId ?? 0,
            results
        });
        return response;
    }
}

public record ManualScrapeRequest(int? JobId);
