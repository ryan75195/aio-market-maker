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

                try
                {
                    await context.CallActivityAsync(nameof(RecordIssueActivity), new RecordIssueRequest(
                        input.ScrapeRunId,
                        input.ListingId,
                        "DESCRIPTION_FETCH_FAILED",
                        "Description blob not found after timeout"));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to record issue for listing {ListingId}", input.ListingId);
                    // Continue - don't fail the listing processing
                }
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

        // Handle skipped listings (delisted or product page redirects)
        if (result.IsDelisted || result.IsProductPageRedirect)
        {
            var skipReason = result.IsDelisted ? "Delisted" : "ProductPageRedirect";
            logger.LogInformation(
                "Listing {ListingId} skipped: {Reason}",
                input.ListingId, skipReason);

            await context.CallActivityAsync(nameof(UpdateScrapeRunListingActivity),
                new UpdateScrapeRunListingInput(
                    input.ScrapeRunId,
                    input.ListingId,
                    "Skipped",
                    FailureReason: skipReason));

            // Clean up blobs
            await context.CallActivityAsync(
                nameof(DeleteListingBlobsActivity),
                new DeleteBlobsInput(input.ScrapeRunId, input.ListingId));

            return;
        }

        if (!result.Success)
        {
            // Handle parse failures with retry loop
            if (result.IsParseFailure)
            {
                const int MaxParseAttempts = 3;
                var currentAttempt = lookupResult.ParseAttempts;

                // Retry loop - keep trying until max attempts reached
                while (currentAttempt < MaxParseAttempts)
                {
                    currentAttempt++;
                    logger.LogInformation(
                        "Parse failed for {ListingId} (attempt {Attempt}/{Max}), re-enqueueing for retry",
                        input.ListingId, currentAttempt, MaxParseAttempts);

                    // Update parse attempts in DB
                    await context.CallActivityAsync(nameof(UpdateScrapeRunListingActivity),
                        new UpdateScrapeRunListingInput(
                            input.ScrapeRunId,
                            input.ListingId,
                            "Pending",
                            IncrementParseAttempts: 1));

                    // Re-enqueue the listing scrape for retry
                    await context.CallActivityAsync(nameof(EnqueueScrapeRetryActivity),
                        new EnqueueScrapeRetryInput(input.ListingId, "listing", input.ScrapeRunId));

                    // Wait for the retry blob
                    var retryTimeout = context.CurrentUtcDateTime.AddMinutes(TimeoutMinutes);
                    using var retryCts = new CancellationTokenSource();

                    var retryTimeoutTask = context.CreateTimer(retryTimeout, retryCts.Token);
                    var retryEvent = context.WaitForExternalEvent<bool>("listing-ready");

                    var retryWinner = await Task.WhenAny(retryTimeoutTask, retryEvent);

                    if (retryWinner == retryEvent)
                    {
                        retryCts.Cancel();
                        logger.LogInformation("Retry listing blob arrived for {ListingId}", input.ListingId);

                        // Re-check blob state and re-process
                        state = await context.CallActivityAsync<BlobState>(nameof(CheckBlobsActivity), input);

                        if (state.HasListing)
                        {
                            // Try processing again
                            processInput = new ProcessListingInput(
                                input.ListingId,
                                ScrapeJobId: lookupResult.ScrapeJobId ?? 0,
                                ScrapeRunId: input.ScrapeRunId,
                                HasDescription: state.HasDescription
                            );

                            result = await context.CallActivityAsync<ProcessListingResult>(
                                nameof(ProcessListingActivity), processInput);

                            if (result.Success)
                            {
                                // Success! Update and return
                                var retryUpdateInput = new UpdateScrapeRunListingInput(
                                    input.ScrapeRunId,
                                    input.ListingId,
                                    "Complete",
                                    IsNewListing: result.IsNewListing,
                                    ListingStatus: result.ListingStatus
                                );
                                await context.CallActivityAsync(nameof(UpdateScrapeRunListingActivity), retryUpdateInput);

                                await context.CallActivityAsync(
                                    nameof(DeleteListingBlobsActivity),
                                    new DeleteBlobsInput(input.ScrapeRunId, input.ListingId));

                                logger.LogInformation(
                                    "ETL orchestration completed for listing {ListingId} after {Attempts} attempts",
                                    input.ListingId, currentAttempt);
                                return;
                            }
                            // Parse still failed - continue loop to try again
                        }
                    }
                    else
                    {
                        logger.LogWarning(
                            "Retry timeout for {ListingId} (attempt {Attempt}/{Max})",
                            input.ListingId, currentAttempt, MaxParseAttempts);
                        // Continue loop to try again
                    }
                }

                // Parse retries exhausted - record detailed failure
                logger.LogWarning(
                    "Parse retries exhausted for {ListingId} after {Attempts} attempts: {MissingFields}",
                    input.ListingId, currentAttempt, string.Join(", ", result.MissingFields ?? new List<string>()));

                var failureDetails = result.MissingFields != null
                    ? $"Missing: {string.Join(", ", result.MissingFields)}"
                    : result.ErrorMessage ?? "Parse failed";

                await context.CallActivityAsync(nameof(UpdateScrapeRunListingActivity),
                    new UpdateScrapeRunListingInput(
                        input.ScrapeRunId,
                        input.ListingId,
                        "Failed",
                        ErrorMessage: failureDetails,
                        FailureReason: "PARSE_EXHAUSTED",
                        FailureDetails: failureDetails));

                await context.CallActivityAsync(nameof(RecordIssueActivity),
                    new RecordIssueRequest(
                        input.ScrapeRunId,
                        input.ListingId,
                        "PARSE_FAILED",
                        failureDetails));

                return;
            }

            // Non-parse failure (e.g., eBay error page)
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
            IsNewListing: result.IsNewListing,
            ListingStatus: result.ListingStatus
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
