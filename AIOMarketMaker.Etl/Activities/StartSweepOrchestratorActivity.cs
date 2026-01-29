using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Orchestrators;

namespace AIOMarketMaker.Etl.Activities;

public class StartSweepOrchestratorActivity
{
    private readonly DurableTaskClient _durableClient;
    private readonly ILogger<StartSweepOrchestratorActivity> _logger;

    public StartSweepOrchestratorActivity(
        DurableTaskClient durableClient,
        ILogger<StartSweepOrchestratorActivity> logger)
    {
        _durableClient = durableClient;
        _logger = logger;
    }

    [Function(nameof(StartSweepOrchestratorActivity))]
    public async Task Run([ActivityTrigger] StartSweepInput input)
    {
        // Check if sweep already running for this run (idempotency)
        var existing = await _durableClient.GetInstanceAsync(input.InstanceId);
        if (existing != null &&
            (existing.RuntimeStatus == OrchestrationRuntimeStatus.Running ||
             existing.RuntimeStatus == OrchestrationRuntimeStatus.Pending))
        {
            _logger.LogInformation(
                "Sweep orchestrator {InstanceId} already running, skipping",
                input.InstanceId);
            return;
        }

        // Start the sweep orchestrator
        await _durableClient.ScheduleNewOrchestrationInstanceAsync(
            nameof(SweepOrchestrator),
            new SweepOrchestratorInput(input.ScrapeRunId),
            new StartOrchestrationOptions { InstanceId = input.InstanceId });

        _logger.LogInformation(
            "Started sweep orchestrator {InstanceId} for ScrapeRun {ScrapeRunId}",
            input.InstanceId, input.ScrapeRunId);
    }
}
