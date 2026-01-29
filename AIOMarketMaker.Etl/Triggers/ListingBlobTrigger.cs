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
        [BlobTrigger("html/{scrapeRunId}/{listingId}/listing.html", Connection = "blobStorageConnectionString")] string html,
        [DurableClient] DurableTaskClient client,
        int scrapeRunId,
        string listingId)
    {
        var instanceId = $"etl-{scrapeRunId}-{listingId}";
        _logger.LogInformation("Listing blob trigger fired for ScrapeRun {ScrapeRunId}, Listing {ListingId}", scrapeRunId, listingId);

        var existingInstance = await client.GetInstanceAsync(instanceId);
        if (existingInstance == null)
        {
            _logger.LogInformation("Starting new orchestration {InstanceId}", instanceId);
            await client.ScheduleNewOrchestrationInstanceAsync(
                "ListingEtlOrchestrator",
                new ListingEtlInput(scrapeRunId, listingId, TriggerSource.Listing),
                new StartOrchestrationOptions { InstanceId = instanceId });
        }
        else if (existingInstance.RuntimeStatus == OrchestrationRuntimeStatus.Running ||
                 existingInstance.RuntimeStatus == OrchestrationRuntimeStatus.Pending ||
                 existingInstance.RuntimeStatus == OrchestrationRuntimeStatus.Suspended)
        {
            _logger.LogInformation("Orchestration {InstanceId} is {Status}, raising event",
                instanceId, existingInstance.RuntimeStatus);
            await client.RaiseEventAsync(instanceId, "listing-ready", true);
        }
        else
        {
            _logger.LogInformation(
                "Orchestration {InstanceId} already completed with status {Status}, skipping event",
                instanceId, existingInstance.RuntimeStatus);
        }
    }
}
