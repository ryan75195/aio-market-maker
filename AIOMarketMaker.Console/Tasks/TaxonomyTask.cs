using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services.Taxonomy;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Console.Tasks;

record ListingProjection(string? Title, decimal? Price, string? ListingStatus);

public class TaxonomyTask : ITask
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly ITaxonomyService _taxonomyService;

    public string Name => "taxonomy";
    public string Description => "Run taxonomy pipeline on a scrape job. Usage: taxonomy <jobId> [maxListings]";

    public TaxonomyTask(IDbContextFactory<EtlDbContext> dbFactory, ITaxonomyService taxonomyService)
    {
        _dbFactory = dbFactory;
        _taxonomyService = taxonomyService;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0 || !int.TryParse(args[0], out var jobId))
        {
            System.Console.WriteLine("Usage: taxonomy <jobId> [maxListings]");
            System.Console.WriteLine("Example: taxonomy 1 2000");
            return 1;
        }

        var maxListings = args.Length > 1 && int.TryParse(args[1], out var max) ? max : 2000;

        // Load job and titles from database
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var job = await db.ScrapeJobs.FindAsync(new object[] { jobId }, ct);
        if (job == null)
        {
            System.Console.WriteLine($"Job {jobId} not found.");
            return 1;
        }

        System.Console.WriteLine($"Job {jobId}: \"{job.SearchTerm}\"");
        System.Console.WriteLine($"Loading listings (max {maxListings})...");

        var listings = await db.Listings
            .Where(l => l.ScrapeJobId == jobId && l.Title != null)
            .OrderBy(l => l.Id)
            .Take(maxListings)
            .Select(l => new ListingProjection(l.Title, l.Price, l.ListingStatus))
            .ToListAsync(ct);

        if (listings.Count == 0)
        {
            System.Console.WriteLine("No listings found for this job.");
            return 1;
        }

        System.Console.WriteLine($"Loaded {listings.Count} listings.");
        System.Console.WriteLine();

        // Run pipeline
        var titles = listings.Select(l => l.Title!).ToList();

        System.Console.WriteLine("Running taxonomy pipeline...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _taxonomyService.Generate(titles, job.SearchTerm, ct);
        sw.Stop();

        System.Console.WriteLine($"Completed in {sw.Elapsed.TotalSeconds:F1}s");
        System.Console.WriteLine();

        // Print axes
        System.Console.WriteLine("=== DISCOVERED AXES ===");
        System.Console.WriteLine();

        var axesList = result.Axes.ToList();
        if (axesList.Count == 0)
        {
            System.Console.WriteLine("No axes discovered. The dataset may not have enough mutually exclusive n-gram pairs.");
            return 0;
        }

        foreach (var axis in axesList)
        {
            var values = axis.Values.ToList();
            System.Console.WriteLine($"  {axis.Name} ({values.Count} values):");
            foreach (var value in values)
            {
                var forms = value.Ngrams.SelectMany(n => n.Forms).Distinct().ToList();
                var formsStr = forms.Count > 1 ? $" (also: {string.Join(", ", forms.Skip(1))})" : "";
                System.Console.WriteLine($"    - {value.Label}{formsStr}");
            }
            System.Console.WriteLine();
        }

        // Print coverage stats
        System.Console.WriteLine("=== COVERAGE ===");
        System.Console.WriteLine();
        System.Console.WriteLine($"  Coverage: {result.CoveragePercent:F1}%");
        System.Console.WriteLine($"  Conflicts: {result.ConflictPercent:F1}%");
        System.Console.WriteLine($"  Total listings: {listings.Count}");

        var assignments = result.Assignments.ToList();
        var covered = assignments.Count(a => a.Cell.Count > 0);
        var conflicts = assignments.Count(a => a.HasConflict);
        System.Console.WriteLine($"  Assigned to cells: {covered}");
        System.Console.WriteLine($"  With conflicts: {conflicts}");
        System.Console.WriteLine();

        // Print top cells
        System.Console.WriteLine("=== TOP CELLS ===");
        System.Console.WriteLine();

        var cellGroups = assignments
            .Where(a => a.Cell.Count > 0)
            .GroupBy(a => string.Join(" | ", a.Cell.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")))
            .OrderByDescending(g => g.Count())
            .Take(15)
            .ToList();

        foreach (var group in cellGroups)
        {
            System.Console.WriteLine($"  [{group.Count(),4} listings] {group.Key}");
        }
        System.Console.WriteLine();

        // Print sample assignments
        System.Console.WriteLine("=== SAMPLE ASSIGNMENTS (first 10 covered) ===");
        System.Console.WriteLine();

        var samples = assignments
            .Where(a => a.Cell.Count > 0 && !a.HasConflict)
            .Take(10)
            .ToList();

        foreach (var sample in samples)
        {
            var title = titles[sample.ListingIndex];
            var cellStr = string.Join(", ", sample.Cell.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            var truncTitle = title.Length > 60 ? title[..57] + "..." : title;
            System.Console.WriteLine($"  {truncTitle}");
            System.Console.WriteLine($"    → {cellStr}");
        }

        return 0;
    }
}
