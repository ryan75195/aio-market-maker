// EbayController.cs
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Services;

namespace AIOMarketMaker.Controllers
{
    public class EbayController
    {
        private readonly IEbayScraper _scraper;
        private readonly ILogger<EbayController> _logger;

        public EbayController(
            IEbayScraper scraper,
            ILogger<EbayController> logger)
        {
            _scraper = scraper;
            _logger = logger;
        }

        [Function("Search")]
        public async Task<HttpResponseData> Search(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "ebay/search")]
            HttpRequestData req,
            CancellationToken token)
        {
            throw new NotImplementedException();
        }

        [Function("Listing")]
        public async Task<HttpResponseData> Listing(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "ebay/listing")]
            HttpRequestData req,
    CancellationToken token)
        {
            throw new NotImplementedException();
        }

        [Function("ListingsBatch")]
        public async Task<HttpResponseData> ListingsBatch([HttpTrigger(AuthorizationLevel.Function, "get", Route = "ebay/listings")] HttpRequestData req)
        {
            throw new NotImplementedException();
        }
    }
}
