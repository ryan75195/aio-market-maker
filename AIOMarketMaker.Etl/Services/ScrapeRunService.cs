using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Services;

public interface IScrapeRunService
{
    Task<IEnumerable<ScrapeJobConfig>> GetScrapeJobConfigs();
    Task<ScrapeJobConfig?> GetScrapeJobConfig(int jobId);
    Task<IEnumerable<StartedScrapeRun>> StartRuns(IEnumerable<ScrapeJobConfig> jobs, string triggerType);
    Task<StartedScrapeRun> StartRun(ScrapeJobConfig job, string triggerType);
    Task<bool> IsRunComplete(int runId);
}

public class ScrapeRunService : IScrapeRunService
{
    private readonly EtlDbContext _dbContext;
    private readonly QueueClient _jobQueueClient;
    private readonly ILogger<ScrapeRunService> _logger;

    public ScrapeRunService(
        EtlDbContext dbContext,
        QueueServiceClient queueService,
        ILogger<ScrapeRunService> logger)
    {
        _dbContext = dbContext;
        _jobQueueClient = queueService.GetQueueClient("scrape-jobs");
        _jobQueueClient.CreateIfNotExists();
        _logger = logger;
    }

    public async Task<IEnumerable<ScrapeJobConfig>> GetScrapeJobConfigs()
    {
        return await _dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm))
            .ToListAsync();
    }

    public async Task<ScrapeJobConfig?> GetScrapeJobConfig(int jobId)
    {
        return await _dbContext.ScrapeJobs
            .Where(j => j.Id == jobId)
            .Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm))
            .FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<StartedScrapeRun>> StartRuns(
        IEnumerable<ScrapeJobConfig> jobs, string triggerType)
    {
        var runs = new List<StartedScrapeRun>();

        foreach (var job in jobs)
        {
            var run = await CreateScrapeRun(job.Id, triggerType);
            await SendToQueue(run, job, triggerType);

            runs.Add(new StartedScrapeRun(run.Id, job.Id, run.Status, run.InstanceId));
            _logger.LogInformation("Started scrape run for {SearchTerm} (RunId: {RunId})", job.SearchTerm, run.Id);
        }

        return runs;
    }

    private async Task<ScrapeRun> CreateScrapeRun(int jobId, string triggerType)
    {
        var scrapeRun = new ScrapeRun
        {
            JobId = jobId,
            Status = "Queued",
            CurrentPhase = "Queued",
            TriggerType = triggerType,
            StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();
        return scrapeRun;
    }

    private async Task SendToQueue(ScrapeRun run, ScrapeJobConfig job, string triggerType)
    {
        var message = new ScrapeJobMessage(run.Id, job.Id, job.SearchTerm, triggerType);
        var messageJson = JsonSerializer.Serialize(message);
        await _jobQueueClient.SendMessageAsync(messageJson);
    }

    public async Task<StartedScrapeRun> StartRun(ScrapeJobConfig job, string triggerType)
    {
        var run = await CreateScrapeRun(job.Id, triggerType);
        await SendToQueue(run, job, triggerType);

        _logger.LogInformation("Started scrape run for {SearchTerm} (RunId: {RunId})", job.SearchTerm, run.Id);
        return new StartedScrapeRun(run.Id, job.Id, run.Status, run.InstanceId);
    }

    public async Task<bool> IsRunComplete(int runId)
    {
        var run = await _dbContext.ScrapeRuns.FindAsync(runId);

        // Treat missing runs as complete (nothing to wait for)
        if (run == null)
        {
            return true;
        }

        return run.Status == "Completed" || run.Status == "Failed";
    }
}
