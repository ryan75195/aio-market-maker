using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Functions.Functions.Orchestrators;

namespace AIOMarketMaker.Functions.Functions;

public class NightlyScrapeTrigger
{
    private readonly ILogger<NightlyScrapeTrigger> _logger;

    public NightlyScrapeTrigger(ILogger<NightlyScrapeTrigger> logger)
    {
        _logger = logger;
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

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(ScrapeOrchestrator));

        _logger.LogInformation("Started orchestration with ID: {InstanceId}", instanceId);
    }
}
