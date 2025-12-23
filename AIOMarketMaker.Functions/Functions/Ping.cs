using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Net;
using System.Text.Json;

namespace AIOMarketMaker.Functions.Functions;

/// <summary>
/// Ultra-simple ping function for debugging.
/// </summary>
public class Ping
{
    // Build timestamp for deployment verification
    private static readonly string BuildTime = "2024-12-23T20:00:00Z";

    [Function("Ping")]
    public HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ping")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");

        var result = new
        {
            status = "pong",
            buildTime = BuildTime,
            runtime = Environment.Version.ToString(),
            machineName = Environment.MachineName
        };

        response.WriteString(JsonSerializer.Serialize(result));
        return response;
    }
}
