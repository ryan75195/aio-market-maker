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

        // ────── Endpoint #1: /api/ebay/scrape?query=… ──────
        [Function("ScrapeEbay")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "ebay/scrape")]
            HttpRequestData req,
            CancellationToken token)
        {
            var qs = HttpUtility.ParseQueryString(req.Url.Query);

            var query = qs["query"];
            var sold = bool.Parse(qs["sold"]);
            var daysBack = 365;
            if (int.TryParse(qs["daysBack"], out var d)) daysBack = d;


            var resp = req.CreateResponse(HttpStatusCode.OK);
            resp.Headers.Add("Content-Type", "application/json; charset=utf-8");

            // Serialize using System.Text.Json
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };

            var results = sold ?
                JsonSerializer.Serialize(await _scraper.SearchSoldListings(query, null), options) :
                JsonSerializer.Serialize(await _scraper.SearchActiveListings(query, null), options);

            await resp.WriteStringAsync(results, token);

            return resp;
        }
    }
}
