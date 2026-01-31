using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

public class FilterNewListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<FilterNewListingsActivity> _logger;

    // Terminal statuses that should be filtered globally
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sold", "Ended", "OutOfStock"
    };

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

        // Get all listings with terminal status (globally, not per-job)
        var terminalListingIds = await _dbContext.Listings
            .Where(l => input.ListingIds.Contains(l.ListingId))
            .Where(l => l.ListingStatus != null && TerminalStatuses.Contains(l.ListingStatus))
            .Select(l => l.ListingId)
            .ToListAsync();

        var terminalSet = terminalListingIds.ToHashSet();

        var newListingIds = input.ListingIds
            .Where(id => !terminalSet.Contains(id))
            .ToList();

        _logger.LogInformation("Found {NewCount} listings to process (filtered {TerminalCount} terminal)",
            newListingIds.Count, terminalSet.Count);

        return newListingIds;
    }
}
