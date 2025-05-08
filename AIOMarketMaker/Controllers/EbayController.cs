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
using AngleSharp.Dom;
using AIOMarketMaker.Models.Ebay;

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

        [Function("ListingsBatch")]
        public async Task<HttpResponseData> ListingsBatch([HttpTrigger(AuthorizationLevel.Function, "get", Route = "ebay/listings")] HttpRequestData req)
        {
            var idsParam = HttpUtility.ParseQueryString(req.Url.Query)["listingIds"];
            var ids = idsParam?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                             .ToArray();
            if (ids == null || ids.Length == 0)
                return req.CreateResponse(HttpStatusCode.BadRequest);

            // 1) Start all fetch tasks in parallel
            var fetchTasks = ids.Select(async id =>
            {
                try
                {
                    var item = await _scraper.GetItemFromListing(id);
                    return (Success: true, Item: item, Id: id, Error: (string)null);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed listing {Id}: {Msg}", id, ex.Message);
                    return (Success: false, Item: (EbayProduct)null, Id: id, Error: ex.Message);
                }
            }).ToArray();

            // 2) Await all of them
            var fetchResults = await Task.WhenAll(fetchTasks);

            // 3) Collect only the successful ones (or propagate errors as you like)
            var listings = fetchResults
                .Where(r => r.Success)
                .Select(r => r.Item)
                .ToList();

            var resp = req.CreateResponse(HttpStatusCode.OK);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            var json = JsonSerializer.Serialize(listings, options);
            await resp.WriteStringAsync(json);
            return resp;
        }
    }
}
