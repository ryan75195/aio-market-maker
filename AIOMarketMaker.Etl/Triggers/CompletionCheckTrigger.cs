using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Triggers;

/// <summary>
/// Timer trigger that checks for completed scrape runs as a FALLBACK safety net.
/// Primary completion detection happens in ProcessListingEndpoint when the last listing is processed.
/// This trigger runs every 5 minutes to catch edge cases (crashed workers, missed messages).
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
    /// Fallback timer trigger that runs every 5 minutes to check for completed scrape runs.
    /// Primary completion happens immediately in ProcessListingEndpoint when the last listing is processed.
    /// This is a safety net for edge cases (crashed workers, queue issues, etc.).
    /// A run is considered complete when:
    /// - Status is "Running" or "Indexing"
    /// - CurrentPhase is "Indexing"
    /// - TotalListingsFound > 0
    /// - ListingsProcessed >= (TotalListingsFound - ListingsFilteredPreQueue)
    /// </summary>
    [Function("CompletionCheckTrigger")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer)
    {
        // Query for scrape runs that are eligible for completion
        // Note: Completion is based on actual listings to process, which is
        // TotalListingsFound - ListingsFilteredPreQueue (terminal status listings)
        var eligibleRuns = await _dbContext.ScrapeRuns
            .Where(r =>
                (r.Status == "Running" || r.Status == "Indexing") &&
                r.CurrentPhase == "Indexing" &&
                r.TotalListingsFound > 0 &&
                r.ListingsProcessed >= (r.TotalListingsFound - r.ListingsFilteredPreQueue))
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

            var listingsToProcess = run.TotalListingsFound - run.ListingsFilteredPreQueue;
            _logger.LogInformation(
                "Marked ScrapeRun {RunId} as completed. Processed {Processed}/{ToProcess} listings ({FilteredPreQueue} pre-filtered)",
                run.Id,
                run.ListingsProcessed,
                listingsToProcess,
                run.ListingsFilteredPreQueue);
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "CompletionCheckTrigger completed {Count} scrape runs",
            eligibleRuns.Count);
    }
}
