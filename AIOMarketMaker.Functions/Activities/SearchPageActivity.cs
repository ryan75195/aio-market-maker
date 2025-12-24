using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Functions.Functions;
using AIOMarketMaker.Models.Ebay;
using AngleSharp;
using ScraperWorker.Services;

namespace AIOMarketMaker.Functions.Activities;

public class SearchPageActivity
{
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly IWebscraperClient _webScraper;
    private readonly ISearchParser _searchParser;
    private readonly ILogger<SearchPageActivity> _logger;

    public SearchPageActivity(
        IEbayUrlBuilder urlBuilder,
        IWebscraperClient webScraper,
        ISearchParser searchParser,
        ILogger<SearchPageActivity> logger)
    {
        _urlBuilder = urlBuilder;
        _webScraper = webScraper;
        _searchParser = searchParser;
        _logger = logger;
    }

    [Function(nameof(SearchPageActivity))]
    public async Task<SearchPageResult> Run(
        [ActivityTrigger] SearchPageInput input,
        FunctionContext context)
    {
        var searchType = input.IsSold ? "sold" : "active";
        _logger.LogInformation("Searching {Type} page {Page} for '{Query}'",
            searchType, input.Page, input.SearchTerm);

        try
        {
            var url = _urlBuilder.BuildSearchUrl(
                input.SearchTerm,
                input.IsSold,
                input.Page,
                Condition.NULL,
                BuyingFormat.ALL);

            var html = await _webScraper.GetPageHtmlAsync(url);

            if (string.IsNullOrEmpty(html))
            {
                _logger.LogWarning("Empty page for {Type} page {Page}", searchType, input.Page);
                return new SearchPageResult(true, new List<string>(), null);
            }

            var browsingContext = BrowsingContext.New(Configuration.Default);
            var doc = await browsingContext.OpenAsync(req => req.Content(html));

            var products = _searchParser.ParseSearchResults(doc)
                .OfType<EbayProductSummary>()
                .ToList();

            // Filter by date if this is a sold search with lookback
            if (input.IsSold && input.LookbackDays.HasValue)
            {
                var startDate = DateTime.UtcNow.AddDays(-input.LookbackDays.Value);
                products = products
                    .Where(p => p.EndDateUtc >= startDate)
                    .ToList();
            }

            var listingIds = products
                .Where(p => !string.IsNullOrEmpty(p.ListingId))
                .Select(p => p.ListingId!)
                .ToList();

            _logger.LogInformation("Found {Count} listings on {Type} page {Page}",
                listingIds.Count, searchType, input.Page);

            return new SearchPageResult(true, listingIds, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching {Type} page {Page}", searchType, input.Page);
            return new SearchPageResult(false, new List<string>(), ex.Message);
        }
    }
}
