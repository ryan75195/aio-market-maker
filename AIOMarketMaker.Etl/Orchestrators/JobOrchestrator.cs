using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Orchestrators;

/// <summary>
/// Sub-orchestrator that handles a single scrape job.
/// Uses durable timers for polling instead of blocking waits in activities.
/// </summary>
public class JobOrchestrator
{
    [Function(nameof(JobOrchestrator))]
    public async Task<JobResult> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<JobOrchestrator>();
        var input = context.GetInput<JobOrchestratorInput>()!;
        var jobId = input.JobId;
        var scrapeInstanceId = input.ScrapeInstanceId;

        logger.LogInformation("Starting job orchestration for job {JobId}", jobId);

        try
        {
            // Step 1: Get job details (with runtime overrides if provided)
            var jobDetails = await context.CallActivityAsync<JobDetails>(
                nameof(GetJobDetailsActivity),
                new GetJobDetailsInput(jobId, input.MaxSoldListings, input.MaxActiveListings, input.LookbackDays));

            if (jobDetails == null)
            {
                return new JobResult(jobId, false, 0, $"Job {jobId} not found");
            }

            logger.LogInformation("Job {JobId}: '{SearchTerm}'", jobId, jobDetails.SearchTerm);

            var seenIds = new HashSet<string>();
            var allListingIds = new List<string>();
            var soldListingIds = new HashSet<string>();

            // Report progress: Searching
            await context.CallActivityAsync(
                nameof(UpdateScrapeRunProgressActivity),
                new UpdateProgressInput(scrapeInstanceId, CurrentPhase: "Searching"));

            // Step 2a: Search SOLD listings page by page until no results in date range
            logger.LogInformation("Job {JobId}: Searching sold listings...", jobId);
            for (int page = 1; ; page++)
            {
                var result = await SearchPageAsync(context, jobDetails.SearchTerm, page, true, jobDetails.LookbackDays);

                if (!result.Success || result.ListingIds.Count == 0)
                    break;

                var newIds = result.ListingIds.Where(id => seenIds.Add(id)).ToList();
                allListingIds.AddRange(newIds);
                foreach (var id in newIds) soldListingIds.Add(id);

                logger.LogInformation("Job {JobId}: Sold page {Page} found {Count} listings (total: {Total})",
                    jobId, page, newIds.Count, allListingIds.Count);

                if (newIds.Count == 0)
                    break;
            }

            // Step 2b: Detect and update Active→Sold transitions
            var statusUpdates = await DetectAndUpdateSoldListingsAsync(
                context, jobId, scrapeInstanceId, soldListingIds, jobDetails.MaxSoldListings, logger);

            // Phase stays as "Searching" - no need to distinguish sold/active
            // (phase already set above)

            // Step 2c: Search ACTIVE listings page by page until no results (limit 10000)
            const int activeItemLimit = 10000;
            logger.LogInformation("Job {JobId}: Searching active listings...", jobId);
            for (int page = 1; allListingIds.Count < activeItemLimit; page++)
            {
                var result = await SearchPageAsync(context, jobDetails.SearchTerm, page, false, null);

                if (!result.Success || result.ListingIds.Count == 0)
                    break;

                var newIds = result.ListingIds.Where(id => seenIds.Add(id)).ToList();
                allListingIds.AddRange(newIds);

                logger.LogInformation("Job {JobId}: Active page {Page} found {Count} listings (total: {Total})",
                    jobId, page, newIds.Count, allListingIds.Count);

                if (newIds.Count == 0)
                    break;
            }

            logger.LogInformation("Job {JobId}: Found {Count} unique listings from search ({SoldCount} sold)",
                jobId, allListingIds.Count, soldListingIds.Count);

            if (allListingIds.Count == 0)
            {
                await context.CallActivityAsync(nameof(UpdateJobTimestampActivity), jobId);
                await context.CallActivityAsync(
                    nameof(UpdateScrapeRunProgressActivity),
                    new UpdateProgressInput(scrapeInstanceId, CurrentPhase: "Completed"));
                return new JobResult(jobId, true, 0, null);
            }

            // Report progress: Filtering
            await context.CallActivityAsync(
                nameof(UpdateScrapeRunProgressActivity),
                new UpdateProgressInput(scrapeInstanceId, CurrentPhase: "Filtering"));

            // Step 3: Filter out existing listings, applying limits separately
            //         so sold listings don't consume the active quota
            var newSoldIds = allListingIds.Where(id => soldListingIds.Contains(id)).ToList();
            var newActiveIds = allListingIds.Where(id => !soldListingIds.Contains(id)).ToList();

            var filteredSold = await context.CallActivityAsync<List<string>>(
                nameof(FilterNewListingsActivity),
                new FilterNewListingsInput(jobId, newSoldIds));
            var filteredActive = await context.CallActivityAsync<List<string>>(
                nameof(FilterNewListingsActivity),
                new FilterNewListingsInput(jobId, newActiveIds));

            // Apply limits independently
            if (jobDetails.MaxSoldListings.HasValue && filteredSold.Count > jobDetails.MaxSoldListings.Value)
            {
                logger.LogInformation("Job {JobId}: Limiting sold to {Max} listings (MaxSoldListings config)",
                    jobId, jobDetails.MaxSoldListings.Value);
                filteredSold = filteredSold.Take(jobDetails.MaxSoldListings.Value).ToList();
            }
            if (jobDetails.MaxActiveListings.HasValue && filteredActive.Count > jobDetails.MaxActiveListings.Value)
            {
                logger.LogInformation("Job {JobId}: Limiting active to {Max} listings (MaxActiveListings config)",
                    jobId, jobDetails.MaxActiveListings.Value);
                filteredActive = filteredActive.Take(jobDetails.MaxActiveListings.Value).ToList();
            }

            var newListingIds = filteredSold.Concat(filteredActive).ToList();

            logger.LogInformation("Job {JobId}: {NewCount} new listings to fetch ({SoldNew} sold, {ActiveNew} active)",
                jobId, newListingIds.Count, filteredSold.Count, filteredActive.Count);

            if (newListingIds.Count == 0)
            {
                await context.CallActivityAsync(nameof(UpdateJobTimestampActivity), jobId);
                await context.CallActivityAsync(
                    nameof(UpdateScrapeRunProgressActivity),
                    new UpdateProgressInput(scrapeInstanceId, CurrentPhase: "Completed"));
                return new JobResult(jobId, true, allListingIds.Count, null);
            }

            // Step 4: Extract ScrapeRunId from scrapeInstanceId (format: "scrape-run-{id}")
            var scrapeRunId = int.Parse(scrapeInstanceId.Split('-').Last());

            // Step 5: Insert junction table entries for tracking
            await context.CallActivityAsync(
                nameof(InsertScrapeRunListingsActivity),
                new InsertScrapeRunListingsInput(scrapeRunId, jobId, newListingIds));

            logger.LogInformation("Job {JobId}: Inserted {Count} ScrapeRunListings entries", jobId, newListingIds.Count);

            // Step 6: Set TotalListingsFound BEFORE submitting jobs to prevent race condition
            // where blob triggers auto-complete the run against TotalListingsFound=0
            await context.CallActivityAsync(
                nameof(UpdateScrapeRunProgressActivity),
                new UpdateProgressInput(scrapeInstanceId,
                    TotalListingsFound: newListingIds.Count,
                    ListingsProcessed: 0,
                    CurrentPhase: "Indexing"));

            // Step 7: Submit all scrape jobs (fire-and-forget)
            var submitResult = await context.CallActivityAsync<SubmitScrapeJobsResult>(
                nameof(SubmitScrapeJobsActivity),
                new SubmitScrapeJobsInput(scrapeRunId, newListingIds));

            logger.LogInformation("Job {JobId}: Submitted {Submitted} scrape jobs ({Failed} failed)",
                jobId, submitResult.SubmittedCount, submitResult.FailedCount);

            // Step 8: Update job timestamp
            await context.CallActivityAsync(nameof(UpdateJobTimestampActivity), jobId);

            logger.LogInformation("Job {JobId} completed: {TotalFound} found, {NewCount} new listings submitted for scraping",
                jobId, allListingIds.Count, newListingIds.Count);

            return new JobResult(jobId, true, allListingIds.Count, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed with exception", jobId);

            try
            {
                await context.CallActivityAsync(
                    nameof(UpdateScrapeRunActivity),
                    new UpdateScrapeRunInput(scrapeInstanceId, false, 0, 0, ex.Message));
            }
            catch (Exception updateEx)
            {
                logger.LogError(updateEx, "Job {JobId}: Failed to update ScrapeRun status for instance {InstanceId}",
                    jobId, scrapeInstanceId);
            }

            return new JobResult(jobId, false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Searches a single page using the durable timer pattern.
    /// Builds URL -> Scrapes with ScrapeUrlOrchestrator -> Parses results
    /// </summary>
    private async Task<SearchPageResult> SearchPageAsync(
        TaskOrchestrationContext context,
        string searchTerm,
        int page,
        bool isSold,
        int? lookbackDays)
    {
        // Build the search URL
        var url = await context.CallActivityAsync<string>(
            nameof(BuildUrlsActivity.BuildSearchUrlActivity),
            new BuildSearchUrlInput(searchTerm, isSold, page));

        // Scrape the page using durable timer pattern (no blocking)
        // Search pages don't need GroupId/FileKey (they're not stored for blob triggers)
        var html = await context.CallSubOrchestratorAsync<string?>(
            nameof(ScrapeUrlOrchestrator),
            new ScrapeUrlInput(url));

        if (string.IsNullOrEmpty(html))
        {
            return new SearchPageResult(false, new List<string>(), "Failed to scrape page");
        }

        // Parse the HTML
        var result = await context.CallActivityAsync<SearchPageResult>(
            nameof(ParseSearchPageActivity),
            new ParseSearchPageInput(html, page, isSold, lookbackDays));

        return result;
    }

    /// <summary>
    /// Detects listings that were Active in DB but appear in sold search results,
    /// re-scrapes them to get accurate sold price/date, and updates the database.
    /// </summary>
    private async Task<int> DetectAndUpdateSoldListingsAsync(
        TaskOrchestrationContext context,
        int jobId,
        string scrapeInstanceId,
        HashSet<string> soldListingIds,
        int? maxListingsToFetch,
        ILogger logger)
    {
        if (soldListingIds.Count == 0)
            return 0;

        // Get all active listings for this job from the database
        var activeListings = await context.CallActivityAsync<List<ActiveListingInfo>>(
            nameof(GetActiveListingsActivity),
            new GetActiveListingsInput(jobId));

        if (activeListings.Count == 0)
            return 0;

        // Find listings that are Active in DB but appear in sold search results
        var transitionedListingIds = activeListings
            .Where(l => soldListingIds.Contains(l.ListingId))
            .Select(l => l.ListingId)
            .ToList();

        if (transitionedListingIds.Count == 0)
        {
            logger.LogInformation("Job {JobId}: No Active→Sold transitions detected", jobId);
            return 0;
        }

        // Apply limit if configured (useful for dev/testing)
        if (maxListingsToFetch.HasValue && transitionedListingIds.Count > maxListingsToFetch.Value)
        {
            logger.LogInformation("Job {JobId}: Limiting Active→Sold updates to {Max} (of {Total} detected)",
                jobId, maxListingsToFetch.Value, transitionedListingIds.Count);
            transitionedListingIds = transitionedListingIds.Take(maxListingsToFetch.Value).ToList();
        }

        logger.LogInformation("Job {JobId}: Re-scraping {Count} Active→Sold transitions...",
            jobId, transitionedListingIds.Count);

        // Report progress: Updating Listings
        await context.CallActivityAsync(
            nameof(UpdateScrapeRunProgressActivity),
            new UpdateProgressInput(scrapeInstanceId,
                TotalListingsFound: transitionedListingIds.Count,
                ListingsProcessed: 0,
                CurrentPhase: "Updating Listings"));

        // Build URLs for transitioned listings
        var urlTasks = transitionedListingIds.Select(listingId =>
            context.CallActivityAsync<string>(nameof(BuildUrlsActivity.BuildListingUrlActivity), listingId));
        var listingUrls = await Task.WhenAll(urlTasks);

        // Re-scrape those listings to get accurate sold price and date
        var soldListings = new List<ListingData>();
        for (int i = 0; i < transitionedListingIds.Count; i++)
        {
            try
            {
                var listingData = await context.CallSubOrchestratorAsync<ListingData?>(
                    nameof(FetchListingOrchestrator),
                    new FetchListingInput(transitionedListingIds[i], listingUrls[i]));

                if (listingData != null)
                {
                    soldListings.Add(listingData);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Job {JobId}: Failed to re-scrape sold listing {ListingId}",
                    jobId, transitionedListingIds[i]);
            }

            // Report progress after each listing for real-time updates
            await context.CallActivityAsync(
                nameof(UpdateScrapeRunProgressActivity),
                new UpdateProgressInput(scrapeInstanceId, ListingsProcessed: i + 1));
        }

        if (soldListings.Count == 0)
            return 0;

        // Update the database with sold status
        var updatedCount = await context.CallActivityAsync<int>(
            nameof(UpdateSoldListingsActivity),
            new UpdateSoldListingsInput(jobId, soldListings));

        logger.LogInformation("Job {JobId}: Updated {Count} listings from Active to Sold",
            jobId, updatedCount);

        return updatedCount;
    }
}
