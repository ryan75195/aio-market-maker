using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Data;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Functions;

/// <summary>
/// Timer trigger for scheduled ETL runs.
/// Runs all enabled jobs that are due for execution based on their FrequencyMinutes setting.
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
    /// Timer-triggered function that checks for jobs due to run and starts orchestrations.
    /// Default schedule: every 15 minutes. Configure via EtlSchedule app setting.
    /// </summary>
    [Function("EtlScheduledRun")]
    public async Task Run(
        [TimerTrigger("%EtlSchedule%")] TimerInfo timerInfo,
        [DurableClient] DurableTaskClient client)
    {
        _logger.LogInformation("ETL scheduled run triggered at {Time}", DateTime.UtcNow);

        if (timerInfo.IsPastDue)
        {
            _logger.LogWarning("Timer is running late!");
        }

        // Find jobs that are enabled and due to run
        var now = DateTime.UtcNow;

        // Load enabled jobs and filter in memory (SQLite doesn't support DateDiff)
        var enabledJobs = await _dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .ToListAsync();

        var dueJobs = enabledJobs
            .Where(j => j.LastRunUtc == null ||
                        (now - j.LastRunUtc.Value).TotalMinutes >= j.FrequencyMinutes)
            .ToList();

        if (dueJobs.Count == 0)
        {
            _logger.LogInformation("No jobs due for execution");
            return;
        }

        _logger.LogInformation("Found {Count} jobs due for execution", dueJobs.Count);

        var jobIds = dueJobs.Select(j => j.Id).ToList();

        // Start the orchestration for all due jobs
        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(EtlOrchestration.RunAllJobsOrchestration),
            jobIds);

        _logger.LogInformation("Started orchestration {InstanceId} for {Count} scheduled jobs: {JobIds}",
            instanceId, jobIds.Count, string.Join(", ", jobIds));
    }
}
