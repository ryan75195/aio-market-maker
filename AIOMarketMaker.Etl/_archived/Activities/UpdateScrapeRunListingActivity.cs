using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Activities;

public record UpdateScrapeRunListingInput(
    int ScrapeRunId,
    string ListingId,
    string Status,
    bool IsNewListing = false,
    string? ErrorMessage = null,
    int? IncrementParseAttempts = null,
    string? FailureReason = null,
    string? FailureDetails = null,
    string? ListingStatus = null);

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
        if (input.Status == "Complete" || input.Status == "Failed" || input.Status == "Skipped")
        {
            mapping.CompletedUtc = DateTime.UtcNow;
        }

        if (input.Status == "Failed" && input.ErrorMessage != null)
        {
            mapping.ErrorMessage = input.ErrorMessage;
        }

        // Handle parse retry tracking
        if (input.IncrementParseAttempts.HasValue)
        {
            mapping.ParseAttempts += input.IncrementParseAttempts.Value;
        }

        if (input.FailureReason != null)
        {
            mapping.FailureReason = input.FailureReason;
        }

        if (input.FailureDetails != null)
        {
            mapping.FailureDetails = input.FailureDetails;
        }

        await _dbContext.SaveChangesAsync();

        // If completing, skipping, or failing, increment ScrapeRun progress so the run can finish
        if (input.Status == "Complete" || input.Status == "Failed" || input.Status == "Skipped")
        {
            var isSold = input.ListingStatus == "Sold";
            // Count all completed active/sold listings (both new inserts and updates)
            var addedActiveIncrement = input.Status == "Complete" && !isSold ? 1 : 0;
            var addedSoldIncrement = input.Status == "Complete" && isSold ? 1 : 0;
            // Only count as skipped if actually skipped (not processed)
            var skippedIncrement = input.Status == "Skipped" ? 1 : 0;
            var failedIncrement = input.Status == "Failed" ? 1 : 0;

            await _dbContext.Database.ExecuteSqlRawAsync(@"
                UPDATE ScrapeRuns
                SET ListingsProcessed = ListingsProcessed + 1,
                    ListingsAddedActive = ListingsAddedActive + {1},
                    ListingsAddedSold = ListingsAddedSold + {2},
                    ListingsSkipped = ListingsSkipped + {3},
                    ListingsFailed = ListingsFailed + {4},
                    Status = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                  AND TotalListingsFound > 0
                                  AND Status = 'Running' THEN 'Completed' ELSE Status END,
                    CompletedUtc = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                        AND TotalListingsFound > 0
                                        AND Status = 'Running' THEN GETUTCDATE() ELSE CompletedUtc END,
                    CurrentPhase = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                        AND TotalListingsFound > 0
                                        AND Status = 'Running' THEN 'Completed' ELSE CurrentPhase END
                WHERE Id = {0}", input.ScrapeRunId, addedActiveIncrement, addedSoldIncrement, skippedIncrement, failedIncrement);
        }

        _logger.LogInformation("Updated ScrapeRunListing {ListingId} to {Status}", input.ListingId, input.Status);
    }
}
