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
    private static readonly DateTime StartTime = DateTime.UtcNow;

    [Function("Ping")]
    public HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ping")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");

        var result = new
        {
            status = "pong",
            startedAt = StartTime.ToString("o"),
            runtime = Environment.Version.ToString(),
            machineName = Environment.MachineName
        };

        response.WriteString(JsonSerializer.Serialize(result));
        return response;
    }
}
