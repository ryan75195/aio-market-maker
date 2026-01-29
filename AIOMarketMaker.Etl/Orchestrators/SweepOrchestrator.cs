using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Orchestrators;

public class SweepOrchestrator
{
    private const int PollIntervalSeconds = 60;
    private const int MaxIterations = 30; // 30 minutes max

    [Function(nameof(SweepOrchestrator))]
    public async Task Run([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<SweepOrchestrator>();
        var input = context.GetInput<SweepOrchestratorInput>()!;

        logger.LogInformation(
            "Starting sweep orchestrator for ScrapeRun {ScrapeRunId}",
            input.ScrapeRunId);

        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            // Wait before checking (except on first iteration)
            if (iteration > 0)
            {
                var nextCheck = context.CurrentUtcDateTime.AddSeconds(PollIntervalSeconds);
                await context.CreateTimer(nextCheck, CancellationToken.None);
            }

            // Check how many pending listings remain
            var pendingCount = await context.CallActivityAsync<int>(
                nameof(GetPendingCountActivity),
                input.ScrapeRunId);

            if (pendingCount == 0)
            {
                logger.LogInformation(
                    "Sweep complete for ScrapeRun {ScrapeRunId} - no pending listings remain",
                    input.ScrapeRunId);
                return;
            }

            logger.LogInformation(
                "Sweep iteration {Iteration} for ScrapeRun {ScrapeRunId}: {PendingCount} pending listings",
                iteration + 1, input.ScrapeRunId, pendingCount);

            // Find stale listings (blob exists but no orchestration)
            var result = await context.CallActivityAsync<FindStalePendingListingsResult>(
                nameof(FindStalePendingListingsActivity),
                input.ScrapeRunId);

            if (result.StaleListings.Count > 0)
            {
                logger.LogInformation(
                    "Found {Count} stale listings for ScrapeRun {ScrapeRunId}, starting orchestrations",
                    result.StaleListings.Count, input.ScrapeRunId);

                // Start missing orchestrations
                foreach (var staleListing in result.StaleListings)
                {
                    await context.CallActivityAsync<bool>(
                        nameof(StartMissingOrchestrationActivity),
                        new StartOrchestrationInput(input.ScrapeRunId, staleListing.ListingId));
                }
            }
        }

        logger.LogWarning(
            "Sweep reached max iterations ({MaxIterations}) for ScrapeRun {ScrapeRunId}, exiting",
            MaxIterations, input.ScrapeRunId);
    }
}
