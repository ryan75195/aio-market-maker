using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Functions.Activities;

public class UpdateScrapeRunProgressActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<UpdateScrapeRunProgressActivity> _logger;

    public UpdateScrapeRunProgressActivity(EtlDbContext dbContext, ILogger<UpdateScrapeRunProgressActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(UpdateScrapeRunProgressActivity))]
    public async Task Run([ActivityTrigger] UpdateProgressInput input)
    {
        var run = await _dbContext.ScrapeRuns
            .FirstOrDefaultAsync(r => r.InstanceId == input.InstanceId);

        if (run == null)
        {
            _logger.LogWarning("ScrapeRun not found for instance {InstanceId}", input.InstanceId);
            return;
        }

        if (input.TotalListingsFound.HasValue)
            run.TotalListingsFound = input.TotalListingsFound.Value;

        if (input.ListingsProcessed.HasValue)
            run.ListingsProcessed = input.ListingsProcessed.Value;

        if (input.CurrentPhase != null)
            run.CurrentPhase = input.CurrentPhase;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Progress updated for {InstanceId}: Phase={Phase}, Found={Found}, Processed={Processed}",
            input.InstanceId, run.CurrentPhase, run.TotalListingsFound, run.ListingsProcessed);
    }
}

public record UpdateProgressInput(
    string InstanceId,
    int? TotalListingsFound = null,
    int? ListingsProcessed = null,
    string? CurrentPhase = null);
