// EbayController.cs
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Services;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;

namespace AIOMarketMaker.Controllers
{
    public class EbayController
    {
        private readonly IEbayScraper _scraper;
        private readonly IHtmlFetcher _fetcher;
        private readonly ILogger<EbayController> _logger;

        public EbayController(
            IEbayScraper scraper,
            IHtmlFetcher fetcher,
            ILogger<EbayController> logger)
        {
            _scraper = scraper;
            _fetcher = fetcher;
            _logger = logger;
        }

        [Function("Search")]
        public async Task<HttpResponseData> Search(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "ebay/search")]
            HttpRequestData req,
            CancellationToken token)
        {
            var inputs = HttpUtility.ParseQueryString(req.Url.Query);

            var query = inputs["query"];
            var buyingFormatString = inputs["buyingFormat"];
            var conditionString = inputs["condition"];


            var condition = (Condition)Enum.Parse(
                typeof(Condition),
                conditionString,
                ignoreCase: true
            );

            var buyingFormat = (BuyingFormat)Enum.Parse(
                typeof(BuyingFormat),
                buyingFormatString,
                ignoreCase: true
            );

            var lowerDateBound = inputs["lowerDateBound"];
            var upperDateBound = inputs["upperDateBound"];
            var dateRangeFilter = (lowerDateBound != null && upperDateBound != null) ? 
                new SearchDateRange(DateTime.Parse(lowerDateBound), DateTime.Parse(upperDateBound)): null;

            var filter = new SearchFilter(dateRangeFilter, buyingFormat, condition);

            var listings = await _scraper.SearchListings(query, filter);

            // 4) Serialize & return
            var resp = req.CreateResponse(HttpStatusCode.OK);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            // serialize with camelCase
            var json = JsonSerializer.Serialize(listings, options);
            await resp.WriteStringAsync(json);
            return resp;
        }

        [Function("Listing")]
        public async Task<HttpResponseData> Listing(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "ebay/listing")]
            HttpRequestData req,
    CancellationToken token)
        {
            var inputs = HttpUtility.ParseQueryString(req.Url.Query);

            var listingId = inputs["listingId"];
            var resp = req.CreateResponse(HttpStatusCode.OK);
            var listing = await _scraper.GetItemFromListing(listingId);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            // serialize with camelCase
            var json = JsonSerializer.Serialize(listing, options);
            await resp.WriteStringAsync(json);

            return resp;

        }
    }
}
