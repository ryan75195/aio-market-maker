using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Orchestrators;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Etl.Triggers;

public class NightlyScrapeTrigger
{
    private readonly ILogger<NightlyScrapeTrigger> _logger;
    private readonly EtlDbContext _dbContext;

    public NightlyScrapeTrigger(ILogger<NightlyScrapeTrigger> logger, EtlDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    /// <summary>
    /// Timer trigger that starts the scrape orchestration nightly at 2 AM UTC.
    /// </summary>
    [Function("NightlyScrapeTrigger")]
    public async Task Run(
        [TimerTrigger("0 0 2 * * *")] TimerInfo timer,
        [DurableClient] DurableTaskClient client,
        FunctionContext context)
    {
        _logger.LogInformation("Nightly scrape trigger fired at {Time}", DateTime.UtcNow);

        // Create a ScrapeRun record to track this execution
        var scrapeRun = new ScrapeRun
        {
            InstanceId = null, // Will be set after orchestration starts
            TriggerType = "Nightly",
            StartedUtc = DateTime.UtcNow,
            Status = "Running"
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        // Start orchestration with runId
        var orchestratorInput = new ScrapeOrchestratorInput(
            scrapeRun.Id,
            null, // Use default MaxListingsToFetch
            null  // Use default LookbackDays
        );

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(ScrapeOrchestrator), orchestratorInput);

        // Update ScrapeRun with instanceId
        scrapeRun.InstanceId = instanceId;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Started nightly orchestration {InstanceId} for run {RunId}", instanceId, scrapeRun.Id);
    }
}
