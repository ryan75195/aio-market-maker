using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Activities;

public record IncrementRetryCountInput(int ScrapeRunId, string ListingId);

public record IncrementRetryCountResult(int NewRetryCount);

public class IncrementRetryCountActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<IncrementRetryCountActivity> _logger;

    public IncrementRetryCountActivity(EtlDbContext dbContext, ILogger<IncrementRetryCountActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(IncrementRetryCountActivity))]
    public async Task<IncrementRetryCountResult> Run([ActivityTrigger] IncrementRetryCountInput input)
    {
        var mapping = await _dbContext.ScrapeRunListings
            .FirstOrDefaultAsync(s => s.ScrapeRunId == input.ScrapeRunId && s.ListingId == input.ListingId);

        if (mapping == null)
        {
            _logger.LogWarning(
                "IncrementRetryCountActivity: ScrapeRunListing not found for ScrapeRunId={ScrapeRunId}, ListingId={ListingId}",
                input.ScrapeRunId, input.ListingId);
            return new IncrementRetryCountResult(999); // Force failure path
        }

        mapping.RetryCount++;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "IncrementRetryCountActivity: Incremented RetryCount for {ListingId} to {RetryCount}",
            input.ListingId, mapping.RetryCount);

        return new IncrementRetryCountResult(mapping.RetryCount);
    }
}
