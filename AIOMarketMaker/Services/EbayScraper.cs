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
        Task<IEnumerable<ActiveEbayProductSummary>> SearchActiveListings(string query, DateTime? startPeriod);
        Task<IEnumerable<SoldEbayProductSummary>> SearchSoldListings(string query, DateTime? startPeriod);

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
            // 1. use itemId to build the URL
            var urlString = _url.BuildListingUrl(itemId);

            // 2. fetch html
            var page = await _fetcher.GetStringAsync(urlString);

            // 3. check listing type to see if active or sold
            //var hasListingEnded = _parser.hasListingEnded(page);

            // 4. parse all data
            //var item = _parser.Parse(page);
            throw new NotImplementedException();
            // 5. return object
        }

        public async Task<IEnumerable<ActiveEbayProductSummary>> SearchActiveListings(string query, DateTime? startPeriod)
        {
            var urlString = _url.BuildSearchUrl(query, sold: false, completed: false);
            var page = await _fetcher.GetStringAsync(urlString);
            var doc = await LoadDocumentAsync(page);
            var products = _parser.ParseSearchResults(doc);
            return products.OfType<ActiveEbayProductSummary>().ToList();
        }

        public async Task<IEnumerable<SoldEbayProductSummary>> SearchSoldListings(string query, DateTime? startPeriod)
        {
            var urlString = _url.BuildSearchUrl(query, sold: true, completed: true);
            var page = await _fetcher.GetStringAsync(urlString);
            var doc = await LoadDocumentAsync(page);
            var products = _parser.ParseSearchResults(doc);
            return products.OfType<SoldEbayProductSummary>().ToList();
        }


        private readonly IBrowsingContext _browsingContext
    = BrowsingContext.New(Configuration.Default);

        private async Task<IDocument> LoadDocumentAsync(string html)
        {
            return await _browsingContext
                         .OpenAsync(req => req.Content(html))
                         .ConfigureAwait(false);
        }
    }
}