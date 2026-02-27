using AIOMarketMaker.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public interface IBatchPipelineRunner
{
    Task<BatchCreationResult> CreateBatch(string triggerType, CancellationToken ct = default);
    Task Execute(Guid batchId, CancellationToken ct = default);
    Task ResumeFromPhase(Guid batchId, string phase, CancellationToken ct = default);
}

public record BatchCreationResult(Guid BatchId, int RunCount);

public class BatchPipelineRunner : IBatchPipelineRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SearchBatchStage _searchStage;
    private readonly ScrapingConfig _config;
    private readonly ILogger<BatchPipelineRunner> _logger;

    public BatchPipelineRunner(
        IServiceScopeFactory scopeFactory,
        SearchBatchStage searchStage,
        ScrapingConfig config,
        ILogger<BatchPipelineRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _searchStage = searchStage;
        _config = config;
        _logger = logger;
    }

    public async Task<BatchCreationResult> CreateBatch(string triggerType, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
        var processor = scope.ServiceProvider.GetRequiredService<IScrapeJobProcessor>();

        var jobs = await db.ScrapeJobs.WhereEffectivelyEnabled()
            .Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm, j.LastRunUtc))
            .ToListAsync(ct);

        if (jobs.Count == 0)
        {
            throw new InvalidOperationException("No enabled jobs to scrape");
        }

        var batchId = Guid.NewGuid();
        foreach (var job in jobs)
        {
            var run = await processor.CreateRun(job, triggerType, batchId);
            run.BatchPhase = "Searching";
            ct.ThrowIfCancellationRequested();
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Created batch {BatchId} with {Count} runs", batchId, jobs.Count);
        return new BatchCreationResult(batchId, jobs.Count);
    }

    public async Task Execute(Guid batchId, CancellationToken ct = default)
    {
        await ResumeFromPhase(batchId, "Searching", ct);
    }

    public async Task ResumeFromPhase(Guid batchId, string phase, CancellationToken ct = default)
    {
        _logger.LogInformation("Batch {BatchId}: resuming from phase {Phase}", batchId, phase);

        var batchStartTime = DateTime.UtcNow;
        var (runIds, jobs) = await LoadBatchState(batchId, ct);

        if (phase == "Searching")
        {
            await SetBatchPhase(batchId, "Searching", ct);
            var searchStart = DateTime.UtcNow;
            var context = new BatchContext(batchId, jobs, runIds);
            await _searchStage.Execute(context, ct);
            _logger.LogInformation("Batch {BatchId}: search phase took {Elapsed}", batchId, DateTime.UtcNow - searchStart);
            phase = "Processing";
        }

        if (phase == "Processing")
        {
            await SetBatchPhase(batchId, "Processing", ct);
            await ResetRunsForProcessing(batchId, ct);
            var processStart = DateTime.UtcNow;
            await ProcessAllJobs(batchId, runIds, jobs, ct);
            _logger.LogInformation("Batch {BatchId}: processing phase took {Elapsed}", batchId, DateTime.UtcNow - processStart);
            phase = "PostProcessing";
        }

        if (phase == "PostProcessing")
        {
            await SetBatchPhase(batchId, "PostProcessing", ct);
            // No batch-level post-stages yet — placeholder for future extensions
            phase = "Completed";
        }

        if (phase == "Completed")
        {
            await SetBatchPhase(batchId, "Completed", ct);
            await LogBatchSummary(batchId, batchStartTime, ct);
        }
    }

    private async Task<(List<int> RunIds, List<ScrapeJobConfig> Jobs)> LoadBatchState(
        Guid batchId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();

        var runs = await db.ScrapeRuns
            .Where(r => r.BatchId == batchId)
            .Select(r => new { r.Id, r.JobId, r.Status })
            .ToListAsync(ct);

        var jobIds = runs
            .Where(r => r.JobId != null)
            .Select(r => r.JobId!.Value)
            .Distinct()
            .ToList();

        var jobs = await db.ScrapeJobs
            .Where(j => jobIds.Contains(j.Id))
            .Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm, j.LastRunUtc))
            .ToListAsync(ct);

        return (runs.Select(r => r.Id).ToList(), jobs);
    }

    private async Task ProcessAllJobs(
        Guid batchId, List<int> runIds, List<ScrapeJobConfig> jobs, CancellationToken ct)
    {
        var jobLookup = jobs.ToDictionary(j => j.Id);

        // Load run→job mapping
        List<(int RunId, int JobId)> runJobPairs;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
            runJobPairs = await db.ScrapeRuns
                .Where(r => r.BatchId == batchId && r.Status != "Completed" && r.Status != "Failed")
                .Select(r => new { r.Id, JobId = r.JobId ?? 0 })
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(x => (x.Id, x.JobId)).ToList(), ct);
        }

        _logger.LogInformation("Processing {Count} runs with concurrency {Max}",
            runJobPairs.Count, _config.MaxConcurrentRuns);

        using var semaphore = new SemaphoreSlim(_config.MaxConcurrentRuns);
        var tasks = runJobPairs.Select(pair => ProcessOneJob(pair.RunId, pair.JobId, jobLookup, semaphore, ct));
        await Task.WhenAll(tasks);
    }

    private async Task ProcessOneJob(
        int runId, int jobId, Dictionary<int, ScrapeJobConfig> jobLookup,
        SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            if (!jobLookup.TryGetValue(jobId, out var job))
            {
                _logger.LogWarning("No job config for JobId={JobId}, skipping run {RunId}", jobId, runId);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<IScrapeJobProcessor>();
            var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();

            var run = await db.ScrapeRuns.FindAsync([runId], ct);
            if (run == null || run.Status == "Completed" || run.Status == "Failed")
            {
                return;
            }

            await processor.Execute(run, job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var searchTerm = jobLookup.TryGetValue(jobId, out var j) ? j.SearchTerm : "unknown";
            _logger.LogError(ex, "Processing failed for RunId={RunId} \"{SearchTerm}\"", runId, searchTerm);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task ResetRunsForProcessing(Guid batchId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();

        var updated = await db.ScrapeRuns
            .Where(r => r.BatchId == batchId && r.Status != "Completed" && r.Status != "Failed")
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "Queued")
                .SetProperty(r => r.CurrentPhase, "Queued"), ct);

        _logger.LogInformation("Batch {BatchId}: reset {Count} runs to Queued for processing", batchId, updated);
    }

    private async Task SetBatchPhase(Guid batchId, string phase, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
        var now = DateTime.UtcNow;

        var query = db.ScrapeRuns.Where(r => r.BatchId == batchId);

        if (phase == "Processing")
        {
            await query.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.BatchPhase, phase)
                .SetProperty(r => r.SearchCompletedUtc, now)
                .SetProperty(r => r.ProcessingStartedUtc, now), ct);
        }
        else
        {
            await query.ExecuteUpdateAsync(s => s.SetProperty(r => r.BatchPhase, phase), ct);
        }

        _logger.LogInformation("Batch {BatchId}: phase → {Phase}", batchId, phase);
    }

    private async Task LogBatchSummary(Guid batchId, DateTime batchStartTime, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();

        var runs = await db.ScrapeRuns
            .Where(r => r.BatchId == batchId)
            .Select(r => new
            {
                r.Status,
                r.TotalListingsFound,
                r.ListingsProcessed,
                r.ListingsAddedActive,
                r.ListingsAddedSold,
                r.ListingsUpdated,
                r.ListingsSkipped,
                r.ListingsFailed
            })
            .ToListAsync(ct);

        var totalFound = runs.Sum(r => r.TotalListingsFound);
        var totalProcessed = runs.Sum(r => r.ListingsProcessed);
        var totalAdded = runs.Sum(r => r.ListingsAddedActive + r.ListingsAddedSold);
        var totalUpdated = runs.Sum(r => r.ListingsUpdated);
        var totalSkipped = runs.Sum(r => r.ListingsSkipped);
        var totalFailed = runs.Sum(r => r.ListingsFailed);
        var completedRuns = runs.Count(r => r.Status == "Completed");
        var failedRuns = runs.Count(r => r.Status == "Failed");
        var elapsed = DateTime.UtcNow - batchStartTime;

        _logger.LogInformation(
            "Batch {BatchId} complete in {Elapsed} — " +
            "Runs: {CompletedRuns}/{TotalRuns} completed ({FailedRuns} failed) | " +
            "Listings: {TotalFound:N0} found, {TotalProcessed:N0} processed, " +
            "{TotalAdded:N0} added, {TotalUpdated:N0} updated, " +
            "{TotalSkipped:N0} skipped, {TotalFailed:N0} failed",
            batchId, elapsed,
            completedRuns, runs.Count, failedRuns,
            totalFound, totalProcessed,
            totalAdded, totalUpdated,
            totalSkipped, totalFailed);
    }
}
