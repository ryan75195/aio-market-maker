using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Etl.Activities;

public record InsertScrapeRunListingsInput(int ScrapeRunId, int ScrapeJobId, List<string> ListingIds);

public class InsertScrapeRunListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<InsertScrapeRunListingsActivity> _logger;

    public InsertScrapeRunListingsActivity(EtlDbContext dbContext, ILogger<InsertScrapeRunListingsActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(InsertScrapeRunListingsActivity))]
    public async Task Run([ActivityTrigger] InsertScrapeRunListingsInput input)
    {
        var listings = input.ListingIds.Select(listingId => new ScrapeRunListing
        {
            ScrapeRunId = input.ScrapeRunId,
            ScrapeJobId = input.ScrapeJobId,
            ListingId = listingId,
            Status = "Pending",
            CreatedUtc = DateTime.UtcNow
        });

        _dbContext.ScrapeRunListings.AddRange(listings);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Inserted {Count} ScrapeRunListings for ScrapeRunId={ScrapeRunId}",
            input.ListingIds.Count, input.ScrapeRunId);
    }
}
