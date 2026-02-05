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

        foreach (var job in jobs)
        {
            if (ct.IsCancellationRequested) { break; }

            try
            {
                var run = await processor.CreateRun(job, "Nightly");
                await processor.Execute(run, job);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nightly scrape failed for job {JobId}", job.Id);
            }
        }

        _logger.LogInformation("Nightly scrape completed");
    }
}
