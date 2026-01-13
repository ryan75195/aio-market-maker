using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Functions.Activities;
using AIOMarketMaker.Functions.Contracts;

namespace AIOMarketMaker.Functions.Functions.Orchestrators;

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

        logger.LogInformation("ScrapeUrlOrchestrator: Starting for URL: {Url}", url);

        // Step 1: Submit the scrape job (returns immediately with jobId)
        string jobId;
        try
        {
            jobId = await context.CallActivityAsync<string>(
                nameof(SubmitScrapeJobActivity), url);
            logger.LogInformation("ScrapeUrlOrchestrator: Job {JobId} submitted for URL: {Url}", jobId, url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ScrapeUrlOrchestrator: Failed to submit job for URL: {Url}", url);
            return null;
        }

        // Step 2: Poll for completion using durable timers
        // This is the key difference - orchestrator sleeps (free) instead of activity blocking (costs $)
        ScrapeJobStatusResult? status = null;
        for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            try
            {
                status = await context.CallActivityAsync<ScrapeJobStatusResult>(
                    nameof(CheckScrapeJobStatusActivity), jobId);

                logger.LogInformation("ScrapeUrlOrchestrator: Job {JobId} attempt {Attempt} status: {Status} (isComplete: {IsComplete})",
                    jobId, attempt + 1, status.Status, status.IsComplete);

                if (status.IsComplete)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "ScrapeUrlOrchestrator: Failed to check status for job {JobId}", jobId);
                return null;
            }

            // Durable timer - orchestrator checkpoints and resumes after delay
            // No compute resources consumed during this wait
            logger.LogInformation("ScrapeUrlOrchestrator: Job {JobId} waiting {Seconds}s before next poll", jobId, PollIntervalSeconds);
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
