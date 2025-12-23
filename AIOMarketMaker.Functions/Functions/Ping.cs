using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Net;

namespace AIOMarketMaker.Functions.Functions;

/// <summary>
/// Ultra-simple ping function for debugging.
/// </summary>
public class Ping
{
    [Function("Ping")]
    public HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ping")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.WriteString("pong");
        return response;
    }
}
