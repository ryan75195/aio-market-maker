using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Functions.Activities;

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

        run.CompletedUtc = DateTime.UtcNow;
        run.Status = input.Success ? "Completed" : "Failed";
        run.ListingsAdded = input.ListingsAdded;
        run.ListingsSkipped = input.ListingsSkipped;
        run.ErrorMessage = input.ErrorMessage;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated ScrapeRun {Id} for instance {InstanceId}: {Status}, {Added} added, {Skipped} skipped",
            run.Id, input.InstanceId, run.Status, run.ListingsAdded, run.ListingsSkipped);
    }
}

public record UpdateScrapeRunInput(
    string InstanceId,
    bool Success,
    int ListingsAdded,
    int ListingsSkipped,
    string? ErrorMessage);
