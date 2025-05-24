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
            var results = new List<EbayProductSummary>();
            var seenIds = new HashSet<string>();

            for (int page = 1; ; page++)
            {
                var pageItems = (await GetProductsFromPageAsync(
                        query, page, sold: true, condition, buyingFormat))
                    .Where(p => p.EndDateUtc >= startDate && p.EndDateUtc <= endDate)
                    .ToList();

                var newItems = pageItems.Where(p => seenIds.Add(p.ListingId)).ToList();
                if (newItems.Count == 0) break;

                results.AddRange(newItems);
            }

            return results;
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