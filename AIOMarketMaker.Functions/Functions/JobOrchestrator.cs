using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Functions.Activities;

namespace AIOMarketMaker.Functions.Functions;

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
        var jobId = context.GetInput<int>();

        logger.LogInformation("Starting job orchestration for job {JobId}", jobId);

        try
        {
            // Step 1: Get job details
            var jobDetails = await context.CallActivityAsync<JobDetails>(
                nameof(GetJobDetailsActivity), jobId);

            if (jobDetails == null)
            {
                return new JobResult(jobId, false, 0, $"Job {jobId} not found");
            }

            logger.LogInformation("Job {JobId}: '{SearchTerm}'", jobId, jobDetails.SearchTerm);

            var seenIds = new HashSet<string>();
            var allListingIds = new List<string>();

            // Step 2a: Search SOLD listings page by page until no results in date range
            logger.LogInformation("Job {JobId}: Searching sold listings...", jobId);
            for (int page = 1; ; page++)
            {
                var result = await SearchPageAsync(context, jobDetails.SearchTerm, page, true, jobDetails.LookbackDays);

                if (!result.Success || result.ListingIds.Count == 0)
                    break;

                var newIds = result.ListingIds.Where(id => seenIds.Add(id)).ToList();
                allListingIds.AddRange(newIds);

                logger.LogInformation("Job {JobId}: Sold page {Page} found {Count} listings (total: {Total})",
                    jobId, page, newIds.Count, allListingIds.Count);

                if (newIds.Count == 0)
                    break;
            }

            // Step 2b: Search ACTIVE listings page by page until no results (limit 10000)
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

            logger.LogInformation("Job {JobId}: Found {Count} unique listings from search", jobId, allListingIds.Count);

            if (allListingIds.Count == 0)
            {
                await context.CallActivityAsync(nameof(UpdateJobTimestampActivity), jobId);
                return new JobResult(jobId, true, 0, null);
            }

            // Step 3: Filter out existing listings
            var newListingIds = await context.CallActivityAsync<List<string>>(
                nameof(FilterNewListingsActivity),
                new FilterNewListingsInput(jobId, allListingIds));

            logger.LogInformation("Job {JobId}: {NewCount} new listings to fetch", jobId, newListingIds.Count);

            if (newListingIds.Count == 0)
            {
                await context.CallActivityAsync(nameof(UpdateJobTimestampActivity), jobId);
                return new JobResult(jobId, true, allListingIds.Count, null);
            }

            // Step 4: Build listing URLs (fan out)
            var urlTasks = newListingIds.Select(listingId =>
                context.CallActivityAsync<string>(nameof(BuildUrlsActivity.BuildListingUrlActivity), listingId));
            var listingUrls = await Task.WhenAll(urlTasks);

            // Step 5: Fan out to fetch each listing using sub-orchestrators
            // Each FetchListingOrchestrator handles the listing page + description with durable timers
            // Wrap each call to handle individual failures without failing the whole batch
            var fetchTasks = newListingIds.Select(async (listingId, i) =>
            {
                try
                {
                    return await context.CallSubOrchestratorAsync<ListingData?>(
                        nameof(FetchListingOrchestrator),
                        new FetchListingInput(listingId, listingUrls[i]));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Job {JobId}: Failed to fetch listing {ListingId}", jobId, listingId);
                    return null;
                }
            });

            var fetchResults = await Task.WhenAll(fetchTasks);

            // Step 6: Collect successful results (nulls are failed/skipped listings)
            var allListings = fetchResults
                .Where(r => r != null)
                .Cast<ListingData>()
                .ToList();

            logger.LogInformation("Job {JobId}: Fetched {Count} listing details", jobId, allListings.Count);

            if (allListings.Count > 0)
            {
                await context.CallActivityAsync(
                    nameof(SaveListingsActivity),
                    new SaveListingsInput(jobId, allListings));
            }

            // Step 7: Update job timestamp
            await context.CallActivityAsync(nameof(UpdateJobTimestampActivity), jobId);

            logger.LogInformation("Job {JobId} completed: {TotalFound} found, {NewFetched} new",
                jobId, allListingIds.Count, allListings.Count);

            return new JobResult(jobId, true, allListingIds.Count, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed with exception", jobId);
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
        var html = await context.CallSubOrchestratorAsync<string?>(
            nameof(ScrapeUrlOrchestrator), url);

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
}

// DTOs for the orchestrator
public record JobDetails(int Id, string SearchTerm, int LookbackDays);
public record SearchPageResult(bool Success, List<string> ListingIds, string? Error);
public record FilterNewListingsInput(int JobId, List<string> ListingIds);
public record SaveListingsInput(int JobId, List<ListingData> Listings);

public record ListingData(
    string ListingId,
    string? Title,
    decimal? Price,
    string? Currency,
    decimal? ShippingCost,
    string? Condition,
    string? ListingStatus,
    string? PurchaseFormat,
    string? Description,
    string? Url,
    DateTime? EndDateUtc,
    string? Location,
    string? ItemSpecifics,
    List<string>? Images
);
