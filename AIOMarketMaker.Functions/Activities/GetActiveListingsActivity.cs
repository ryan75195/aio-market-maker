using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Functions.Activities;

public record ActiveListingInfo(int Id, string ListingId);
public record GetActiveListingsInput(int JobId);

public class GetActiveListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<GetActiveListingsActivity> _logger;

    public GetActiveListingsActivity(
        EtlDbContext dbContext,
        ILogger<GetActiveListingsActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(GetActiveListingsActivity))]
    public async Task<List<ActiveListingInfo>> Run(
        [ActivityTrigger] GetActiveListingsInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Getting active listings for job {JobId}", input.JobId);

        var activeListings = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == input.JobId && l.ListingStatus == "Active")
            .Select(l => new ActiveListingInfo(l.Id, l.ListingId))
            .ToListAsync();

        _logger.LogInformation("Found {Count} active listings for job {JobId}",
            activeListings.Count, input.JobId);

        return activeListings;
    }
}
