using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

/// <summary>
/// Submits fire-and-forget scrape jobs for listing and description pages.
/// For each listing ID, two scrape jobs are submitted (listing + description).
/// This activity does NOT wait for scrapes to complete - it just queues them.
/// </summary>
public class SubmitScrapeJobsActivity
{
    private readonly IWebscraperClient _webScraper;
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly ILogger<SubmitScrapeJobsActivity> _logger;

    public SubmitScrapeJobsActivity(
        IWebscraperClient webScraper,
        IEbayUrlBuilder urlBuilder,
        ILogger<SubmitScrapeJobsActivity> logger)
    {
        _webScraper = webScraper;
        _urlBuilder = urlBuilder;
        _logger = logger;
    }

    [Function(nameof(SubmitScrapeJobsActivity))]
    public async Task<SubmitScrapeJobsResult> Run(
        [ActivityTrigger] SubmitScrapeJobsInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Submitting scrape jobs for {Count} listings", input.ListingIds.Count);

        var submittedCount = 0;
        var failedCount = 0;

        foreach (var listingId in input.ListingIds)
        {
            try
            {
                // Submit listing page scrape
                var listingUrl = _urlBuilder.BuildListingUrl(listingId);
                await _webScraper.NewJobAsync(
                    new[] { listingUrl },
                    groupId: listingId,
                    fileKey: "listing",
                    scrapeRunId: input.ScrapeRunId);

                // Submit description page scrape
                var descriptionUrl = _urlBuilder.BuildDescriptionUrl(listingId);
                await _webScraper.NewJobAsync(
                    new[] { descriptionUrl },
                    groupId: listingId,
                    fileKey: "description",
                    scrapeRunId: input.ScrapeRunId);

                submittedCount++;
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogWarning(ex, "Failed to submit scrape jobs for listing {ListingId}", listingId);
                // Continue with other listings - don't fail the whole batch
            }
        }

        _logger.LogInformation(
            "Submitted scrape jobs: {Submitted} succeeded, {Failed} failed",
            submittedCount, failedCount);

        return new SubmitScrapeJobsResult(submittedCount, failedCount);
    }
}
