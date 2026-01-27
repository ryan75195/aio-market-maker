using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Orchestrators;

public class ListingEtlOrchestrator
{
    private const int TimeoutMinutes = 5;

    [Function(nameof(ListingEtlOrchestrator))]
    public async Task Run([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<ListingEtlOrchestrator>();
        var input = context.GetInput<ListingEtlInput>()!;

        logger.LogInformation(
            "Starting ETL orchestration for listing {ListingId} (triggered by {Source})",
            input.ListingId, input.TriggerSource);

        // Lookup ScrapeRun and ScrapeJob from junction table
        var lookupResult = await context.CallActivityAsync<ScrapeRunLookupResult>(
            nameof(LookupScrapeRunActivity), input.ListingId);

        if (!lookupResult.Found)
        {
            logger.LogWarning(
                "No pending ScrapeRunListing found for {ListingId}, skipping processing",
                input.ListingId);
            return;
        }

        // Check what blobs exist
        var state = await context.CallActivityAsync<BlobState>(
            nameof(CheckBlobsActivity), input);

        // Wait for partner if needed
        if (!state.HasBoth)
        {
            logger.LogInformation(
                "Waiting for {MissingBlob} blob for listing {ListingId}",
                state.MissingBlob, input.ListingId);

            var timeout = context.CurrentUtcDateTime.AddMinutes(TimeoutMinutes);
            using var cts = new CancellationTokenSource();

            var timeoutTask = context.CreateTimer(timeout, cts.Token);
            var eventName = state.MissingBlob == "listing" ? "listing-ready" : "description-ready";
            var partnerEvent = context.WaitForExternalEvent<bool>(eventName);

            var winner = await Task.WhenAny(timeoutTask, partnerEvent);

            if (winner == partnerEvent)
            {
                cts.Cancel(); // Cancel the timer
                logger.LogInformation("Partner blob arrived for listing {ListingId}", input.ListingId);

                // Re-check blob state after event
                state = await context.CallActivityAsync<BlobState>(
                    nameof(CheckBlobsActivity), input);
            }
            else
            {
                logger.LogWarning(
                    "Timeout waiting for {MissingBlob} blob for listing {ListingId}",
                    state.MissingBlob, input.ListingId);
            }
        }

        // Process listing (with or without description)
        var processInput = new ProcessListingInput(
            JobId: "",
            input.ListingId,
            ScrapeJobId: lookupResult.ScrapeJobId ?? 0,
            HasDescription: state.HasDescription
        );

        await context.CallActivityAsync(nameof(ProcessListingActivity), processInput);

        // Update junction table with completion status
        var updateInput = new UpdateScrapeRunListingInput(
            lookupResult.ScrapeRunId!.Value,
            input.ListingId,
            "Complete"
        );

        await context.CallActivityAsync(nameof(UpdateScrapeRunListingActivity), updateInput);

        logger.LogInformation(
            "ETL orchestration completed for listing {ListingId} (hasDescription={HasDescription})",
            input.ListingId, state.HasDescription);
    }
}
