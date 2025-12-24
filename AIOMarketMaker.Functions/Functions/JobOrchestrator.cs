using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Functions.Activities;

namespace AIOMarketMaker.Functions.Functions;

/// <summary>
/// Sub-orchestrator that handles a single scrape job.
/// Breaks down the work into page-level activities to avoid timeouts.
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

            // Step 2: Fan out to search pages (both active and sold)
            var searchTasks = new List<Task<SearchPageResult>>();

            // Search sold listings - up to 10 pages
            for (int page = 1; page <= 10; page++)
            {
                searchTasks.Add(context.CallActivityAsync<SearchPageResult>(
                    nameof(SearchPageActivity),
                    new SearchPageInput(jobDetails.SearchTerm, page, IsSold: true, jobDetails.LookbackDays)));
            }

            // Search active listings - up to 10 pages
            for (int page = 1; page <= 10; page++)
            {
                searchTasks.Add(context.CallActivityAsync<SearchPageResult>(
                    nameof(SearchPageActivity),
                    new SearchPageInput(jobDetails.SearchTerm, page, IsSold: false, LookbackDays: null)));
            }

            var searchResults = await Task.WhenAll(searchTasks);

            // Step 3: Aggregate and dedupe results
            var allListingIds = searchResults
                .Where(r => r.Success)
                .SelectMany(r => r.ListingIds)
                .Distinct()
                .ToList();

            logger.LogInformation("Job {JobId}: Found {Count} unique listings from search", jobId, allListingIds.Count);

            if (allListingIds.Count == 0)
            {
                await context.CallActivityAsync(nameof(UpdateJobTimestampActivity), jobId);
                return new JobResult(jobId, true, 0, null);
            }

            // Step 4: Filter out existing listings
            var newListingIds = await context.CallActivityAsync<List<string>>(
                nameof(FilterNewListingsActivity),
                new FilterNewListingsInput(jobId, allListingIds));

            logger.LogInformation("Job {JobId}: {NewCount} new listings to fetch", jobId, newListingIds.Count);

            if (newListingIds.Count == 0)
            {
                await context.CallActivityAsync(nameof(UpdateJobTimestampActivity), jobId);
                return new JobResult(jobId, true, allListingIds.Count, null);
            }

            // Step 5: Fan out to fetch listing details in batches of 20
            const int batchSize = 20;
            var fetchTasks = new List<Task<FetchListingsBatchResult>>();

            for (int i = 0; i < newListingIds.Count; i += batchSize)
            {
                var batch = newListingIds.Skip(i).Take(batchSize).ToList();
                fetchTasks.Add(context.CallActivityAsync<FetchListingsBatchResult>(
                    nameof(FetchListingsBatchActivity),
                    new FetchListingsBatchInput(batch)));
            }

            var fetchResults = await Task.WhenAll(fetchTasks);

            // Step 6: Save all fetched listings
            var allListings = fetchResults
                .Where(r => r.Success)
                .SelectMany(r => r.Listings)
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
}

// DTOs for the orchestrator
public record JobDetails(int Id, string SearchTerm, int LookbackDays);
public record SearchPageInput(string SearchTerm, int Page, bool IsSold, int? LookbackDays);
public record SearchPageResult(bool Success, List<string> ListingIds, string? Error);
public record FilterNewListingsInput(int JobId, List<string> ListingIds);
public record FetchListingsBatchInput(List<string> ListingIds);
public record FetchListingsBatchResult(bool Success, List<ListingData> Listings, string? Error);
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
