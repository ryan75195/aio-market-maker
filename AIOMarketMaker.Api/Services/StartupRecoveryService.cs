using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;

namespace AIOMarketMaker.Api.Services;

public class StartupRecoveryService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StartupRecoveryService> _logger;
    private readonly ScrapingConfig _scrapingConfig;

    public StartupRecoveryService(
        IServiceScopeFactory scopeFactory,
        ILogger<StartupRecoveryService> logger,
        ScrapingConfig scrapingConfig)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _scrapingConfig = scrapingConfig;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var toResume = await FindOrphanedRuns(ct);
        if (toResume.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Resuming {Count} orphaned scrape runs", toResume.Count);

        // Fire-and-forget parallel execution (same pattern as ScrapeEndpoints/NightlyScrapeService)
        _ = Task.Run(() => ExecuteRuns(toResume));
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task<List<OrphanedRun>> FindOrphanedRuns(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();

        var terminalStatuses = new[] { "Completed", "Failed" };
        var orphanedRuns = await db.ScrapeRuns
            .Where(r => !terminalStatuses.Contains(r.Status))
            .ToListAsync(ct);

        if (orphanedRuns.Count == 0)
        {
            return [];
        }

        var jobIds = orphanedRuns
            .Where(r => r.JobId.HasValue)
            .Select(r => r.JobId!.Value)
            .Distinct();

        var jobs = await db.ScrapeJobs
            .Where(j => jobIds.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, j => new ScrapeJobConfig(j.Id, j.SearchTerm, j.LastRunUtc), ct);

        return orphanedRuns
            .Where(r => r.JobId.HasValue && jobs.ContainsKey(r.JobId.Value))
            .Select(r => new OrphanedRun(r.Id, jobs[r.JobId.Value]))
            .ToList();
    }

    private async Task ExecuteRuns(List<OrphanedRun> toResume)
    {
        var parallelism = new SemaphoreSlim(_scrapingConfig.MaxConcurrentRuns);
        var tasks = toResume.Select(item => Task.Run(async () =>
        {
            await parallelism.WaitAsync();
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
                _logger.LogError(ex, "Recovery failed for run {RunId}", item.RunId);
            }
            finally
            {
                parallelism.Release();
            }
        }));
        await Task.WhenAll(tasks);
    }

    private record OrphanedRun(int RunId, ScrapeJobConfig Job);
}
