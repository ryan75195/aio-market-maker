using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using System.Net;
using AIOMarketMaker.Functions.Functions.Orchestrators;

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

    /// <summary>
    /// HTTP trigger to terminate a specific orchestration.
    /// DELETE /api/orchestration/{instanceId}
    /// </summary>
    [Function("TerminateOrchestration")]
    public async Task<HttpResponseData> TerminateOrchestration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "orchestration/{instanceId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        string instanceId,
        FunctionContext context)
    {
        _logger.LogInformation("Terminating orchestration: {InstanceId}", instanceId);

        try
        {
            await client.TerminateInstanceAsync(instanceId, "Terminated via API");

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { instanceId, status = "Terminated" });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to terminate orchestration: {InstanceId}", instanceId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// HTTP trigger to purge all orchestrations (running and completed).
    /// POST /api/orchestration/purge
    /// </summary>
    [Function("PurgeOrchestrations")]
    public async Task<HttpResponseData> PurgeOrchestrations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orchestration/purge")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        FunctionContext context)
    {
        _logger.LogInformation("Purging orchestrations...");

        try
        {
            // Purge ALL orchestration statuses at once
            // This is more efficient than listing and terminating individually
            var allStatuses = new[]
            {
                OrchestrationRuntimeStatus.Completed,
                OrchestrationRuntimeStatus.Failed,
                OrchestrationRuntimeStatus.Terminated,
                OrchestrationRuntimeStatus.Running,
                OrchestrationRuntimeStatus.Pending,
                OrchestrationRuntimeStatus.Suspended
            };

            var purgeResult = await client.PurgeAllInstancesAsync(
                new PurgeInstancesFilter(
                    CreatedFrom: DateTime.UtcNow.AddDays(-7),
                    CreatedTo: DateTime.UtcNow,
                    Statuses: allStatuses));

            _logger.LogInformation("Purge completed: {Purged} instances purged",
                purgeResult.PurgedInstanceCount);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                purged = purgeResult.PurgedInstanceCount,
                status = "Purge completed"
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to purge orchestrations");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return errorResponse;
        }
    }
}
