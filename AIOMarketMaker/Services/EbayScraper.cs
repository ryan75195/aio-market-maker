// Services/EbayScraper.cs
using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Models;
using AIOMarketMaker.Models.Ebay;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace AIOMarketMaker.Services
{
    public interface IEbayScraper
    {
        Task<EbayProduct> GetItemFromListing(string itemId);
        Task<IEnumerable<EbayProductSummary>> SearchListings(string query, SearchFilter? filter);
    }

    public class EbayScraper : IEbayScraper
    {
        private readonly IEbayUrlBuilder _url;
        private readonly IHtmlFetcher _fetcher;
        private readonly IListingParser _listingParser;
        private readonly ISearchParser _searchParser;


        public EbayScraper(
            IEbayUrlBuilder url,
            IHtmlFetcher fetcher,
            ISearchParser searchParser,
            IListingParser listingParser,
            ILogger<EbayScraper> log)
        {
            _url = url;
            _fetcher = fetcher;
            _searchParser = searchParser;
            _listingParser = listingParser;

        }

        public async Task<EbayProduct> GetItemFromListing(string itemId)
        {
            var urlString = _url.BuildListingUrl(itemId);
            var page = await _fetcher.GetStringAsync(urlString);
            var doc = await LoadDocumentAsync(page);

            var parsedListing = _listingParser.ParseProductListing(doc);
            var descriptionHtml = await _fetcher.GetStringAsync(parsedListing.descriptionSource);
            var descriptionDoc = await LoadDocumentAsync(descriptionHtml);
            var description = _listingParser.ParseDescription(descriptionDoc);

            return new EbayProduct(
                ListingId: parsedListing.id,
                Title: parsedListing.title,
                Price: parsedListing.price,
                Currency: parsedListing.currency,
                ShippingCost: parsedListing.shippingCost,
                Condition: parsedListing.Condition,
                Images: parsedListing.images,
                ItemSpecifics: parsedListing.ItemSpecifics,
                Description: description,
                Url: urlString,
                EndDateUtc: parsedListing.SoldDateUtc,
                ListingStatus: parsedListing.listingStatus,
                PurchaseFormat: parsedListing.purchaseFormat,
                Location: parsedListing.Location
            );
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
            var page = await _fetcher.GetStringAsync(urlString);
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