using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Functions.Functions;
using AngleSharp;

namespace AIOMarketMaker.Functions.Activities;

/// <summary>
/// Fetches a single listing page and its description.
/// Each listing is processed independently for maximum parallelism and fault isolation.
/// </summary>
public class FetchSingleListingActivity
{
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly IWebscraperClient _webScraper;
    private readonly IListingParser _listingParser;
    private readonly ILogger<FetchSingleListingActivity> _logger;

    public FetchSingleListingActivity(
        IEbayUrlBuilder urlBuilder,
        IWebscraperClient webScraper,
        IListingParser listingParser,
        ILogger<FetchSingleListingActivity> logger)
    {
        _urlBuilder = urlBuilder;
        _webScraper = webScraper;
        _listingParser = listingParser;
        _logger = logger;
    }

    [Function(nameof(FetchSingleListingActivity))]
    public async Task<ListingData?> Run(
        [ActivityTrigger] string listingId,
        FunctionContext context)
    {
        _logger.LogInformation("Fetching listing {ListingId}", listingId);

        try
        {
            // 1. Build listing URL and fetch page
            var listingUrl = _urlBuilder.BuildListingUrl(listingId);
            var listingHtml = await _webScraper.GetPageHtmlAsync(listingUrl);

            if (string.IsNullOrEmpty(listingHtml))
            {
                _logger.LogWarning("Empty HTML for listing {ListingId}", listingId);
                return null;
            }

            // 2. Parse listing page
            var browsingContext = BrowsingContext.New(Configuration.Default);
            var doc = await browsingContext.OpenAsync(req => req.Content(listingHtml));
            var parsed = _listingParser.ParseProductListing(doc, listingUrl);

            // 3. Fetch and parse description (in same activity - no separate scraper call overhead)
            string? description = null;
            if (!string.IsNullOrEmpty(parsed.descriptionSource))
            {
                try
                {
                    var descHtml = await _webScraper.GetPageHtmlAsync(parsed.descriptionSource);
                    if (!string.IsNullOrEmpty(descHtml))
                    {
                        var descDoc = await browsingContext.OpenAsync(req => req.Content(descHtml));
                        description = _listingParser.ParseDescription(descDoc);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch description for {ListingId}", listingId);
                    // Continue without description - don't fail the whole listing
                }
            }

            _logger.LogInformation("Successfully fetched listing {ListingId}", listingId);

            return new ListingData(
                ListingId: parsed.id ?? listingId,
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
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch listing {ListingId}", listingId);
            return null;
        }
    }
}
