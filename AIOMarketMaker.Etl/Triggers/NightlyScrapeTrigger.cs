using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
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
    /// Creates a separate ScrapeRun and JobOrchestrator for each enabled job.
    /// </summary>
    [Function("NightlyScrapeTrigger")]
    public async Task Run(
        [TimerTrigger("0 0 2 * * *")] TimerInfo timer,
        [DurableClient] DurableTaskClient client,
        FunctionContext context)
    {
        _logger.LogInformation("Nightly scrape trigger fired at {Time}", DateTime.UtcNow);

        // Get all enabled jobs
        var enabledJobs = await _dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .Select(j => new { j.Id, j.SearchTerm })
            .ToListAsync();

        if (enabledJobs.Count == 0)
        {
            _logger.LogInformation("No enabled jobs found for nightly scrape");
            return;
        }

        // Create a ScrapeRun for each enabled job
        foreach (var job in enabledJobs)
        {
            var scrapeRun = new ScrapeRun
            {
                JobId = job.Id,
                TriggerType = "Nightly",
                StartedUtc = DateTime.UtcNow,
                Status = "Running"
            };
            _dbContext.ScrapeRuns.Add(scrapeRun);
            await _dbContext.SaveChangesAsync();

            // Start orchestration for this job
            var instanceId = $"scrape-run-{scrapeRun.Id}";
            var orchestratorInput = new JobOrchestratorInput(
                job.Id,
                instanceId,
                null, // Use default MaxListingsToFetch
                null  // Use default LookbackDays
            );

            await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(JobOrchestrator), orchestratorInput,
                new StartOrchestrationOptions { InstanceId = instanceId });

            scrapeRun.InstanceId = instanceId;
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Started nightly JobOrchestrator {InstanceId} for job {JobId}: {SearchTerm}",
                instanceId, job.Id, job.SearchTerm);
        }

        _logger.LogInformation("Nightly scrape started {Count} job orchestrations", enabledJobs.Count);
    }
}
