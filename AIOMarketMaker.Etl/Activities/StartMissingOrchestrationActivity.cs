using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Orchestrators;

namespace AIOMarketMaker.Etl.Activities;

public class StartMissingOrchestrationActivity
{
    private readonly DurableTaskClient _durableClient;
    private readonly ILogger<StartMissingOrchestrationActivity> _logger;

    public StartMissingOrchestrationActivity(
        DurableTaskClient durableClient,
        ILogger<StartMissingOrchestrationActivity> logger)
    {
        _durableClient = durableClient;
        _logger = logger;
    }

    [Function(nameof(StartMissingOrchestrationActivity))]
    public async Task<bool> Run([ActivityTrigger] StartOrchestrationInput input)
    {
        var instanceId = $"etl-{input.ScrapeRunId}-{input.ListingId}";

        // Check if already exists (race condition protection)
        var existing = await _durableClient.GetInstanceAsync(instanceId);
        if (existing != null)
        {
            _logger.LogInformation(
                "Orchestration {InstanceId} already exists with status {Status}, skipping",
                instanceId, existing.RuntimeStatus);
            return false;
        }

        // Start the ETL orchestration
        var etlInput = new ListingEtlInput(input.ScrapeRunId, input.ListingId, TriggerSource.Sweep);

        await _durableClient.ScheduleNewOrchestrationInstanceAsync(
            nameof(ListingEtlOrchestrator),
            etlInput,
            new StartOrchestrationOptions { InstanceId = instanceId });

        _logger.LogInformation(
            "Started missing orchestration {InstanceId} via sweep",
            instanceId);

        return true;
    }
}
