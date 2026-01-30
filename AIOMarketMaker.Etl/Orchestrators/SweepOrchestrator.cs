using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Orchestrators;

public class SweepOrchestrator
{
    private const int InitialDelaySeconds = 90;  // Wait for JobOrchestrator to submit work
    private const int PollIntervalSeconds = 60;
    private const int MaxIterations = 30; // 30 minutes max

    [Function(nameof(SweepOrchestrator))]
    public async Task Run([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<SweepOrchestrator>();
        var input = context.GetInput<SweepOrchestratorInput>()!;

        logger.LogInformation(
            "Starting sweep orchestrator for ScrapeRun {ScrapeRunId}, waiting {InitialDelay}s before first check",
            input.ScrapeRunId, InitialDelaySeconds);

        // Initial delay to let JobOrchestrator submit work and blob triggers fire
        var initialWait = context.CurrentUtcDateTime.AddSeconds(InitialDelaySeconds);
        await context.CreateTimer(initialWait, CancellationToken.None);

        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            // Wait before checking (except on first iteration after initial delay)
            if (iteration > 0)
            {
                var nextCheck = context.CurrentUtcDateTime.AddSeconds(PollIntervalSeconds);
                await context.CreateTimer(nextCheck, CancellationToken.None);
            }

            // Check run status first
            var runStatus = await context.CallActivityAsync<ScrapeRunStatusResult>(
                nameof(GetScrapeRunStatusActivity),
                input.ScrapeRunId);

            // If run is already completed or failed, we're done
            if (runStatus.Status is "Completed" or "Failed" or "NotFound")
            {
                logger.LogInformation(
                    "Sweep exiting for ScrapeRun {ScrapeRunId} - run status is {Status}",
                    input.ScrapeRunId, runStatus.Status);
                return;
            }

            // Check how many pending listings remain in the junction table
            var pendingCount = await context.CallActivityAsync<int>(
                nameof(GetPendingCountActivity),
                input.ScrapeRunId);

            // Only exit when pendingCount == 0 if we're past the Indexing phase start
            var isInIndexingOrLater = runStatus.CurrentPhase is "Indexing" or "Completed";

            if (pendingCount == 0 && isInIndexingOrLater)
            {
                logger.LogInformation(
                    "Sweep complete for ScrapeRun {ScrapeRunId} - no pending listings remain (phase: {Phase})",
                    input.ScrapeRunId, runStatus.CurrentPhase);
                return;
            }

            if (pendingCount == 0)
            {
                // Not in Indexing phase yet - keep waiting
                logger.LogInformation(
                    "Sweep iteration {Iteration} for ScrapeRun {ScrapeRunId}: waiting for Indexing phase (current: {Phase})",
                    iteration + 1, input.ScrapeRunId, runStatus.CurrentPhase);
                continue;
            }

            logger.LogInformation(
                "Sweep iteration {Iteration} for ScrapeRun {ScrapeRunId}: {PendingCount} pending listings",
                iteration + 1, input.ScrapeRunId, pendingCount);

            // Find pending listings and their blob status
            var result = await context.CallActivityAsync<FindPendingListingsResult>(
                nameof(FindStalePendingListingsActivity),
                input.ScrapeRunId);

            // Find listings where blob exists (potential missed triggers)
            var listingsWithBlobs = result.PendingListings
                .Where(p => p.BlobExists)
                .ToList();

            if (listingsWithBlobs.Count > 0)
            {
                logger.LogInformation(
                    "Found {Count} pending listings with blobs for ScrapeRun {ScrapeRunId}, starting orchestrations",
                    listingsWithBlobs.Count, input.ScrapeRunId);

                int startedCount = 0;
                foreach (var pending in listingsWithBlobs)
                {
                    // Use sub-orchestrator to start if not exists
                    var started = await context.CallSubOrchestratorAsync<bool>(
                        nameof(StartOrchestrationIfNotExistsOrchestrator),
                        new StartOrchestrationInput(input.ScrapeRunId, pending.ListingId));

                    if (started)
                    {
                        startedCount++;
                    }
                }

                logger.LogInformation(
                    "Sweep started {StartedCount} missing orchestrations for ScrapeRun {ScrapeRunId}",
                    startedCount, input.ScrapeRunId);
            }
        }

        logger.LogWarning(
            "Sweep reached max iterations ({MaxIterations}) for ScrapeRun {ScrapeRunId}, exiting",
            MaxIterations, input.ScrapeRunId);
    }
}
