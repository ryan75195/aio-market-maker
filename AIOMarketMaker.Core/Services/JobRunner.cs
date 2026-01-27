using System.Text.Json;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Models.Ebay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public record JobRunResult(
    int JobId,
    bool Success,
    int ListingsFound,
    int NewListingsFetched,
    int StatusUpdates,
    string? Error
);

public interface IJobRunner
{
    Task<JobRunResult> RunJob(int jobId, CancellationToken ct = default);
    Task<JobRunResult> RunJob(ScrapeJob job, CancellationToken ct = default);
}

/// <summary>
/// Runs scrape jobs to fetch eBay listings and save them to the database.
/// </summary>
public class JobRunner : IJobRunner
{
    private readonly EtlDbContext _dbContext;
    private readonly IEbayScraper _ebayScraper;
    private readonly ILogger<JobRunner> _logger;
    private readonly int _defaultLookbackDays;

    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sold", "Ended", "OutOfStock"
    };

    public JobRunner(
        EtlDbContext dbContext,
        IEbayScraper ebayScraper,
        IConfiguration configuration,
        ILogger<JobRunner> logger)
    {
        _dbContext = dbContext;
        _ebayScraper = ebayScraper;
        _logger = logger;
        _defaultLookbackDays = configuration.GetValue<int>("Scraping:DefaultLookbackDays", 90);
    }

    public async Task<JobRunResult> RunJob(int jobId, CancellationToken ct = default)
    {
        var job = await _dbContext.ScrapeJobs.FindAsync(new object[] { jobId }, ct);
        if (job == null)
        {
            return new JobRunResult(jobId, false, 0, 0, 0, $"Job {jobId} not found");
        }

        return await RunJob(job, ct);
    }

    public async Task<JobRunResult> RunJob(ScrapeJob job, CancellationToken ct = default)
    {
        _logger.LogInformation("Processing job {JobId}: '{SearchTerm}'",
            job.Id, job.SearchTerm);

        try
        {
            var (allResults, soldResultIds) = await SearchEbay(job, ct);

            // Detect and update Active→Sold transitions
            var statusUpdates = await DetectAndUpdateSoldListings(job.Id, soldResultIds, ct);

            var listingIdsToProcess = await FilterNewListings(job.Id, allResults, ct);

            if (listingIdsToProcess.Length == 0)
            {
                _logger.LogInformation("No listings to process for job {JobId}", job.Id);
                await UpdateJobTimestamp(job, ct);
                return new JobRunResult(job.Id, true, allResults.Count(), 0, statusUpdates, null);
            }

            var ebayProducts = await FetchEbayProducts(listingIdsToProcess);
            var (inserted, updated) = await UpsertListings(ebayProducts, job.Id, ct);

            await UpdateJobTimestamp(job, ct);

            return new JobRunResult(job.Id, true, allResults.Count(), inserted, statusUpdates + updated, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}: {Message}", job.Id, ex.Message);
            return new JobRunResult(job.Id, false, 0, 0, 0, ex.Message);
        }
    }

    private async Task<(List<IEbayProductSummary> AllResults, HashSet<string> SoldResultIds)> SearchEbay(ScrapeJob job, CancellationToken ct)
    {
        var allResults = new List<IEbayProductSummary>();
        var seenIds = new HashSet<string>();
        var soldResultIds = new HashSet<string>();

        var buyingFormat = BuyingFormat.ALL;
        var condition = Condition.NULL;

        // Search SOLD listings with smart lookback
        var lookbackDays = CalculateLookbackDays(job.LastRunUtc);
        var endDate = DateTime.UtcNow;
        var startDate = endDate.AddDays(-lookbackDays);

        _logger.LogInformation("Searching sold listings ({LookbackDays} day lookback)...", lookbackDays);

        var soldResults = await _ebayScraper.SearchSoldListings(
            job.SearchTerm,
            buyingFormat,
            condition,
            startDate,
            endDate);

        foreach (var result in soldResults)
        {
            if (result.ListingId != null && seenIds.Add(result.ListingId))
            {
                allResults.Add(result);
                soldResultIds.Add(result.ListingId);
            }
        }

        // Search ACTIVE listings
        _logger.LogInformation("Searching active listings...");

        var activeResults = await _ebayScraper.SearchActiveListings(
            job.SearchTerm,
            buyingFormat,
            condition,
            itemLimit: 10000);

        foreach (var result in activeResults)
        {
            if (result.ListingId != null && seenIds.Add(result.ListingId))
            {
                allResults.Add(result);
            }
        }

        _logger.LogInformation("Search complete: {TotalCount} unique listings found ({SoldCount} sold)",
            allResults.Count, soldResultIds.Count);

        return (allResults, soldResultIds);
    }

    private int CalculateLookbackDays(DateTime? lastRunUtc)
    {
        if (lastRunUtc == null)
        {
            return _defaultLookbackDays;
        }

        var daysSinceLastRun = (int)Math.Ceiling((DateTime.UtcNow - lastRunUtc.Value).TotalDays);
        return Math.Max(1, daysSinceLastRun + 1); // +1 day buffer
    }

    private async Task<int> DetectAndUpdateSoldListings(
        int jobId,
        HashSet<string> soldResultIds,
        CancellationToken ct)
    {
        // Get all active listings GLOBALLY (not per-job) that appear in sold search results
        var activeListings = await _dbContext.Listings
            .Where(l => l.ListingStatus == "Active")
            .Where(l => soldResultIds.Contains(l.ListingId))
            .Select(l => new { l.Id, l.ListingId })
            .ToListAsync(ct);

        if (activeListings.Count == 0)
        {
            _logger.LogInformation("No Active→Sold transitions detected globally");
            return 0;
        }

        // Check which ones are already marked as Sold (by another job that ran first)
        var alreadySoldIds = await _dbContext.Listings
            .Where(l => activeListings.Select(a => a.ListingId).Contains(l.ListingId))
            .Where(l => l.ListingStatus == "Sold")
            .Select(l => l.ListingId)
            .ToListAsync(ct);

        var needsRescrape = activeListings
            .Where(l => !alreadySoldIds.Contains(l.ListingId))
            .Select(l => l.ListingId)
            .ToArray();

        if (needsRescrape.Length == 0)
        {
            _logger.LogInformation("All {Count} transitions already processed by other jobs",
                alreadySoldIds.Count);
            return 0;
        }

        _logger.LogInformation("Detected {Count} Active→Sold transitions, re-scraping...",
            needsRescrape.Length);

        // Re-scrape those listings to get accurate sold price and date
        var soldProducts = await _ebayScraper.GetItemsFromListings(needsRescrape);

        var updatedCount = 0;
        foreach (var product in soldProducts)
        {
            if (product.ListingId == null) continue;

            // Update ALL matching listings (could be in multiple jobs' active list)
            var listings = await _dbContext.Listings
                .Where(l => l.ListingId == product.ListingId && l.ListingStatus == "Active")
                .ToListAsync(ct);

            foreach (var listing in listings)
            {
                listing.ListingStatus = product.ListingStatus?.ToString() ?? "Sold";
                listing.Price = product.Price;
                listing.EndDateUtc = product.EndDateUtc;
                listing.UpdatedUtc = DateTime.UtcNow;

                _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                {
                    ListingId = listing.Id,
                    ListingStatus = listing.ListingStatus,
                    Price = product.Price,
                    SoldDateUtc = product.EndDateUtc,
                    RecordedUtc = DateTime.UtcNow,
                    Source = "JobScrape"
                });

                updatedCount++;
            }

            _logger.LogInformation("Updated listing {ListingId}: Active → {NewStatus}",
                product.ListingId, product.ListingStatus);
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Updated {Count} listing records from Active to Sold", updatedCount);

        return updatedCount;
    }

    private async Task<string[]> FilterNewListings(
        int jobId,
        IEnumerable<IEbayProductSummary> searchResults,
        CancellationToken ct)
    {
        var searchResultIds = searchResults
            .Where(s => s.ListingId != null)
            .Select(s => s.ListingId!)
            .ToList();

        // Get listings with terminal status (globally, not per-job)
        var terminalListingIds = (await _dbContext.Listings
            .Where(l => searchResultIds.Contains(l.ListingId))
            .Where(l => l.ListingStatus != null && TerminalStatuses.Contains(l.ListingStatus))
            .Select(l => l.ListingId)
            .ToListAsync(ct))
            .ToHashSet();

        var newListingIds = searchResultIds
            .Where(id => !terminalListingIds.Contains(id))
            .ToArray();

        _logger.LogInformation("{NewCount} listings to process (filtered {TerminalCount} terminal)",
            newListingIds.Length, terminalListingIds.Count);

        return newListingIds;
    }

    private async Task<IEnumerable<EbayProduct>> FetchEbayProducts(string[] listingIds)
    {
        var items = await _ebayScraper.GetItemsFromListings(listingIds);
        var ebayProducts = items.ToList();
        _logger.LogInformation("Fetched {Count} full listings", ebayProducts.Count);
        return ebayProducts;
    }

    private async Task<(int Inserted, int Updated)> UpsertListings(
        IEnumerable<EbayProduct> ebayProducts,
        int jobId,
        CancellationToken ct)
    {
        var insertCount = 0;
        var updateCount = 0;

        foreach (var product in ebayProducts.Where(p => p.ListingId != null))
        {
            var existing = await _dbContext.Listings
                .FirstOrDefaultAsync(l => l.ListingId == product.ListingId, ct);

            if (existing == null)
            {
                // INSERT new listing
                var newListing = MapToListing(product, jobId);
                _dbContext.Listings.Add(newListing);
                await _dbContext.SaveChangesAsync(ct);

                // Create initial history record
                _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                {
                    ListingId = newListing.Id,
                    ListingStatus = newListing.ListingStatus ?? "Unknown",
                    Price = newListing.Price,
                    SoldDateUtc = newListing.EndDateUtc,
                    RecordedUtc = DateTime.UtcNow,
                    Source = "InitialScrape"
                });
                insertCount++;
            }
            else
            {
                // UPDATE existing listing with status protection
                var statusChanged = UpdateExistingListing(existing, product);

                if (statusChanged)
                {
                    _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                    {
                        ListingId = existing.Id,
                        ListingStatus = existing.ListingStatus ?? "Unknown",
                        Price = existing.Price,
                        SoldDateUtc = existing.EndDateUtc,
                        RecordedUtc = DateTime.UtcNow,
                        Source = "StatusUpdate"
                    });
                }
                updateCount++;
            }
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Upserted listings: {Inserted} inserted, {Updated} updated",
            insertCount, updateCount);

        return (insertCount, updateCount);
    }

    private static bool UpdateExistingListing(Listing existing, EbayProduct product)
    {
        var statusChanged = false;
        var newStatus = product.ListingStatus?.ToString();

        // Only update status if it's a forward progression
        if (ListingStatusHelper.CanUpdateStatus(existing.ListingStatus, newStatus))
        {
            if (existing.ListingStatus != newStatus)
            {
                existing.ListingStatus = newStatus;
                existing.EndDateUtc = product.EndDateUtc;
                statusChanged = true;
            }
        }

        // Always update data fields (don't touch CreatedUtc or ScrapeJobId)
        existing.Title = product.Title;
        existing.Price = product.Price;
        existing.Currency = product.Currency;
        existing.ShippingCost = product.ShippingCost;
        existing.Url = product.Url;
        existing.Condition = product.Condition?.ToString();
        existing.PurchaseFormat = product.PurchaseFormat?.ToString();
        existing.Description = product.Description;
        existing.ItemSpecifics = product.ItemSpecifics;
        existing.Location = product.Location;
        if (product.Images != null)
        {
            existing.Images = JsonSerializer.Serialize(product.Images);
        }
        existing.UpdatedUtc = DateTime.UtcNow;

        return statusChanged;
    }

    private async Task UpdateJobTimestamp(ScrapeJob job, CancellationToken ct)
    {
        job.LastRunUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
    }

    private static Listing MapToListing(EbayProduct ebayProduct, int scrapeJobId)
    {
        return new Listing
        {
            ListingId = ebayProduct.ListingId!,
            ScrapeJobId = scrapeJobId,
            Title = ebayProduct.Title,
            Price = ebayProduct.Price,
            Currency = ebayProduct.Currency,
            ShippingCost = ebayProduct.ShippingCost,
            Url = ebayProduct.Url,
            Condition = ebayProduct.Condition?.ToString(),
            ListingStatus = ebayProduct.ListingStatus?.ToString(),
            PurchaseFormat = ebayProduct.PurchaseFormat?.ToString(),
            Description = ebayProduct.Description,
            ItemSpecifics = ebayProduct.ItemSpecifics,
            Images = ebayProduct.Images != null ? JsonSerializer.Serialize(ebayProduct.Images) : null,
            Location = ebayProduct.Location,
            EndDateUtc = ebayProduct.EndDateUtc,
            CreatedUtc = DateTime.UtcNow
        };
    }
}
