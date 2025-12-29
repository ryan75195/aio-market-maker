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
    /// HTTP trigger to purge all completed/terminated orchestrations.
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
            // Terminate all running/pending orchestrations first
            var query = new OrchestrationQuery
            {
                CreatedFrom = DateTime.UtcNow.AddDays(-7)
            };

            var terminated = 0;
            await foreach (var metadata in client.GetAllInstancesAsync(query))
            {
                if (metadata.RuntimeStatus == OrchestrationRuntimeStatus.Running ||
                    metadata.RuntimeStatus == OrchestrationRuntimeStatus.Pending ||
                    metadata.RuntimeStatus == OrchestrationRuntimeStatus.Suspended)
                {
                    try
                    {
                        await client.TerminateInstanceAsync(metadata.InstanceId, "Purged via API");
                        terminated++;
                        _logger.LogInformation("Terminated: {InstanceId} ({Name})", metadata.InstanceId, metadata.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to terminate: {InstanceId}", metadata.InstanceId);
                    }
                }
            }

            // Purge completed/failed/terminated orchestrations
            var purgeResult = await client.PurgeAllInstancesAsync(
                new PurgeInstancesFilter(
                    CreatedFrom: DateTime.UtcNow.AddDays(-7),
                    CreatedTo: DateTime.UtcNow,
                    Statuses: new[]
                    {
                        OrchestrationRuntimeStatus.Completed,
                        OrchestrationRuntimeStatus.Failed,
                        OrchestrationRuntimeStatus.Terminated
                    }));

            _logger.LogInformation("Purge completed: {Terminated} terminated, {Purged} purged",
                terminated, purgeResult.PurgedInstanceCount);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                terminated,
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
