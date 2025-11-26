// Services/EbayScraper.cs
using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Api.Services;
using AIOMarketMaker.Models.Ebay;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using ScraperWorker.Services;

namespace AIOMarketMaker.Services
{
    public interface IEbayScraper
    {
        Task<IEnumerable<EbayProduct>> GetItemsFromListings(string[] itemIds);
        Task<IEnumerable<EbayProductSummary>> SearchActiveListings(string query, BuyingFormat buyingFormat, Condition condition, int itemLimit = 500);
        Task<IEnumerable<EbayProductSummary>> SearchSoldListings(string query, BuyingFormat buyingFormat, Condition condition, DateTime startDate, DateTime endDate);
    }

    public class EbayScraper : IEbayScraper
    {
        private readonly IEbayUrlBuilder _url;
        private readonly IWebscraperClient _fetcher;
        private readonly IListingParser _listingParser;
        private readonly ILogger<EbayScraper> _logger;
        private readonly ISearchParser _searchParser;
        private readonly IJobRepository _jobRepository;


        public EbayScraper(
            IEbayUrlBuilder url,
            IWebscraperClient fetcher,
            ISearchParser searchParser,
            IListingParser listingParser,
            IJobRepository jobRepository,
            ILogger<EbayScraper> log)
        {
            _url = url;
            _fetcher = fetcher;
            _searchParser = searchParser;
            _listingParser = listingParser;
            _jobRepository = jobRepository;
            _logger = log;
        }

        public async Task<IEnumerable<EbayProductSummary>> SearchActiveListings(
            string query,
            BuyingFormat buyingFormat,
            Condition condition,
            int itemLimit = 500)
        {
            var results = new List<EbayProductSummary>();
            var seenIds = new HashSet<string>();

            for (int page = 1; results.Count < itemLimit; page++)
            {
                var items = await GetProductsFromPageAsync(query, page, sold: false, condition, buyingFormat);
                var newItems = items.Where(i => seenIds.Add(i.ListingId)).ToList();
                if (!newItems.Any()) break;
                results.AddRange(newItems);
            }

            return results.Take(itemLimit);
        }

        public async Task<IEnumerable<EbayProductSummary>> SearchSoldListings(
            string query,
            BuyingFormat buyingFormat,
            Condition condition,
            DateTime startDate,
            DateTime endDate)
        {
            _logger.LogInformation("Searching sold listings: query=\"{Query}\", dateRange={Start:d} to {End:d}",
                query, startDate, endDate);

            var results = new List<EbayProductSummary>();
            var seenIds = new HashSet<string>();

            for (int page = 1; ; page++)
            {
                var allPageItems = (await GetProductsFromPageAsync(
                        query, page, sold: true, condition, buyingFormat)).ToList();

                var pageItems = allPageItems
                    .Where(p => p.EndDateUtc >= startDate && p.EndDateUtc <= endDate)
                    .ToList();

                var newItems = pageItems.Where(p => seenIds.Add(p.ListingId)).ToList();

                _logger.LogDebug("Page {Page}: {Total} items, {Filtered} in date range, {New} new unique",
                    page, allPageItems.Count, pageItems.Count, newItems.Count);

                if (newItems.Count == 0) break;

                results.AddRange(newItems);
            }

            _logger.LogInformation("Search complete: found {Count} sold listings", results.Count);
            return results;
        }

