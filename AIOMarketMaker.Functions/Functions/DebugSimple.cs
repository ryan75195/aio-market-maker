using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Models.Ebay;
using System.Net;
using System.Text.Json;

namespace AIOMarketMaker.Functions.Functions;

/// <summary>
/// Simple debug endpoint with minimal dependencies.
/// </summary>
public class DebugSimple
{
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly IConfiguration _config;

    public DebugSimple(IEbayUrlBuilder urlBuilder, IConfiguration config)
    {
        _urlBuilder = urlBuilder;
        _config = config;
    }

    [Function("DebugSimple")]
    public HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "debug/simple")] HttpRequestData req)
    {
        var query = req.Query["q"] ?? "Playstation 5 Console";
        var sold = req.Query["sold"] == "true";

        var url = _urlBuilder.BuildSearchUrl(query, sold, 1, Condition.NULL, BuyingFormat.ALL);

        var result = new
        {
            query,
            sold,
            url,
            config = new
            {
                scraperBaseUrl = _config["ScraperApi:BaseUrl"] ?? _config["ScraperApi__BaseUrl"],
                storageConnection = !string.IsNullOrEmpty(_config["StorageConnectionString"]) ? "Set" : "Not set",
                sqlConnection = !string.IsNullOrEmpty(_config["SqlConnectionString"]) ? "Set" : "Not set"
            }
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        response.WriteString(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return response;
    }
}
