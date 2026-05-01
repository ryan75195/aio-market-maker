using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Pipeline;

namespace AIOMarketMaker.Api.Services;

public class StartupRecoveryService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBatchPipelineRunner _pipeline;
    private readonly ILogger<StartupRecoveryService> _logger;
    private readonly ScrapingConfig _scrapingConfig;

    public StartupRecoveryService(
        IServiceScopeFactory scopeFactory,
        IBatchPipelineRunner pipeline,
        ILogger<StartupRecoveryService> logger,
        ScrapingConfig scrapingConfig)
    {
        _scopeFactory = scopeFactory;
        _pipeline = pipeline;
        _logger = logger;
        _scrapingConfig = scrapingConfig;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();

        var terminalStatuses = new[] { "Completed", "Failed" };
        var orphanedRuns = await db.ScrapeRuns
            .Where(r => !terminalStatuses.Contains(r.Status))
            .ToListAsync(ct);

        if (orphanedRuns.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Found {Count} orphaned scrape runs", orphanedRuns.Count);

        // Group by BatchId for phased recovery
        var batched = orphanedRuns.Where(r => r.BatchId != null).GroupBy(r => r.BatchId!.Value).ToList();
        var legacy = orphanedRuns.Where(r => r.BatchId == null).ToList();

        // Resume batched runs by phase
        foreach (var batch in batched)
        {
            var batchId = batch.Key;
            var phase = batch.First().BatchPhase ?? "Searching";
            _logger.LogInformation("Resuming batch {BatchId} from phase {Phase} ({Count} runs)",
                batchId, phase, batch.Count());

            _ = Task.Run(async () =>
            {
                try
                {
                    await _pipeline.ResumeFromPhase(batchId, phase);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Batch recovery failed for {BatchId}", batchId);
                }
            });
        }

        // Resume legacy runs (pre-BatchPipelineRunner) individually
        if (legacy.Count > 0)
        {
            _logger.LogInformation("Resuming {Count} legacy orphaned runs", legacy.Count);

            var jobIds = legacy.Where(r => r.JobId.HasValue).Select(r => r.JobId!.Value).Distinct();
            var jobs = await db.ScrapeJobs
                .Where(j => jobIds.Contains(j.Id))
                .ToDictionaryAsync(j => j.Id, j => new ScrapeJobConfig(j.Id, j.SearchTerm, j.LastRunUtc), ct);

            var legacyRuns = legacy
                .Where(r => r.JobId.HasValue && jobs.ContainsKey(r.JobId.Value))
                .Select(r => (RunId: r.Id, Job: jobs[r.JobId!.Value]))
                .ToList();

            _ = Task.Run(() => ExecuteLegacyRuns(legacyRuns));
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task ExecuteLegacyRuns(List<(int RunId, ScrapeJobConfig Job)> runs)
    {
        using var semaphore = new SemaphoreSlim(_scrapingConfig.MaxConcurrentRuns);
        var tasks = runs.Select(item => Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            try
            {
                using var jobScope = _scopeFactory.CreateScope();
                var processor = jobScope.ServiceProvider.GetRequiredService<IScrapeJobProcessor>();
                var db = jobScope.ServiceProvider.GetRequiredService<EtlDbContext>();

                var run = await db.ScrapeRuns.FindAsync(item.RunId);
                if (run != null)
                {
                    await processor.Execute(run, item.Job);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Legacy recovery failed for run {RunId}", item.RunId);
            }
            finally
            {
                semaphore.Release();
            }
        }));
        await Task.WhenAll(tasks);
    }
}
