using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

public class UpdateScrapeRunActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<UpdateScrapeRunActivity> _logger;

    public UpdateScrapeRunActivity(EtlDbContext dbContext, ILogger<UpdateScrapeRunActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(UpdateScrapeRunActivity))]
    public async Task Run([ActivityTrigger] UpdateScrapeRunInput input)
    {
        var run = await _dbContext.ScrapeRuns
            .FirstOrDefaultAsync(r => r.InstanceId == input.InstanceId);

        if (run == null)
        {
            _logger.LogWarning("ScrapeRun not found for instance {InstanceId}", input.InstanceId);
            return;
        }

        // Only mark as Completed/Failed if no listings are pending processing
        // Blob triggers will mark as Completed when ListingsProcessed >= TotalListingsFound
        if (!input.Success)
        {
            // Failed runs should be marked immediately
            run.CompletedUtc = DateTime.UtcNow;
            run.Status = "Failed";
            run.ErrorMessage = input.ErrorMessage;
        }
        else if (run.CurrentPhase == "Completed" &&
                 (run.TotalListingsFound == 0 || run.ListingsProcessed >= run.TotalListingsFound))
        {
            // Only mark complete when phase confirms all work is done
            // This prevents premature completion when multiple jobs overwrite each other's progress
            run.CompletedUtc = DateTime.UtcNow;
            run.Status = "Completed";
        }
        // else: listings still being processed by blob triggers, leave status as Running

        run.ListingsAdded = input.ListingsAdded;
        run.ListingsSkipped = input.ListingsSkipped;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated ScrapeRun {Id} for instance {InstanceId}: {Status}, {Added} added, {Skipped} skipped, {Processed}/{Total} processed",
            run.Id, input.InstanceId, run.Status, run.ListingsAdded, run.ListingsSkipped, run.ListingsProcessed, run.TotalListingsFound);
    }
}
