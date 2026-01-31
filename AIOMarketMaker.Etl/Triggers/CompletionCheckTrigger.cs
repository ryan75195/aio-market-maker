using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Triggers;

/// <summary>
/// Timer trigger that checks for completed scrape runs.
/// Runs every 30 seconds to mark runs as completed when all listings have been processed.
/// This replaces the SweepOrchestrator Durable Function.
/// </summary>
public class CompletionCheckTrigger
{
    private readonly ILogger<CompletionCheckTrigger> _logger;
    private readonly EtlDbContext _dbContext;

    public CompletionCheckTrigger(
        ILogger<CompletionCheckTrigger> logger,
        EtlDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    /// <summary>
    /// Timer trigger that runs every 30 seconds to check for completed scrape runs.
    /// A run is considered complete when:
    /// - Status is "Running" or "Indexing"
    /// - CurrentPhase is "Indexing"
    /// - TotalListingsFound > 0
    /// - ListingsProcessed >= TotalListingsFound
    /// </summary>
    [Function("CompletionCheckTrigger")]
    public async Task Run([TimerTrigger("*/30 * * * * *")] TimerInfo timer)
    {
        // Query for scrape runs that are eligible for completion
        var eligibleRuns = await _dbContext.ScrapeRuns
            .Where(r =>
                (r.Status == "Running" || r.Status == "Indexing") &&
                r.CurrentPhase == "Indexing" &&
                r.TotalListingsFound > 0 &&
                r.ListingsProcessed >= r.TotalListingsFound)
            .ToListAsync();

        if (eligibleRuns.Count == 0)
        {
            return;
        }

        var completedUtc = DateTime.UtcNow;

        foreach (var run in eligibleRuns)
        {
            run.Status = "Completed";
            run.CurrentPhase = "Completed";
            run.CompletedUtc = completedUtc;

            _logger.LogInformation(
                "Marked ScrapeRun {RunId} as completed. Processed {Processed}/{Total} listings",
                run.Id,
                run.ListingsProcessed,
                run.TotalListingsFound);
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "CompletionCheckTrigger completed {Count} scrape runs",
            eligibleRuns.Count);
    }
}
