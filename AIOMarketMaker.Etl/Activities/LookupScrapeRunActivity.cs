using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Activities;

public record ScrapeRunLookupResult(int? ScrapeRunId, int? ScrapeJobId, bool Found, int ParseAttempts = 0);

public class LookupScrapeRunActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<LookupScrapeRunActivity> _logger;

    public LookupScrapeRunActivity(EtlDbContext dbContext, ILogger<LookupScrapeRunActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(LookupScrapeRunActivity))]
    public async Task<ScrapeRunLookupResult> Run([ActivityTrigger] string listingId)
    {
        var mapping = await _dbContext.ScrapeRunListings
            .Where(s => s.ListingId == listingId && s.Status == "Pending")
            .OrderByDescending(s => s.CreatedUtc)
            .FirstOrDefaultAsync();

        if (mapping == null)
        {
            _logger.LogWarning("No pending ScrapeRunListing found for {ListingId}", listingId);
            return new ScrapeRunLookupResult(null, null, false);
        }

        _logger.LogInformation("Found ScrapeRunListing for {ListingId}: ScrapeRunId={ScrapeRunId}, ScrapeJobId={ScrapeJobId}, ParseAttempts={ParseAttempts}",
            listingId, mapping.ScrapeRunId, mapping.ScrapeJobId, mapping.ParseAttempts);

        return new ScrapeRunLookupResult(mapping.ScrapeRunId, mapping.ScrapeJobId, true, mapping.ParseAttempts);
    }
}
