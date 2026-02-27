using AIOMarketMaker.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public class ComparablesBatchStage : IBatchStage
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IComparablesEtlService _etlService;
    private readonly ILogger<ComparablesBatchStage> _logger;

    public string Name => "Finding Comparables";

    public ComparablesBatchStage(
        IServiceScopeFactory scopeFactory,
        IComparablesEtlService etlService,
        ILogger<ComparablesBatchStage> logger)
    {
        _scopeFactory = scopeFactory;
        _etlService = etlService;
        _logger = logger;
    }

    public async Task Execute(BatchContext context, CancellationToken ct = default)
    {
        _logger.LogInformation("Collecting new active listings for batch {BatchId}", context.BatchId);

        List<int> newListingIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();

            var batchStartUtc = await db.ScrapeRuns
                .Where(r => r.BatchId == context.BatchId)
                .MinAsync(r => r.StartedUtc, ct);

            var jobIds = context.Jobs.Select(j => j.Id).ToList();

            newListingIds = await db.Listings
                .Where(l => jobIds.Contains(l.ScrapeJobId)
                    && l.ListingStatus == "Active"
                    && l.CreatedUtc >= batchStartUtc)
                .Select(l => l.Id)
                .ToListAsync(ct);
        }

        _logger.LogInformation("Found {Count} new active listings across batch", newListingIds.Count);

        if (newListingIds.Count == 0)
        {
            _logger.LogInformation("No new listings to process, skipping comparables");
            return;
        }

        var result = await _etlService.RunForListings(newListingIds, ct);

        _logger.LogInformation(
            "Batch comparables complete: {Processed} processed, {Pairs} pairs classified, {Comps} comparables found",
            result.ListingsProcessed, result.PairsClassified, result.ComparablesFound);
    }
}
