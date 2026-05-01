using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Console.Tasks;

record JobRef(int Id, string SearchTerm);

public class ComparablesTask : ITask
{
    private readonly IComparablesEtlService _etl;
    private readonly IDbContextFactory<EtlDbContext> _dbContextFactory;

    public string Name => "comparables";
    public string Description => "Find comparable listings via vector search + ONNX classification";

    public ComparablesTask(IComparablesEtlService etl, IDbContextFactory<EtlDbContext> dbContextFactory)
    {
        _etl = etl;
        _dbContextFactory = dbContextFactory;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
        var jobs = await dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .OrderBy(j => j.Id)
            .Select(j => new JobRef(j.Id, j.SearchTerm))
            .ToListAsync(ct);

        if (jobs.Count == 0)
        {
            System.Console.WriteLine("No enabled scrape jobs found.");
            return 0;
        }

        System.Console.WriteLine($"Running comparables for {jobs.Count} enabled jobs");
        System.Console.WriteLine();

        var totals = new ComparablesEtlResult(0, 0, 0, 0, 0, 0);

        foreach (var job in jobs)
        {
            System.Console.WriteLine($"--- Job {job.Id}: {job.SearchTerm} ---");
            var result = await _etl.RunForJob(job.Id, ct);

            totals = new ComparablesEtlResult(
                totals.ListingsProcessed + result.ListingsProcessed,
                totals.VectorQueries + result.VectorQueries,
                totals.CandidatePairsFound + result.CandidatePairsFound,
                totals.CacheHits + result.CacheHits,
                totals.PairsClassified + result.PairsClassified,
                totals.ComparablesFound + result.ComparablesFound);

            System.Console.WriteLine($"  Processed: {result.ListingsProcessed}, Pairs: {result.CandidatePairsFound}, " +
                                     $"Classified: {result.PairsClassified}, Comparables: {result.ComparablesFound}");
        }

        System.Console.WriteLine();
        System.Console.WriteLine("Total Summary");
        System.Console.WriteLine("=============");
        System.Console.WriteLine($"Listings processed:     {totals.ListingsProcessed}");
        System.Console.WriteLine($"Vector queries:         {totals.VectorQueries}");
        System.Console.WriteLine($"Candidate pairs found:  {totals.CandidatePairsFound}");
        System.Console.WriteLine($"Cache hits:             {totals.CacheHits}");
        System.Console.WriteLine($"ONNX pairs classified:  {totals.PairsClassified}");
        System.Console.WriteLine($"Comparables found:      {totals.ComparablesFound}");

        return 0;
    }
}
