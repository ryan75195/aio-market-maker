// Services/EbayScraper.cs
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Models.Ebay;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using ScraperWorker.Services;

namespace AIOMarketMaker.Core.Services
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
                _logger.LogInformation("Fetching active listings page {Page} for '{Query}'...", page, query);

                var items = await GetProductsFromPageAsync(query, page, sold: false, condition, buyingFormat);
                var newItems = items.Where(i => seenIds.Add(i.ListingId)).ToList();
                if (!newItems.Any()) break;
                results.AddRange(newItems);

                _logger.LogInformation("  Found {Count} listings (total: {Total})", newItems.Count, results.Count);
            }

            _logger.LogInformation("Active search complete: {Count} listings found", results.Count);
            return results.Take(itemLimit);
        }

        public async Task<IEnumerable<EbayProductSummary>> SearchSoldListings(
            string query,
            BuyingFormat buyingFormat,
            Condition condition,
            DateTime startDate,
            DateTime endDate)
        {
            var results = new List<EbayProductSummary>();
            var seenIds = new HashSet<string>();

            for (int page = 1; ; page++)
            {
                _logger.LogInformation("Fetching sold listings page {Page} for '{Query}'...", page, query);

                var allPageItems = (await GetProductsFromPageAsync(
                        query, page, sold: true, condition, buyingFormat)).ToList();

                var pageItems = allPageItems
                    .Where(p => p.EndDateUtc >= startDate && p.EndDateUtc <= endDate)
                    .ToList();

                var newItems = pageItems.Where(p => seenIds.Add(p.ListingId)).ToList();

                if (newItems.Count == 0) break;

                results.AddRange(newItems);
                _logger.LogInformation("  Found {Count} listings (total: {Total})", newItems.Count, results.Count);
            }

            _logger.LogInformation("Sold search complete: {Count} listings found", results.Count);
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

            var urls = itemIds.Select(_url.BuildListingUrl).ToList();

            var resultMetadata = await _fetcher.RunJobAsync(urls);
            var baseListings = await Task.WhenAll(resultMetadata.Select(x => ParseProductListingAsync(x, x.PartitionKey)));
            var fullListings = baseListings.Select(listing =>
                new EbayProduct(
                    ListingId: listing.id,
                    Title: listing.title,
                    Price: listing.price,
                    Currency: listing.currency,
                    ShippingCost: listing.shippingCost,
                    Condition: listing.Condition,
                    Images: listing.images,
                    Description: null,
                    Url: listing.Url,
                    EndDateUtc: listing.SoldDateUtc,
                    ListingStatus: listing.listingStatus,
                    PurchaseFormat: listing.purchaseFormat,
                    IsSold: listing.listingStatus == EbayListingStatus.Sold
                ));

            return fullListings;
        }


        private async Task<ExtractedEbayListing> ParseProductListingAsync(JobItemEntity metadata, string jobId)
        {
            var downloadLink = metadata.Url;
            var html = await _jobRepository.GetFileContentsAsync(jobId, downloadLink, new CancellationToken());
            var doc = await LoadDocumentAsync(html);
            return _listingParser.ParseProductListing(doc);
        }

        private async Task<IEnumerable<EbayProductSummary>> GetProductsFromPageAsync(string query, int pageNumber, bool sold, Condition condition, BuyingFormat buyingFormat)
        {
            var urlString = _url.BuildSearchUrl(query, sold, pageNumber, condition, buyingFormat);
            var page = await _fetcher.GetPageHtmlAsync(urlString);

            if (string.IsNullOrEmpty(page))
            {
                _logger.LogWarning("Received empty page for '{Query}' page {Page}", query, pageNumber);
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
                        "PARSER BROKEN: Page has {LinkCount} listing links but parser returned 0. eBay HTML structure may have changed.",
                        itemLinks.Length);
                }
            }

            return productList;
        }

        private readonly IBrowsingContext _browsingContext = BrowsingContext.New(AngleSharp.Configuration.Default);

        private async Task<IDocument> LoadDocumentAsync(string html)
        {
            return await _browsingContext
                         .OpenAsync(req => req.Content(html))
                         .ConfigureAwait(false);
        }
    }
}