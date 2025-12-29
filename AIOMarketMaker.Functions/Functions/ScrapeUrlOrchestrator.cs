using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Functions.Activities;

namespace AIOMarketMaker.Functions.Functions;

/// <summary>
/// Sub-orchestrator that handles scraping a single URL.
/// Uses durable timers for polling instead of blocking waits in activities.
/// This prevents activity timeout issues on Consumption plan.
/// </summary>
public class ScrapeUrlOrchestrator
{
    private const int PollIntervalSeconds = 10;
    private const int MaxPollAttempts = 120; // 20 minutes max wait

    [Function(nameof(ScrapeUrlOrchestrator))]
    public async Task<string?> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<ScrapeUrlOrchestrator>();
        var url = context.GetInput<string>();

        logger.LogInformation("Starting scrape for URL: {Url}", url);

        // Step 1: Submit the scrape job (returns immediately with jobId)
        var jobId = await context.CallActivityAsync<string>(
            nameof(SubmitScrapeJobActivity), url);

        logger.LogDebug("Submitted job {JobId} for URL: {Url}", jobId, url);

        // Step 2: Poll for completion using durable timers
        // This is the key difference - orchestrator sleeps (free) instead of activity blocking (costs $)
        ScrapeJobStatusResult? status = null;
        for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            status = await context.CallActivityAsync<ScrapeJobStatusResult>(
                nameof(CheckScrapeJobStatusActivity), jobId);

            if (status.IsComplete)
            {
                logger.LogDebug("Job {JobId} completed with status: {Status}", jobId, status.Status);
                break;
            }

            // Durable timer - orchestrator checkpoints and resumes after delay
            // No compute resources consumed during this wait
            await context.CreateTimer(
                context.CurrentUtcDateTime.AddSeconds(PollIntervalSeconds),
                CancellationToken.None);
        }

        // Step 3: Handle completion status
        if (status == null || !status.IsComplete)
        {
            logger.LogError("Job {JobId} timed out after {MaxAttempts} attempts", jobId, MaxPollAttempts);
            return null;
        }

        if (status.Status == "Failure")
        {
            logger.LogError("Job {JobId} failed", jobId);
            return null;
        }

        // Step 4: Fetch the scraped HTML from blob storage
        var html = await context.CallActivityAsync<string?>(
            nameof(GetScrapedHtmlActivity),
            new GetScrapedHtmlInput(jobId));

        if (string.IsNullOrEmpty(html))
        {
            logger.LogWarning("No HTML content retrieved for job {JobId}", jobId);
            return null;
        }

        logger.LogInformation("Successfully scraped URL: {Url} ({Length} chars)", url, html.Length);

        return html;
    }
}
