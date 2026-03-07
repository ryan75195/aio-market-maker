using AIOMarketMaker.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services.Pipeline;

public class SearchBatchStage : IBatchStage
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ScrapingConfig _config;
    private readonly ILogger<SearchBatchStage> _logger;

    public string Name => "Search All Jobs";

    public SearchBatchStage(
        IServiceScopeFactory scopeFactory,
        ScrapingConfig config,
        ILogger<SearchBatchStage> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    public async Task Execute(BatchContext context, CancellationToken ct = default)
    {
        var jobLookup = context.Jobs.ToDictionary(j => j.Id);
        var runIds = context.RunIds.ToList();

        _logger.LogInformation("SearchBatchStage: searching {Count} jobs with concurrency {Max}",
            runIds.Count, _config.MaxConcurrentSearches);

        var startTime = DateTime.UtcNow;
        var completedCount = new int[] { 0 };
        using var semaphore = new SemaphoreSlim(_config.MaxConcurrentSearches);
        var tasks = runIds.Select(runId => SearchOneJob(runId, jobLookup, semaphore, completedCount, runIds.Count, ct));
        await Task.WhenAll(tasks);

        var elapsed = DateTime.UtcNow - startTime;
        _logger.LogInformation("SearchBatchStage complete: {Count} jobs searched in {Elapsed}",
            runIds.Count, elapsed);
    }

    private async Task SearchOneJob(
        int runId,
        Dictionary<int, ScrapeJobConfig> jobLookup,
        SemaphoreSlim semaphore,
        int[] completedCount,
        int total,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
            var processor = scope.ServiceProvider.GetRequiredService<IScrapeJobProcessor>();

            var run = await db.ScrapeRuns.FindAsync([runId], ct);
            if (run == null)
            {
                _logger.LogWarning("Run {RunId} not found, skipping", runId);
                return;
            }

            if (run.SearchResultsJson != null)
            {
                _logger.LogInformation("Run {RunId} already has search results, skipping", runId);
                return;
            }

            if (run.JobId == null || !jobLookup.TryGetValue(run.JobId.Value, out var job))
            {
                _logger.LogWarning("Run {RunId} has no matching job config, skipping", runId);
                return;
            }

            await processor.SearchAndPersist(run, job, ct);

            var done = Interlocked.Increment(ref completedCount[0]);
            _logger.LogInformation("Search progress: {Done}/{Total} — RunId={RunId} \"{SearchTerm}\" found {Count} listings",
                done, total, runId, job.SearchTerm, run.TotalListingsFound);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "SearchBatchStage failed for RunId={RunId}", runId);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
