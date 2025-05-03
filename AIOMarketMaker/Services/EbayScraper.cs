// Services/EbayScraper.cs
using AIOMarketMaker.Models;
using AIOMarketMaker.Models.Ebay;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.AspNetCore.Html;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Services
{
    public interface IEbayScraper
    {
        Task<IEbayProduct> GetItemFromListing(string itemId);
        Task<IEnumerable<EbayProductSummary>> SearchListings(string query, SearchFilter? filter = null);
    }

    public class EbayScraper : IEbayScraper
    {
        private readonly IEbayUrlBuilder _url;
        private readonly IHtmlFetcher _fetcher;
        private readonly IEbayItemParser _parser;
        private readonly ILogger<EbayScraper> _log;
        private readonly IBrowsingContext _ctx;

        private const int PerPage = 240;

        public EbayScraper(
            IEbayUrlBuilder url,
            IHtmlFetcher fetcher,
            IEbayItemParser parser,
            ILogger<EbayScraper> log)
        {
            _url = url;
            _fetcher = fetcher;
            _parser = parser;
            _log = log;
            _ctx = BrowsingContext.New(Configuration.Default);
        }

        public async Task<IEbayProduct> GetItemFromListing(string itemId)
        {
            var urlString = _url.BuildListingUrl(itemId);
            var page = await _fetcher.GetStringAsync(urlString);
            var doc = await LoadDocumentAsync(page);
            var item = _parser.ParseProductListing(doc);
            return item;
        }

        public async Task<IEnumerable<EbayProductSummary>> SearchListings(string query, SearchFilter? filter = null)
        {
            if (filter != null && filter.SoldFilter != null) {
                var pageOffset = 1;
                var productList = new List<EbayProductSummary>();
                var earliestDate = DateTime.UtcNow;

                while (earliestDate > filter.SoldFilter.startDate)
                {
                    var products = await GetProductsFromPageAsync(query, pageOffset, filter.SoldFilter != null);

                    products = products.Where(x => x.soldDateUtc <= filter.SoldFilter!.endDate && x.soldDateUtc >= filter.SoldFilter.startDate);
                    
                    if( products.Count() == 0)
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

            else
            {
                return await GetProductsFromPageAsync(query, 1, filter.SoldFilter != null);
            }
        }

        private async Task<IEnumerable<EbayProductSummary>> GetProductsFromPageAsync(string query, int pageNumber, bool sold)
        {
            var urlString = _url.BuildSearchUrl(query, sold, pageNumber);
            var page = await _fetcher.GetStringAsync(urlString);
            var doc = await LoadDocumentAsync(page);
            var products = _parser.ParseSearchResults(doc);
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