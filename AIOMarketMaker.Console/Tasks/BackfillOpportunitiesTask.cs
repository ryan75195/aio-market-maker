using System.Diagnostics;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Services.Taxonomy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AIOMarketMaker.Console.Tasks;

public class BackfillOpportunitiesTask : ITask
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly ITaxonomyOpportunityService _opportunityService;
    private readonly PricingOptions _options;

    public string Name => "backfill-opportunities";
    public string Description => "Compute taxonomy opportunities for all jobs. Usage: backfill-opportunities [--job <id>]";

    public BackfillOpportunitiesTask(
        IDbContextFactory<EtlDbContext> dbFactory,
        ITaxonomyOpportunityService opportunityService,
        IOptions<PricingOptions> options)
    {
        _dbFactory = dbFactory;
        _opportunityService = opportunityService;
        _options = options.Value;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var singleJobId = CommandHelpers.GetIntArg(args, "--job");

        System.Console.WriteLine("=== Backfill Taxonomy Opportunities ===");
        System.Console.WriteLine($"Fee: {_options.FeePercent}%, MinComps: {_options.MinComps}");
        System.Console.WriteLine();

        List<int> jobIds;
        if (singleJobId.HasValue)
        {
            jobIds = new List<int> { singleJobId.Value };
        }
        else
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            jobIds = await db.ScrapeJobs
                .AsNoTracking()
                .Where(j => j.IsEnabled)
                .OrderBy(j => j.Id)
                .Select(j => j.Id)
                .ToListAsync(ct);
        }

        System.Console.WriteLine($"Jobs to process: {jobIds.Count}");
        System.Console.WriteLine();

        var totalOpps = 0;
        var totalSw = Stopwatch.StartNew();

        foreach (var jobId in jobIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var sw = Stopwatch.StartNew();
                var count = await _opportunityService.Compute(
                    jobId, _options.FeePercent, _options.MinComps, ct);
                sw.Stop();
                totalOpps += count;
                System.Console.WriteLine($"  Job {jobId}: {count} opportunities ({sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"  Job {jobId}: FAILED - {ex.Message}");
            }
        }

        totalSw.Stop();
        System.Console.WriteLine();
        System.Console.WriteLine($"Total: {totalOpps} opportunities across {jobIds.Count} jobs in {totalSw.Elapsed.Minutes}m {totalSw.Elapsed.Seconds:D2}s");
        return 0;
    }
}
