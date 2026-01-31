using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Orchestrators;

/// <summary>
/// Sub-orchestrator that starts ListingEtlOrchestrator if it doesn't exist.
/// Used by SweepOrchestrator to recover missed blob triggers.
/// </summary>
public class StartOrchestrationIfNotExistsOrchestrator
{
    [Function(nameof(StartOrchestrationIfNotExistsOrchestrator))]
    public async Task<bool> Run([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<StartOrchestrationIfNotExistsOrchestrator>();
        var input = context.GetInput<StartOrchestrationInput>()!;

        var instanceId = $"etl-{input.ScrapeRunId}-{input.ListingId}";

        try
        {
            var etlInput = new ListingEtlInput(input.ScrapeRunId, input.ListingId, TriggerSource.Sweep);

            // Start the ETL orchestrator with a specific instanceId
            // Durable Functions will reject duplicate starts with the same instanceId
            await context.CallSubOrchestratorAsync(
                nameof(ListingEtlOrchestrator),
                etlInput,
                new SubOrchestrationOptions { InstanceId = instanceId });

            logger.LogInformation(
                "Started missing orchestration {InstanceId} via sweep",
                instanceId);

            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("already exists") ||
                                   ex.Message.Contains("conflict") ||
                                   ex.Message.Contains("duplicate"))
        {
            logger.LogInformation(
                "Orchestration {InstanceId} already exists, skipping",
                instanceId);
            return false;
        }
    }
}
