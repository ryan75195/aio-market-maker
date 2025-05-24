// Services/EbayScraper.cs
using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Api.Services;
using AIOMarketMaker.Models;
using AIOMarketMaker.Models.Ebay;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using ScraperWorker.Services;
using System.Data.SqlTypes;
using System.Reflection;

namespace AIOMarketMaker.Services
{
    public interface IEbayScraper
    {
        Task<IEnumerable<EbayProduct>> GetItemsFromListings(string[] itemIds);
        Task<IEnumerable<EbayProductSummary>> SearchListings(string query, SearchFilter? filter);
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

        public async Task<IEnumerable<EbayProduct>> GetItemsFromListings(string[] itemIds)
        {
            var results = new List<EbayProduct>();
            var urls = itemIds.Select(_url.BuildListingUrl);
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

        public async Task<IEnumerable<EbayProductSummary>> SearchListings(string query, SearchFilter? filter)
        {
            var defaultFilter = new SearchFilter(
                new SearchDateRange(DateTime.Now - new TimeSpan(7, 0, 0, 0), DateTime.Now),
                BuyingFormat.BUY_NOW,
                Condition.NEW
            );

            filter = filter ?? defaultFilter;

            return (filter != null && filter.SearchDateRange != null) ?
                await GetProductsInDateRange(query, filter) :
                await GetProductsFromPageAsync(query, 1, filter != null && filter.SearchDateRange != null, filter.Condition, filter.BuyingFormat);
        }

        // I need tests :( Move me to my own service and write some unit tests. 
        private async Task<IEnumerable<EbayProductSummary>> GetProductsInDateRange(string query, SearchFilter? filter = null)
        {
            var pageOffset = 1;
            var productList = new List<EbayProductSummary>();
            var lastPageProducts = new List<EbayProductSummary>();
            var earliestDate = DateTime.UtcNow;

            while (earliestDate > filter.SearchDateRange.startDate)
            {
                var products = await GetProductsFromPageAsync(query, pageOffset, filter.SearchDateRange != null, filter.Condition, filter.BuyingFormat);

                products = products.Where(x => x.EndDateUtc <= filter.SearchDateRange!.endDate && x.EndDateUtc >= filter.SearchDateRange.startDate);

                if (products.Count() == 0 || products.All(p => lastPageProducts.Any(lp => lp.ListingId == p.ListingId)))
                {
                    break;
                }

                var soldDates = products
                  .Where(p => p.EndDateUtc.HasValue)
                  .Select(p => p.EndDateUtc.Value);

                earliestDate = soldDates.Min();

                productList.AddRange(products);
                lastPageProducts = products.ToList();
                pageOffset++;
            }
            return productList;
        }

        private async Task<IEnumerable<EbayProductSummary>> GetProductsFromPageAsync(string query, int pageNumber, bool sold, Condition condition, BuyingFormat buyingFormat)
        {
            var urlString = _url.BuildSearchUrl(query, sold, pageNumber, condition, buyingFormat);
            var page = await _fetcher.GetPageHtmlAsync(urlString);
            var doc = await LoadDocumentAsync(page);
            var products = _searchParser.ParseSearchResults(doc);
            return products.OfType<EbayProductSummary>().ToList();
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