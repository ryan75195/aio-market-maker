using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Triggers;

public class ListingBlobTrigger
{
    private readonly ILogger<ListingBlobTrigger> _logger;

    public ListingBlobTrigger(ILogger<ListingBlobTrigger> logger)
    {
        _logger = logger;
    }

    [Function("OnListingBlobCreated")]
    public async Task Run(
        [BlobTrigger("html/{listingId}/listing.html", Connection = "blobStorageConnectionString")] string html,
        [DurableClient] DurableTaskClient client,
        string listingId)
    {
        var instanceId = $"etl-{listingId}";
        _logger.LogInformation("Listing blob trigger fired for {ListingId}", listingId);

        var existingInstance = await client.GetInstanceAsync(instanceId);
        if (existingInstance == null)
        {
            _logger.LogInformation("Starting new orchestration {InstanceId}", instanceId);
            await client.ScheduleNewOrchestrationInstanceAsync(
                "ListingEtlOrchestrator",
                new ListingEtlInput("", listingId, TriggerSource.Listing),
                new StartOrchestrationOptions { InstanceId = instanceId });
        }
        else
        {
            _logger.LogInformation("Orchestration {InstanceId} already exists, raising event", instanceId);
            await client.RaiseEventAsync(instanceId, "listing-ready", true);
        }
    }
}
