using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using System.Net;

namespace AIOMarketMaker.Functions.Functions;

public class ManualScrapeTrigger
{
    private readonly ILogger<ManualScrapeTrigger> _logger;

    public ManualScrapeTrigger(ILogger<ManualScrapeTrigger> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// HTTP trigger to manually start the scrape orchestration.
    /// POST /api/scrape/start
    /// </summary>
    [Function("ManualScrapeTrigger")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "scrape/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        FunctionContext context)
    {
        _logger.LogInformation("Manual scrape trigger fired at {Time}", DateTime.UtcNow);

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(ScrapeOrchestrator));

        _logger.LogInformation("Started orchestration with ID: {InstanceId}", instanceId);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { instanceId, status = "Started" });
        return response;
    }
}
