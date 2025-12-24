using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Models.Ebay;
using AngleSharp;
using ScraperWorker.Services;
using System.Net;
using System.Text.Json;

namespace AIOMarketMaker.Functions.Functions;

/// <summary>
/// Debug endpoint to test the search flow.
/// </summary>
public class DebugSearch
{
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly IWebscraperClient _webScraper;
    private readonly ISearchParser _searchParser;
    private readonly ILogger<DebugSearch> _logger;

    public DebugSearch(
        IEbayUrlBuilder urlBuilder,
        IWebscraperClient webScraper,
        ISearchParser searchParser,
        ILogger<DebugSearch> logger)
    {
        _urlBuilder = urlBuilder;
        _webScraper = webScraper;
        _searchParser = searchParser;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/debug/url - Just show the URL that would be built (no fetch)
    /// </summary>
    [Function("DebugUrl")]
    public HttpResponseData GetUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "debug/url")] HttpRequestData req)
    {
        var query = req.Query["q"] ?? "Playstation 5 Console";
        var sold = req.Query["sold"] == "true";

        var url = _urlBuilder.BuildSearchUrl(query, sold, 1, Condition.NULL, BuyingFormat.ALL);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        response.WriteString(JsonSerializer.Serialize(new { query, sold, url }));
        return response;
    }

    /// <summary>
    /// GET /api/debug/search - Full search with fetch and parse (may timeout)
    /// </summary>
    [Function("DebugSearch")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "debug/search")] HttpRequestData req)
    {
        var query = req.Query["q"] ?? "test";
        var sold = req.Query["sold"] == "true";

        var result = new DebugSearchResult
        {
            Query = query,
            IsSold = sold,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            // Step 1: Build URL
            result.Url = _urlBuilder.BuildSearchUrl(query, sold, 1, Condition.NULL, BuyingFormat.ALL);
            _logger.LogInformation("Built URL: {Url}", result.Url);

            // Step 2: Fetch HTML
            var html = await _webScraper.GetPageHtmlAsync(result.Url);
            result.HtmlLength = html?.Length ?? 0;
            result.HtmlPreview = html?.Substring(0, Math.Min(500, html?.Length ?? 0));
            _logger.LogInformation("Got HTML: {Length} chars", result.HtmlLength);

            if (string.IsNullOrEmpty(html))
            {
                result.Error = "Empty HTML returned";
            }
            else
            {
                // Step 3: Parse
                var browsingContext = BrowsingContext.New(Configuration.Default);
                var doc = await browsingContext.OpenAsync(r => r.Content(html));

                var products = _searchParser.ParseSearchResults(doc)
                    .OfType<EbayProductSummary>()
                    .ToList();

                result.ProductCount = products.Count;
                result.Products = products.Take(5).Select(p => new ProductPreview
                {
                    ListingId = p.ListingId,
                    Title = p.Title?.Substring(0, Math.Min(50, p.Title?.Length ?? 0)),
                    Price = p.Price,
                    EndDateUtc = p.EndDateUtc
                }).ToList();

                _logger.LogInformation("Parsed {Count} products", products.Count);
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.ToString();
            _logger.LogError(ex, "Debug search failed");
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return response;
    }
}

public class DebugSearchResult
{
    public string? Query { get; set; }
    public bool IsSold { get; set; }
    public DateTime Timestamp { get; set; }
    public string? Url { get; set; }
    public int HtmlLength { get; set; }
    public string? HtmlPreview { get; set; }
    public int ProductCount { get; set; }
    public List<ProductPreview>? Products { get; set; }
    public string? Error { get; set; }
}

public class ProductPreview
{
    public string? ListingId { get; set; }
    public string? Title { get; set; }
    public decimal? Price { get; set; }
    public DateTime? EndDateUtc { get; set; }
}
