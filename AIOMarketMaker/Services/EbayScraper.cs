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
            var job = await _fetcher.NewJobAsync(urls);
            var jobId = job.JobId;

            var jobStatus = JobStatusType.Pending;
            while (jobStatus != JobStatusType.Success && jobStatus != JobStatusType.Failure)
            {
                var currentStatus = await _fetcher.GetStatusAsync(jobId);
                _logger.LogInformation(currentStatus?.ToLogString());
                jobStatus = currentStatus != null ? currentStatus.Status : jobStatus;
                await Task.Delay(1000);
            }

            var resultMetadata = await _fetcher.GetResultsAsync(jobId);
            foreach (var result in resultMetadata)
            {
                var downloadLink = result.Url;
                var html = await _jobRepository.GetFileContentsAsync(jobId, downloadLink, new CancellationToken());
                var doc = await LoadDocumentAsync(html);
                var parsedListing = _listingParser.ParseProductListing(doc);
                var descriptionHtml = ""; //await _fetcher.GetStringAsync(parsedListing.descriptionSource);
                var descriptionDoc = await LoadDocumentAsync(descriptionHtml);
                var description = "foo"; // _listingParser.ParseDescription(descriptionDoc);

                results.Add(new EbayProduct(
                    ListingId: parsedListing.id,
                    Title: parsedListing.title,
                    Price: parsedListing.price,
                    Currency: parsedListing.currency,
                    ShippingCost: parsedListing.shippingCost,
                    Condition: parsedListing.Condition,
                    Images: parsedListing.images,
                    ItemSpecifics: parsedListing.ItemSpecifics,
                    Description: description,
                    Url: result.Url,
                    EndDateUtc: parsedListing.SoldDateUtc,
                    ListingStatus: parsedListing.listingStatus,
                    PurchaseFormat: parsedListing.purchaseFormat,
                    Location: parsedListing.Location
                ));
            }
            return results;
        }

        public async Task<IEnumerable<EbayProductSummary>> SearchListings(string query, SearchFilter? filter)
        {
            return (filter != null && filter.SearchDateRange != null) ?
                await GetProductsInDateRange(query, filter) :
                await GetProductsFromPageAsync(query, 1, filter.SearchDateRange != null, filter.Condition, filter.BuyingFormat);
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