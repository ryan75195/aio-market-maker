// Services/EbayScraper.cs
using AIOMarketMaker.Api.Parsers;
using AIOMarketMaker.Models;
using AIOMarketMaker.Models.Ebay;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Services
{
    public interface IEbayScraper
    {
        Task<IEbayProduct> GetItemFromListing(string itemId);
        Task<IEnumerable<EbayProductSummary>> SearchListings(string query, SearchFilter filter);
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

        public async Task<IEbayProduct> GetItemFromListing(string itemId)
        {
            var urlString = _url.BuildListingUrl(itemId);
            var page = await _fetcher.GetStringAsync(urlString);
            var doc = await LoadDocumentAsync(page);

            var parsedListing = _listingParser.ParseProductListing(doc);
            var descriptionHtml = await _fetcher.GetStringAsync(parsedListing.descriptionSource);
            var descriptionDoc = await LoadDocumentAsync(descriptionHtml);
            var description = _listingParser.ParseDescription(descriptionDoc);

            return new EbayProduct(
                id: parsedListing.id,
                title: parsedListing.title,
                price: parsedListing.price,
                currency: parsedListing.currency,
                shippingCost: parsedListing.shippingCost,
                Condition: parsedListing.Condition,
                images: parsedListing.images,
                ItemSpecifics: parsedListing.ItemSpecifics,
                Description: description,
                url: parsedListing.url,
                SoldDateUtc: parsedListing.SoldDateUtc
            );
        }

        public async Task<IEnumerable<EbayProductSummary>> SearchListings(string query, SearchFilter filter)
        {
            return (filter != null && filter.SoldFilter != null) ? 
                await GetProductsInDateRange(query, filter) : 
                await GetProductsFromPageAsync(query, 1, filter.SoldFilter != null, filter.Condition, filter.BuyingFormat);
        }

        private async Task<IEnumerable<EbayProductSummary>> GetProductsInDateRange(string query, SearchFilter? filter = null)
        {
            var pageOffset = 1;
            var productList = new List<EbayProductSummary>();
            var earliestDate = DateTime.UtcNow;

            while (earliestDate > filter.SoldFilter.startDate)
            {
                var products = await GetProductsFromPageAsync(query, pageOffset, filter.SoldFilter != null, filter.Condition, filter.BuyingFormat);

                products = products.Where(x => x.soldDateUtc <= filter.SoldFilter!.endDate && x.soldDateUtc >= filter.SoldFilter.startDate);

                if (products.Count() == 0)
                {
                    break;
                }

                var soldDates = products
                  .Where(p => p.soldDateUtc.HasValue)
                  .Select(p => p.soldDateUtc.Value);

                earliestDate = soldDates.Min();

                productList.AddRange(products);
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