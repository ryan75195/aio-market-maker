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
            "Starting ETL orchestration for listing {ListingId} in ScrapeRun {ScrapeRunId} (triggered by {Source})",
            input.ListingId, input.ScrapeRunId, input.TriggerSource);

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
                cts.Cancel();
                logger.LogInformation("Partner blob arrived for listing {ListingId}", input.ListingId);
            }
            else
            {
                logger.LogWarning(
                    "Timeout waiting for {MissingBlob} blob for listing {ListingId}",
                    state.MissingBlob, input.ListingId);
            }

            // Always re-check blob state after wait (blob may have arrived without trigger firing)
            state = await context.CallActivityAsync<BlobState>(
                nameof(CheckBlobsActivity), input);
        }

        // Handle missing blobs with retry logic
        if (!state.HasListing || !state.HasDescription)
        {
            var missingBlob = !state.HasListing ? "listing" : "description";

            // Check retry count
            var retryResult = await context.CallActivityAsync<IncrementRetryCountResult>(
                nameof(IncrementRetryCountActivity),
                new IncrementRetryCountInput(input.ScrapeRunId, input.ListingId));

            if (retryResult.NewRetryCount > 1)
            {
                logger.LogWarning(
                    "Max retries exceeded for {ListingId}/{MissingBlob}, marking as Failed",
                    input.ListingId, missingBlob);

                await MarkFailed(context, input.ScrapeRunId, input.ListingId,
                    $"Missing {missingBlob} blob after {retryResult.NewRetryCount} retries");
                return;
            }

            // Re-enqueue the missing blob for retry
            logger.LogInformation(
                "Re-enqueueing {MissingBlob} scrape for {ListingId} (retry {RetryCount})",
                missingBlob, input.ListingId, retryResult.NewRetryCount);

            await context.CallActivityAsync(
                nameof(EnqueueScrapeRetryActivity),
                new EnqueueScrapeRetryInput(input.ListingId, missingBlob));

            // Wait again for the blob
            var retryTimeout = context.CurrentUtcDateTime.AddMinutes(TimeoutMinutes);
            using var retryCts = new CancellationTokenSource();

            var retryTimeoutTask = context.CreateTimer(retryTimeout, retryCts.Token);
            var retryEventName = missingBlob == "listing" ? "listing-ready" : "description-ready";
            var retryEvent = context.WaitForExternalEvent<bool>(retryEventName);

            var retryWinner = await Task.WhenAny(retryTimeoutTask, retryEvent);

            if (retryWinner == retryEvent)
            {
                retryCts.Cancel();
                logger.LogInformation("Retry blob arrived for {ListingId}/{MissingBlob}",
                    input.ListingId, missingBlob);
            }
            else
            {
                logger.LogWarning(
                    "Retry timeout for {ListingId}/{MissingBlob}",
                    input.ListingId, missingBlob);
            }

            // Always re-check blob state after retry wait
            state = await context.CallActivityAsync<BlobState>(nameof(CheckBlobsActivity), input);

            // If listing blob still missing after retry, fail
            if (!state.HasListing)
            {
                logger.LogWarning(
                    "Listing blob still missing for {ListingId} after retry, marking as Failed",
                    input.ListingId);

                await MarkFailed(context, input.ScrapeRunId, input.ListingId,
                    "Missing listing blob after timeout and retry");
                return;
            }

            // If only description missing, proceed without it (description is optional)
            if (!state.HasDescription)
            {
                logger.LogWarning(
                    "Description blob still missing for {ListingId} after retry, proceeding without it",
                    input.ListingId);
            }
        }

        // Process listing (with or without description)
        var processInput = new ProcessListingInput(
            input.ListingId,
            ScrapeJobId: lookupResult.ScrapeJobId ?? 0,
            ScrapeRunId: input.ScrapeRunId,
            HasDescription: state.HasDescription
        );

        var result = await context.CallActivityAsync<ProcessListingResult>(nameof(ProcessListingActivity), processInput);

        if (!result.Success)
        {
            logger.LogWarning(
                "Listing {ListingId} failed processing: {Error}",
                input.ListingId, result.ErrorMessage);

            await MarkFailed(context, input.ScrapeRunId, input.ListingId,
                result.ErrorMessage ?? "Processing failed");
            return;
        }

        // Update junction table with completion status
        var updateInput = new UpdateScrapeRunListingInput(
            input.ScrapeRunId,
            input.ListingId,
            "Complete",
            IsNewListing: result.IsNewListing
        );

        await context.CallActivityAsync(nameof(UpdateScrapeRunListingActivity), updateInput);

        // Clean up blobs after successful processing
        await context.CallActivityAsync(
            nameof(DeleteListingBlobsActivity),
            new DeleteBlobsInput(input.ScrapeRunId, input.ListingId));

        logger.LogInformation(
            "ETL orchestration completed for listing {ListingId} (hasDescription={HasDescription})",
            input.ListingId, state.HasDescription);
    }

    private static async Task MarkFailed(
        TaskOrchestrationContext context,
        int scrapeRunId,
        string listingId,
        string errorMessage)
    {
        var failedInput = new UpdateScrapeRunListingInput(
            scrapeRunId,
            listingId,
            "Failed",
            ErrorMessage: errorMessage
        );
        await context.CallActivityAsync(nameof(UpdateScrapeRunListingActivity), failedInput);
    }
}
