using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Services;

public interface IScrapeRunCounterService
{
    Task Increment(int scrapeRunId, string status, string? listingStatus = null);
}

public class SqlScrapeRunCounterService : IScrapeRunCounterService
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<SqlScrapeRunCounterService> _logger;

    public SqlScrapeRunCounterService(EtlDbContext dbContext, ILogger<SqlScrapeRunCounterService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task Increment(int scrapeRunId, string status, string? listingStatus = null)
    {
        var isSold = listingStatus == "Sold";

        string sql = status switch
        {
            "added" when isSold => "UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1, ListingsAddedSold = ListingsAddedSold + 1 WHERE Id = {0}",
            "added" => "UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1, ListingsAddedActive = ListingsAddedActive + 1 WHERE Id = {0}",
            "updated" => "UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1, ListingsUpdated = ListingsUpdated + 1 WHERE Id = {0}",
            "skipped" => "UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1, ListingsSkipped = ListingsSkipped + 1 WHERE Id = {0}",
            "failed" => "UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1, ListingsFailed = ListingsFailed + 1 WHERE Id = {0}",
            _ => "UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1 WHERE Id = {0}"
        };

        var rowsAffected = await _dbContext.Database.ExecuteSqlRawAsync(sql, scrapeRunId);

        if (rowsAffected == 0)
        {
            _logger.LogWarning("ScrapeRun {ScrapeRunId} not found while incrementing counters", scrapeRunId);
            return;
        }

        var completionSql = @"
            UPDATE ScrapeRuns
            SET Status = 'Completed', CurrentPhase = 'Completed', CompletedUtc = {1}
            WHERE Id = {0}
              AND (Status = 'Running' OR Status = 'Indexing')
              AND CurrentPhase IN ('Indexing', 'Refreshing comparables')
              AND TotalListingsFound > 0
              AND ListingsProcessed >= (TotalListingsFound - ListingsFilteredPreQueue)";

        var completedRows = await _dbContext.Database.ExecuteSqlRawAsync(completionSql, scrapeRunId, DateTime.UtcNow);

        if (completedRows > 0)
            _logger.LogInformation("Marked ScrapeRun {ScrapeRunId} as Completed (last listing processed)", scrapeRunId);
    }
}

public class EfCoreScrapeRunCounterService : IScrapeRunCounterService
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<EfCoreScrapeRunCounterService> _logger;

    public EfCoreScrapeRunCounterService(EtlDbContext dbContext, ILogger<EfCoreScrapeRunCounterService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task Increment(int scrapeRunId, string status, string? listingStatus = null)
    {
        var scrapeRun = await _dbContext.ScrapeRuns.FirstOrDefaultAsync(sr => sr.Id == scrapeRunId);
        if (scrapeRun == null) return;

        var isSold = listingStatus == "Sold";

        scrapeRun.ListingsProcessed++;
        if (status == "added" && isSold) scrapeRun.ListingsAddedSold++;
        else if (status == "added") scrapeRun.ListingsAddedActive++;
        else if (status == "updated") scrapeRun.ListingsUpdated++;
        else if (status == "skipped") scrapeRun.ListingsSkipped++;
        else if (status == "failed") scrapeRun.ListingsFailed++;

        var listingsToProcess = scrapeRun.TotalListingsFound - scrapeRun.ListingsFilteredPreQueue;
        if ((scrapeRun.Status == "Running" || scrapeRun.Status == "Indexing") &&
            (scrapeRun.CurrentPhase == "Indexing" || scrapeRun.CurrentPhase == "Refreshing comparables") &&
            scrapeRun.TotalListingsFound > 0 &&
            scrapeRun.ListingsProcessed >= listingsToProcess)
        {
            scrapeRun.Status = "Completed";
            scrapeRun.CurrentPhase = "Completed";
            scrapeRun.CompletedUtc = DateTime.UtcNow;
            _logger.LogInformation("Marked ScrapeRun {ScrapeRunId} as Completed (last listing processed)", scrapeRunId);
        }

        await _dbContext.SaveChangesAsync();
    }
}
