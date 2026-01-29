using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Triggers;

public class DescriptionBlobTrigger
{
    private readonly ILogger<DescriptionBlobTrigger> _logger;

    public DescriptionBlobTrigger(ILogger<DescriptionBlobTrigger> logger)
    {
        _logger = logger;
    }

    [Function("OnDescriptionBlobCreated")]
    public async Task Run(
        [BlobTrigger("html/{listingId}/description.html", Connection = "blobStorageConnectionString")] string html,
        [DurableClient] DurableTaskClient client,
        string listingId)
    {
        var instanceId = $"etl-{listingId}";
        _logger.LogInformation("Description blob trigger fired for {ListingId}", listingId);

        var existingInstance = await client.GetInstanceAsync(instanceId);
        if (existingInstance == null)
        {
            _logger.LogInformation("Starting new orchestration {InstanceId}", instanceId);
            await client.ScheduleNewOrchestrationInstanceAsync(
                "ListingEtlOrchestrator",
                new ListingEtlInput(listingId, TriggerSource.Description),
                new StartOrchestrationOptions { InstanceId = instanceId });
        }
        else if (existingInstance.RuntimeStatus == OrchestrationRuntimeStatus.Running ||
                 existingInstance.RuntimeStatus == OrchestrationRuntimeStatus.Pending ||
                 existingInstance.RuntimeStatus == OrchestrationRuntimeStatus.Suspended)
        {
            _logger.LogInformation("Orchestration {InstanceId} is {Status}, raising event",
                instanceId, existingInstance.RuntimeStatus);
            await client.RaiseEventAsync(instanceId, "description-ready", true);
        }
        else
        {
            _logger.LogInformation(
                "Orchestration {InstanceId} already completed with status {Status}, skipping event",
                instanceId, existingInstance.RuntimeStatus);
        }
    }
}
