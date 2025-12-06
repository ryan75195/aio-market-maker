using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Data;

namespace AIOMarketMaker.Functions;

/// <summary>
/// Timer trigger for scheduled ETL runs.
/// DISABLED: Jobs are now on-demand only. Use dashboard to run jobs manually.
///
/// Configure the schedule in local.settings.json:
/// "EtlSchedule": "0 */15 * * * *"  (every 15 minutes)
/// </summary>
public class EtlTimerTrigger
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<EtlTimerTrigger> _logger;

    public EtlTimerTrigger(
        EtlDbContext dbContext,
        ILogger<EtlTimerTrigger> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Timer-triggered function that was previously used for scheduled job runs.
    /// DISABLED: Jobs are now on-demand only. Use dashboard to run jobs manually.
    /// </summary>
    [Function("EtlScheduledRun")]
    public Task Run(
        [TimerTrigger("%EtlSchedule%")] TimerInfo timerInfo,
        [DurableClient] DurableTaskClient client)
    {
        // DISABLED: Auto-run functionality is disabled. Jobs are now on-demand only.
        // Use the dashboard "Run Now" button or "Run All Enabled" to execute jobs.
        _logger.LogInformation("ETL scheduled run triggered but auto-run is DISABLED. Use dashboard to run jobs manually.");
        return Task.CompletedTask;
    }
}