        public async Task<IEnumerable<EbayProduct>> GetItemsFromListings(string[] itemIds)
        {
            if (itemIds.Length == 0)
            {
                _logger.LogDebug("GetItemsFromListings called with no item IDs");
                return Enumerable.Empty<EbayProduct>();
            }

            _logger.LogInformation("Fetching {Count} listing details...", itemIds.Length);

            var results = new List<EbayProduct>();
            var urls = itemIds.Select(_url.BuildListingUrl).ToList();

            var resultMetadata = await _fetcher.RunJobAsync(urls);
            var baseListings = await Task.WhenAll(resultMetadata.Select(x => ParseProductListingAsync(x, x.PartitionKey)));
            var descriptions = await FetchProductDescriptionsAsync(baseListings);
            var fullListings = baseListings.Select(listing =>
            {
                string description = string.Empty;
                if (!string.IsNullOrEmpty(listing.descriptionSource)
                    && descriptions.TryGetValue(listing.descriptionSource, out var desc))
                {
                    description = desc;
                }

                return new EbayProduct(
                    ListingId: listing.id,
                    Title: listing.title,
                    Price: listing.price,
                    Currency: listing.currency,
                    ShippingCost: listing.shippingCost,
                    Condition: listing.Condition,
                    Images: listing.images,
                    ItemSpecifics: listing.ItemSpecifics,
                    Description: description,
                    Url: listing.Url,
                    EndDateUtc: listing.SoldDateUtc,
                    ListingStatus: listing.listingStatus,
                    PurchaseFormat: listing.purchaseFormat,
                    Location: listing.Location
                );
            });

            return fullListings;
        }

        private async Task<Dictionary<string, string>> FetchProductDescriptionsAsync(
            IEnumerable<ExtractedEbayListing> products)
        {
            var urls = products
                .Select(x => x.descriptionSource)
                .Where(u => !string.IsNullOrEmpty(u))   // ← drop any null/empty
                .Distinct();

            var metas = await _fetcher.RunJobAsync(urls);

            var parsed = await Task.WhenAll(metas.Select(async meta =>
            {
                var html = await _jobRepository.GetFileContentsAsync(
                    meta.PartitionKey, meta.Url, new CancellationToken());

                var doc = await LoadDocumentAsync(html);
                var text = _listingParser.ParseDescription(doc);

                return (Url: meta.Url, Text: text);
            }));

            // only use non-null Urls as keys
            return parsed
              .Where(p => !string.IsNullOrEmpty(p.Url))
              .ToDictionary(p => p.Url!, p => p.Text);
        }


        private async Task<ExtractedEbayListing> ParseProductListingAsync(JobItemEntity metadata, string jobId)
        {
            var downloadLink = metadata.Url;
            var html = await _jobRepository.GetFileContentsAsync(jobId, downloadLink, new CancellationToken());
            var doc = await LoadDocumentAsync(html);
            return _listingParser.ParseProductListing(doc, downloadLink);
        }

        private async Task<IEnumerable<EbayProductSummary>> GetProductsFromPageAsync(string query, int pageNumber, bool sold, Condition condition, BuyingFormat buyingFormat)
        {
            var urlString = _url.BuildSearchUrl(query, sold, pageNumber, condition, buyingFormat);
            _logger.LogInformation("[GetProductsFromPage] Fetching URL: {Url}", urlString);

            var page = await _fetcher.GetPageHtmlAsync(urlString);

            if (string.IsNullOrEmpty(page))
            {
                _logger.LogWarning("[GetProductsFromPage] Received null or empty HTML!");
                return Enumerable.Empty<EbayProductSummary>();
            }

            var doc = await LoadDocumentAsync(page);
            var products = _searchParser.ParseSearchResults(doc);
            var productList = products.OfType<EbayProductSummary>().ToList();

            // Check if parser returned no results but page has listing links - indicates parser is broken
            if (productList.Count == 0)
            {
                var itemLinks = doc.QuerySelectorAll("a[href*='/itm/']");
                if (itemLinks.Length > 10)
                {
                    _logger.LogError(
                        "========== PARSER BROKEN ==========\n" +
                        "EbaySearchParser returned 0 products but page contains {LinkCount} listing links.\n" +
                        "eBay has likely changed their HTML structure.\n" +
                        "The parser in EbaySearchParser.cs needs to be updated.\n" +
                        "URL: {Url}\n" +
                        "===================================",
                        itemLinks.Length, urlString);
                }
            }

            _logger.LogDebug("[GetProductsFromPage] Parser returned {Count} products from {Url}", productList.Count, urlString);

            return productList;
        }

        private readonly IBrowsingContext _browsingContext = BrowsingContext.New(Configuration.Default);

        private async Task<IDocument> LoadDocumentAsync(string html)
        {
            return await _browsingContext
                         .OpenAsync(req => req.Content(html))
                         .ConfigureAwait(false);
        }
    }
}