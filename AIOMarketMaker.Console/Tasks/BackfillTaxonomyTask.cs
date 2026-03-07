using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Console.Tasks;

public class BackfillTaxonomyTask : ITask
{
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

        await using var listDb = await _dbFactory.CreateDbContextAsync(ct);

        List<int> jobIds;
        if (singleJobId.HasValue)
        {
            jobIds = new List<int> { singleJobId.Value };
        }
        else
        {
            jobIds = await listDb.ScrapeJobs
                .AsNoTracking()
                .Where(j => j.Id > startFromJobId && j.IsEnabled)
                .OrderBy(j => j.Id)
                .Select(j => j.Id)
                .ToListAsync(ct);
        }

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

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
                var persistence = scope.ServiceProvider.GetRequiredService<ITaxonomyPersistenceService>();

                var job = await db.ScrapeJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
                if (job == null)
                {
                    System.Console.WriteLine($"  Job {jobId}: not found, skipping");
                    skipped++;
                    continue;
                }

                var listings = await db.Listings
                    .AsNoTracking()
                    .Where(l => l.ScrapeJobId == jobId && l.Title != null)
                    .OrderBy(l => l.Id)
                    .Select(l => new { l.Id, l.Title })
                    .ToListAsync(ct);

                if (listings.Count < 10)
                {
                    System.Console.WriteLine($"  Job {jobId} \"{job.SearchTerm}\": {listings.Count} listings (< 10), skipping");
                    skipped++;
                    continue;
                }

                var titles = listings.Select(l => l.Title!).ToList();
                var listingIds = listings.Select(l => l.Id).ToList();

                var sw = Stopwatch.StartNew();
                var result = await _taxonomyService.Generate(titles, ct);
                sw.Stop();

                var persisted = await persistence.Save(
                    jobId, result, listingIds, (int)sw.ElapsedMilliseconds, ct);

                System.Console.WriteLine(
                    $"  Job {jobId} \"{job.SearchTerm}\": " +
                    $"{listings.Count} listings, {persisted.AxisCount} axes, " +
                    $"{persisted.CoveragePercent:F1}% coverage, {sw.ElapsedMilliseconds}ms");

                processed++;
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"  Job {jobId}: FAILED - {ex.GetType().Name}: {ex.Message}");
                failed++;
            }
        }

        totalSw.Stop();
        System.Console.WriteLine();
        System.Console.WriteLine("=== Summary ===");
        System.Console.WriteLine($"Processed: {processed}");
        System.Console.WriteLine($"Skipped:   {skipped}");
        System.Console.WriteLine($"Failed:    {failed}");
        System.Console.WriteLine($"Duration:  {totalSw.Elapsed.Minutes}m {totalSw.Elapsed.Seconds:D2}s");

        return failed > 0 ? 1 : 0;
    }
}
