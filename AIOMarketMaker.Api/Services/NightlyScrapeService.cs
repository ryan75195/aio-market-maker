using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;

namespace AIOMarketMaker.Api.Services;

public class NightlyScrapeService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NightlyScrapeService> _logger;

    public NightlyScrapeService(
        IServiceScopeFactory scopeFactory,
        ILogger<NightlyScrapeService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var next2AM = now.Date.AddHours(2);
            if (next2AM <= now)
            {
                next2AM = next2AM.AddDays(1);
            }

            var delay = next2AM - now;
            _logger.LogInformation("Next nightly scrape at {Time} ({Delay})", next2AM, delay);

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunNightly(ct);
        }
    }

    public async Task RunNightly(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting nightly scrape");

        using var scope = _scopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IScrapeJobProcessor>();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();

        var jobs = await db.ScrapeJobs.Where(j => j.IsEnabled)
            .Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm))
            .ToListAsync(ct);

        _logger.LogInformation("Found {Count} enabled jobs for nightly scrape", jobs.Count);

        // Create all runs sequentially in one scope so they appear in UI immediately
        var runIds = new List<int>();
        foreach (var job in jobs)
        {
            if (ct.IsCancellationRequested) { break; }

            var run = await processor.CreateRun(job, "Nightly");
            runIds.Add(run.Id);
        }

        // Execute in parallel (max 3 concurrent) with per-job DI scopes
        var parallelism = new SemaphoreSlim(3);
        var tasks = jobs.Select((job, i) => Task.Run(async () =>
        {
            await parallelism.WaitAsync(ct);
            try
            {
                using var jobScope = _scopeFactory.CreateScope();
                var jobProcessor = jobScope.ServiceProvider.GetRequiredService<IScrapeJobProcessor>();
                var jobDb = jobScope.ServiceProvider.GetRequiredService<EtlDbContext>();

                var run = await jobDb.ScrapeRuns.FindAsync(runIds[i]);
                if (run == null) { return; }

                await jobProcessor.Execute(run, job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nightly scrape failed for job {JobId}", job.Id);
            }
            finally
            {
                parallelism.Release();
            }
        }));
        await Task.WhenAll(tasks);

        _logger.LogInformation("Nightly scrape completed");
    }
}
