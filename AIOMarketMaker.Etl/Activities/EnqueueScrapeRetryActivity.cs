using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

/// <summary>
/// Enqueues a single URL to the scrape-work queue for retry.
/// Used when a blob fails to arrive after ETL timeout.
/// </summary>
public class EnqueueScrapeRetryActivity
{
    private readonly IWebscraperClient _webScraper;
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly ILogger<EnqueueScrapeRetryActivity> _logger;

    public EnqueueScrapeRetryActivity(
        IWebscraperClient webScraper,
        IEbayUrlBuilder urlBuilder,
        ILogger<EnqueueScrapeRetryActivity> logger)
    {
        _webScraper = webScraper;
        _urlBuilder = urlBuilder;
        _logger = logger;
    }

    [Function(nameof(EnqueueScrapeRetryActivity))]
    public async Task Run([ActivityTrigger] EnqueueScrapeRetryInput input)
    {
        var url = input.FileKey == "listing"
            ? _urlBuilder.BuildListingUrl(input.ListingId)
            : _urlBuilder.BuildDescriptionUrl(input.ListingId);

        _logger.LogInformation(
            "Enqueuing retry scrape for {ListingId}/{FileKey}: {Url}",
            input.ListingId, input.FileKey, url);

        await _webScraper.NewJobAsync(
            new[] { url },
            groupId: input.ListingId,
            fileKey: input.FileKey);
    }
}
