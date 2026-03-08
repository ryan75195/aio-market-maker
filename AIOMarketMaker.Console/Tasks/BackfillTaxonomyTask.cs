using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Console.Tasks;

public enum JobOutcome { Processed, Skipped, Failed }

public record ListingTitleProjection(int Id, string Title);

public record TaxonomyGenerationResult(PersistedTaxonomyRun Persisted, long DurationMs);

public class BackfillTaxonomyTask : ITask
{
    private sealed class JobScope : IDisposable
    {
        private readonly IServiceScope _scope;
        public EtlDbContext Db { get; }
        public ITaxonomyPersistenceService Persistence { get; }

        public JobScope(IServiceScope scope)
        {
            _scope = scope;
            Db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
            Persistence = scope.ServiceProvider.GetRequiredService<ITaxonomyPersistenceService>();
        }

        public void Dispose() => _scope.Dispose();
    }

    private const int MinListingsForTaxonomy = 10;

    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly ITaxonomyService _taxonomyService;
    private readonly IServiceScopeFactory _scopeFactory;

    public string Name => "backfill-taxonomy";
    public string Description => "Generate and persist taxonomy for existing scrape jobs. " +
        "Usage: backfill-taxonomy [--job <id>] [--start-from-job <id>]";

    public BackfillTaxonomyTask(
        IDbContextFactory<EtlDbContext> dbFactory,
        ITaxonomyService taxonomyService,
        IServiceScopeFactory scopeFactory)
    {
        _dbFactory = dbFactory;
        _taxonomyService = taxonomyService;
        _scopeFactory = scopeFactory;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var singleJobId = CommandHelpers.GetIntArg(args, "--job");
        var startFromJobId = CommandHelpers.GetIntArg(args, "--start-from-job") ?? 0;

        System.Console.WriteLine("=== Backfill Taxonomy ===");
        System.Console.WriteLine();

        var jobIds = await LoadJobIds(singleJobId, startFromJobId, ct);

        System.Console.WriteLine($"Jobs to process: {jobIds.Count}");
        if (startFromJobId > 0)
        {
            System.Console.WriteLine($"Starting from job ID > {startFromJobId}");
        }
        System.Console.WriteLine();

        var processed = 0;
        var skipped = 0;
        var failed = 0;
        var totalSw = Stopwatch.StartNew();

        foreach (var jobId in jobIds)
        {
            ct.ThrowIfCancellationRequested();

            var outcome = await ProcessJob(jobId, ct);
            switch (outcome)
            {
                case JobOutcome.Processed: processed++; break;
                case JobOutcome.Skipped: skipped++; break;
                case JobOutcome.Failed: failed++; break;
            }
        }

        totalSw.Stop();
        PrintSummary(processed, skipped, failed, totalSw.Elapsed);

        return failed > 0 ? 1 : 0;
    }

    private async Task<List<int>> LoadJobIds(int? singleJobId, int startFromJobId, CancellationToken ct)
    {
        if (singleJobId.HasValue)
        {
            return new List<int> { singleJobId.Value };
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ScrapeJobs
            .AsNoTracking()
            .Where(j => j.Id > startFromJobId && j.IsEnabled)
            .OrderBy(j => j.Id)
            .Select(j => j.Id)
            .ToListAsync(ct);
    }

    private JobScope CreateJobScope() => new(_scopeFactory.CreateScope());

    private async Task<JobOutcome> ProcessJob(int jobId, CancellationToken ct)
    {
        try
        {
            using var scope = CreateJobScope();

            var job = await FindJob(scope.Db, jobId, ct);
            if (job == null)
            {
                System.Console.WriteLine($"  Job {jobId}: not found, skipping");
                return JobOutcome.Skipped;
            }

            var listings = await LoadListingsWithTitles(scope.Db, jobId, ct);
            if (listings.Count < MinListingsForTaxonomy)
            {
                System.Console.WriteLine($"  Job {jobId} \"{job.SearchTerm}\": {listings.Count} listings (< {MinListingsForTaxonomy}), skipping");
                return JobOutcome.Skipped;
            }

            var generated = await GenerateAndPersistTaxonomy(jobId, listings, scope.Persistence, ct);

            System.Console.WriteLine(
                $"  Job {jobId} \"{job.SearchTerm}\": " +
                $"{listings.Count} listings, {generated.Persisted.AxisCount} axes, " +
                $"{generated.Persisted.CoveragePercent:F1}% coverage, {generated.DurationMs}ms");

            return JobOutcome.Processed;
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"  Job {jobId}: FAILED - {ex.GetType().Name}: {ex.Message}");
            return JobOutcome.Failed;
        }
    }

    private static async Task<ScrapeJob?> FindJob(EtlDbContext db, int jobId, CancellationToken ct)
    {
        return await db.ScrapeJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
    }

    private static async Task<List<ListingTitleProjection>> LoadListingsWithTitles(
        EtlDbContext db, int jobId, CancellationToken ct)
    {
        return await db.Listings
            .AsNoTracking()
            .Where(l => l.ScrapeJobId == jobId && l.Title != null)
            .OrderBy(l => l.Id)
            .Select(l => new ListingTitleProjection(l.Id, l.Title!))
            .ToListAsync(ct);
    }

    private async Task<TaxonomyGenerationResult> GenerateAndPersistTaxonomy(
        int jobId, List<ListingTitleProjection> listings,
        ITaxonomyPersistenceService persistence, CancellationToken ct)
    {
        var titles = listings.Select(l => l.Title).ToList();
        var listingIds = listings.Select(l => l.Id).ToList();

        var sw = Stopwatch.StartNew();
        var result = await _taxonomyService.Generate(titles, ct: ct);
        sw.Stop();

        var persisted = await persistence.Save(jobId, result, listingIds, (int)sw.ElapsedMilliseconds, ct);
        return new TaxonomyGenerationResult(persisted, sw.ElapsedMilliseconds);
    }

    private static void PrintSummary(int processed, int skipped, int failed, TimeSpan elapsed)
    {
        System.Console.WriteLine();
        System.Console.WriteLine("=== Summary ===");
        System.Console.WriteLine($"Processed: {processed}");
        System.Console.WriteLine($"Skipped:   {skipped}");
        System.Console.WriteLine($"Failed:    {failed}");
        System.Console.WriteLine($"Duration:  {elapsed.Minutes}m {elapsed.Seconds:D2}s");
    }
}
