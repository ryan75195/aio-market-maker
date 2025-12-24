using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Functions.Functions;
using AngleSharp;
using ScraperWorker.Services;

namespace AIOMarketMaker.Functions.Activities;

public class FetchListingsBatchActivity
{
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly IWebscraperClient _webScraper;
    private readonly IListingParser _listingParser;
    private readonly IJobRepository _jobRepository;
    private readonly ILogger<FetchListingsBatchActivity> _logger;

    public FetchListingsBatchActivity(
        IEbayUrlBuilder urlBuilder,
        IWebscraperClient webScraper,
        IListingParser listingParser,
        IJobRepository jobRepository,
        ILogger<FetchListingsBatchActivity> logger)
    {
        _urlBuilder = urlBuilder;
        _webScraper = webScraper;
        _listingParser = listingParser;
        _jobRepository = jobRepository;
        _logger = logger;
    }

    [Function(nameof(FetchListingsBatchActivity))]
    public async Task<FetchListingsBatchResult> Run(
        [ActivityTrigger] FetchListingsBatchInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Fetching batch of {Count} listings", input.ListingIds.Count);

        try
        {
            var listings = new List<ListingData>();
            var urls = input.ListingIds.Select(id => _urlBuilder.BuildListingUrl(id)).ToList();

            var resultMetadata = await _webScraper.RunJobAsync(urls);
            var browsingContext = BrowsingContext.New(Configuration.Default);

            foreach (var meta in resultMetadata)
            {
                try
                {
                    var html = await _jobRepository.GetFileContentsAsync(
                        meta.PartitionKey, meta.Url, CancellationToken.None);

                    if (string.IsNullOrEmpty(html))
                    {
                        _logger.LogWarning("Empty HTML for listing {Url}", meta.Url);
                        continue;
                    }

                    var doc = await browsingContext.OpenAsync(req => req.Content(html));
                    var parsed = _listingParser.ParseProductListing(doc, meta.Url);

                    // Fetch description if available
                    string? description = null;
                    if (!string.IsNullOrEmpty(parsed.descriptionSource))
                    {
                        try
                        {
                            var descMeta = await _webScraper.RunJobAsync(new[] { parsed.descriptionSource });
                            if (descMeta.Any())
                            {
                                var descHtml = await _jobRepository.GetFileContentsAsync(
                                    descMeta.First().PartitionKey, parsed.descriptionSource, CancellationToken.None);
                                if (!string.IsNullOrEmpty(descHtml))
                                {
                                    var descDoc = await browsingContext.OpenAsync(req => req.Content(descHtml));
                                    description = _listingParser.ParseDescription(descDoc);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch description for {ListingId}", parsed.id);
                        }
                    }

                    listings.Add(new ListingData(
                        ListingId: parsed.id ?? "",
                        Title: parsed.title,
                        Price: parsed.price,
                        Currency: parsed.currency,
                        ShippingCost: parsed.shippingCost,
                        Condition: parsed.Condition?.ToString(),
                        ListingStatus: parsed.listingStatus?.ToString(),
                        PurchaseFormat: parsed.purchaseFormat?.ToString(),
                        Description: description,
                        Url: parsed.Url,
                        EndDateUtc: parsed.SoldDateUtc,
                        Location: parsed.Location,
                        ItemSpecifics: parsed.ItemSpecifics,
                        Images: parsed.images?.ToList()
                    ));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse listing {Url}", meta.Url);
                }
            }

            _logger.LogInformation("Successfully fetched {Count} listings", listings.Count);
            return new FetchListingsBatchResult(true, listings, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching batch of listings");
            return new FetchListingsBatchResult(false, new List<ListingData>(), ex.Message);
        }
    }
}
