using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Models.Ebay;
using AngleSharp;

namespace AIOMarketMaker.Etl.Activities;

/// <summary>
/// Parses search page HTML and returns listing IDs.
/// This activity only parses - it does NOT fetch any HTML.
/// </summary>
public class ParseSearchPageActivity
{
    private readonly ISearchParser _searchParser;
    private readonly ILogger<ParseSearchPageActivity> _logger;

    public ParseSearchPageActivity(
        ISearchParser searchParser,
        ILogger<ParseSearchPageActivity> logger)
    {
        _searchParser = searchParser;
        _logger = logger;
    }

    [Function(nameof(ParseSearchPageActivity))]
    public async Task<SearchPageResult> Run(
        [ActivityTrigger] ParseSearchPageInput input,
        FunctionContext context)
    {
        var searchType = input.IsSold ? "sold" : "active";
        _logger.LogInformation("Parsing {Type} search page {Page}", searchType, input.Page);

        try
        {
            if (string.IsNullOrEmpty(input.Html))
            {
                _logger.LogWarning("Empty HTML for {Type} page {Page}", searchType, input.Page);
                return new SearchPageResult(true, new List<string>(), null);
            }

            var browsingContext = BrowsingContext.New(Configuration.Default);
            var doc = await browsingContext.OpenAsync(req => req.Content(input.Html));

            var products = _searchParser.ParseSearchResults(doc)
                .OfType<EbayProductSummary>()
                .ToList();

            // Log warning if HTML was large but no products found - indicates bot detection or selector change
            if (products.Count == 0 && input.Html.Length > 10_000)
            {
                _logger.LogWarning("No products parsed from {Length} chars of HTML on {Type} page {Page} - possible bot detection or selector change",
                    input.Html.Length, searchType, input.Page);
            }

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
            _logger.LogError(ex, "Error parsing {Type} page {Page}", searchType, input.Page);
            return new SearchPageResult(false, new List<string>(), ex.Message);
        }
    }
}
