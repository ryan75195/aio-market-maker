using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Functions.Contracts;

namespace AIOMarketMaker.Functions.Activities;

public class FilterNewListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<FilterNewListingsActivity> _logger;

    public FilterNewListingsActivity(
        EtlDbContext dbContext,
        ILogger<FilterNewListingsActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(FilterNewListingsActivity))]
    public async Task<List<string>> Run(
        [ActivityTrigger] FilterNewListingsInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Filtering {Count} listings for job {JobId}",
            input.ListingIds.Count, input.JobId);

        var existingListingIds = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == input.JobId)
            .Select(l => l.ListingId)
            .ToListAsync();

        var existingSet = existingListingIds.ToHashSet();

        var newListingIds = input.ListingIds
            .Where(id => !existingSet.Contains(id))
            .ToList();

        _logger.LogInformation("Found {NewCount} new listings (skipping {ExistingCount} existing)",
            newListingIds.Count, input.ListingIds.Count - newListingIds.Count);

        return newListingIds;
    }
}
