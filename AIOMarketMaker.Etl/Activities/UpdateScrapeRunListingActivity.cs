using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Activities;

public record UpdateScrapeRunListingInput(int ScrapeRunId, string ListingId, string Status, bool IsNewListing = false);

public class UpdateScrapeRunListingActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<UpdateScrapeRunListingActivity> _logger;

    public UpdateScrapeRunListingActivity(EtlDbContext dbContext, ILogger<UpdateScrapeRunListingActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(UpdateScrapeRunListingActivity))]
    public async Task Run([ActivityTrigger] UpdateScrapeRunListingInput input)
    {
        var mapping = await _dbContext.ScrapeRunListings
            .FirstOrDefaultAsync(s => s.ScrapeRunId == input.ScrapeRunId && s.ListingId == input.ListingId);

        if (mapping == null)
        {
            _logger.LogWarning("ScrapeRunListing not found for ScrapeRunId={ScrapeRunId}, ListingId={ListingId}",
                input.ScrapeRunId, input.ListingId);
            return;
        }

        mapping.Status = input.Status;
        if (input.Status == "Complete" || input.Status == "Failed")
        {
            mapping.CompletedUtc = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();

        // If completing, also increment ScrapeRun progress
        if (input.Status == "Complete")
        {
            var addedIncrement = input.IsNewListing ? 1 : 0;
            var skippedIncrement = input.IsNewListing ? 0 : 1;

            await _dbContext.Database.ExecuteSqlRawAsync(@"
                UPDATE ScrapeRuns
                SET ListingsProcessed = ListingsProcessed + 1,
                    ListingsAdded = ListingsAdded + {1},
                    ListingsSkipped = ListingsSkipped + {2},
                    Status = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                  AND TotalListingsFound > 0
                                  AND Status = 'Running' THEN 'Completed' ELSE Status END,
                    CompletedUtc = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                        AND TotalListingsFound > 0
                                        AND Status = 'Running' THEN GETUTCDATE() ELSE CompletedUtc END,
                    CurrentPhase = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                        AND TotalListingsFound > 0
                                        AND Status = 'Running' THEN 'Completed' ELSE CurrentPhase END
                WHERE Id = {0}", input.ScrapeRunId, addedIncrement, skippedIncrement);
        }

        _logger.LogInformation("Updated ScrapeRunListing {ListingId} to {Status}", input.ListingId, input.Status);
    }
}
