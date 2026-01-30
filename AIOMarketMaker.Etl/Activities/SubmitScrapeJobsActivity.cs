using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Models;
using ScraperWorker.Services;

namespace AIOMarketMaker.Etl.Activities;

/// <summary>
/// Submits fire-and-forget scrape jobs by writing directly to the queue.
/// For each listing ID, two queue messages are created (listing + description).
/// This bypasses the WebScraper HTTP API for performance - no job tracking records
/// are created since this flow uses blob triggers and ScrapeRunListings for tracking.
/// </summary>
public class SubmitScrapeJobsActivity
{
    private readonly IQueueService _queueService;
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly ILogger<SubmitScrapeJobsActivity> _logger;

    public SubmitScrapeJobsActivity(
        IQueueService queueService,
        IEbayUrlBuilder urlBuilder,
        ILogger<SubmitScrapeJobsActivity> logger)
    {
        _queueService = queueService;
        _urlBuilder = urlBuilder;
        _logger = logger;
    }

    [Function(nameof(SubmitScrapeJobsActivity))]
    public async Task<SubmitScrapeJobsResult> Run(
        [ActivityTrigger] SubmitScrapeJobsInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Submitting scrape jobs for {Count} listings via direct queue write",
            input.ListingIds.Count);

        var messages = new List<ScrapeQueueMessage>();

        foreach (var listingId in input.ListingIds)
        {
            // Generate a unique job ID for this listing (used for blob path fallback)
            var jobId = Guid.NewGuid().ToString("N");

            // Listing page message
            messages.Add(new ScrapeQueueMessage
            {
                JobId = jobId,
                Url = _urlBuilder.BuildListingUrl(listingId),
                GroupId = listingId,
                FileKey = "listing",
                ScrapeRunId = input.ScrapeRunId,
                EnqueuedAt = DateTimeOffset.UtcNow
            });

            // Description page message
            messages.Add(new ScrapeQueueMessage
            {
                JobId = jobId,
                Url = _urlBuilder.BuildDescriptionUrl(listingId),
                GroupId = listingId,
                FileKey = "description",
                ScrapeRunId = input.ScrapeRunId,
                EnqueuedAt = DateTimeOffset.UtcNow
            });
        }

        try
        {
            await _queueService.EnqueueBatchAsync(messages, CancellationToken.None);

            _logger.LogInformation(
                "Successfully enqueued {MessageCount} messages for {ListingCount} listings",
                messages.Count, input.ListingIds.Count);

            return new SubmitScrapeJobsResult(input.ListingIds.Count, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue scrape jobs batch");
            return new SubmitScrapeJobsResult(0, input.ListingIds.Count);
        }
    }
}
