using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services.Taxonomy;

namespace AIOMarketMaker.Console.Tasks;

public class ViewTaxonomyTask : ITask
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly ITaxonomyPersistenceService _persistence;

    public string Name => "view-taxonomy";
    public string Description => "View saved taxonomy for a scrape job. " +
        "Usage: view-taxonomy <jobId> | view-taxonomy --all";

    public ViewTaxonomyTask(
        IDbContextFactory<EtlDbContext> dbFactory,
        ITaxonomyPersistenceService persistence)
    {
        _dbFactory = dbFactory;
        _persistence = persistence;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length > 0 && args[0] == "--all")
        {
            return await PrintAllJobs(ct);
        }

        if (args.Length == 0 || !int.TryParse(args[0], out var jobId))
        {
            System.Console.WriteLine("Usage: view-taxonomy <jobId>");
            System.Console.WriteLine("       view-taxonomy --all");
            return 1;
        }

        return await PrintJobTaxonomy(jobId, ct);
    }

    private async Task<int> PrintJobTaxonomy(int jobId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var job = await db.ScrapeJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job == null)
        {
            System.Console.WriteLine($"Job {jobId} not found.");
            return 1;
        }

        var taxonomy = await _persistence.GetByJob(jobId, ct);
        if (taxonomy == null)
        {
            System.Console.WriteLine($"No taxonomy saved for job {jobId} \"{job.SearchTerm}\".");
            return 1;
        }

        System.Console.WriteLine($"=== Job {jobId}: \"{job.SearchTerm}\" ===");
        System.Console.WriteLine();
        System.Console.WriteLine($"  Coverage:  {taxonomy.CoveragePercent:F1}%");
        System.Console.WriteLine($"  Conflicts: {taxonomy.ConflictPercent:F1}%");
        System.Console.WriteLine($"  Listings:  {taxonomy.AssignedListings} / {taxonomy.TotalListings} assigned");
        System.Console.WriteLine($"  Axes:      {taxonomy.AxisCount}");
        System.Console.WriteLine($"  Duration:  {taxonomy.DurationMs}ms");
        System.Console.WriteLine($"  Created:   {taxonomy.CreatedUtc:yyyy-MM-dd HH:mm} UTC");
        System.Console.WriteLine();

        foreach (var axis in taxonomy.Axes)
        {
            var values = axis.Values.ToList();
            System.Console.WriteLine($"  {axis.Name} ({values.Count} values):");
            foreach (var value in values)
            {
                System.Console.WriteLine($"    - {value.Label}");
            }
            System.Console.WriteLine();
        }

        return 0;
    }

    private async Task<int> PrintAllJobs(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var runs = await db.TaxonomyRuns
            .AsNoTracking()
            .Include(r => r.ScrapeJob)
            .OrderBy(r => r.ScrapeJobId)
            .ToListAsync(ct);

        if (runs.Count == 0)
        {
            System.Console.WriteLine("No taxonomy runs found.");
            return 0;
        }

        System.Console.WriteLine($"{"Job",-6} {"Search Term",-35} {"Axes",-5} {"Coverage",-10} {"Assigned",-12} {"Duration",-10} {"Created",-20}");
        System.Console.WriteLine(new string('-', 98));

        foreach (var run in runs)
        {
            var term = run.ScrapeJob?.SearchTerm ?? "?";
            if (term.Length > 33)
            {
                term = term[..30] + "...";
            }

            System.Console.WriteLine(
                $"{run.ScrapeJobId,-6} {term,-35} {run.AxisCount,-5} " +
                $"{run.CoveragePercent,7:F1}%  {run.AssignedListings,5}/{run.TotalListings,-5} " +
                $"{run.DurationMs,6}ms   {run.CreatedUtc:yyyy-MM-dd HH:mm}");
        }

        System.Console.WriteLine();
        System.Console.WriteLine($"Total: {runs.Count} jobs with taxonomy");

        return 0;
    }
}
