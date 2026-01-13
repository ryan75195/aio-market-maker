using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Contracts;

namespace AIOMarketMaker.Functions.Activities;

public class UpdateSoldListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<UpdateSoldListingsActivity> _logger;

    public UpdateSoldListingsActivity(
        EtlDbContext dbContext,
        ILogger<UpdateSoldListingsActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(UpdateSoldListingsActivity))]
    public async Task<int> Run(
        [ActivityTrigger] UpdateSoldListingsInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Updating {Count} sold listings for job {JobId}",
            input.SoldListings.Count, input.JobId);

        var updatedCount = 0;

        foreach (var soldData in input.SoldListings)
        {
            if (string.IsNullOrEmpty(soldData.ListingId)) continue;

            var listing = await _dbContext.Listings
                .FirstOrDefaultAsync(l => l.ScrapeJobId == input.JobId && l.ListingId == soldData.ListingId);

            if (listing == null) continue;

            // Update listing with sold data
            listing.ListingStatus = soldData.ListingStatus ?? "Sold";
            listing.Price = soldData.Price;
            listing.EndDateUtc = soldData.EndDateUtc;
            listing.UpdatedUtc = DateTime.UtcNow;

            // Add status history record
            _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
            {
                ListingId = listing.Id,
                ListingStatus = listing.ListingStatus,
                Price = soldData.Price,
                SoldDateUtc = soldData.EndDateUtc,
                RecordedUtc = DateTime.UtcNow,
                Source = "JobScrape"
            });

            updatedCount++;

            _logger.LogInformation("Updated listing {ListingId}: Active -> {NewStatus}, Price: {Price}, SoldDate: {SoldDate}",
                soldData.ListingId, listing.ListingStatus, soldData.Price, soldData.EndDateUtc);
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated {Count} listings from Active to Sold for job {JobId}",
            updatedCount, input.JobId);

        return updatedCount;
    }
}
