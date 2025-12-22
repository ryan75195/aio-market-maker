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
            return new JobRunResult(jobId, false, 0, 0, $"Job {jobId} not found");
        }

        return await RunJob(job, ct);
    }

    public async Task<JobRunResult> RunJob(ScrapeJob job, CancellationToken ct = default)
    {
        _logger.LogInformation("Processing job {JobId}: '{SearchTerm}'",
            job.Id, job.SearchTerm);

        try
        {
            var searchResults = await SearchEbay(job, ct);
            var newSearchResultIds = await FilterNewListings(job.Id, searchResults, ct);

            if (newSearchResultIds.Length == 0)
            {
                _logger.LogInformation("No new listings for job {JobId}, skipping fetch", job.Id);
                await UpdateJobTimestamp(job, ct);
                return new JobRunResult(job.Id, true, searchResults.Count(), 0, null);
            }

            var ebayProducts = await FetchEbayProducts(newSearchResultIds);
            var listings = await SaveEbayListings(ebayProducts, job.Id, ct);

            await SaveInitialStatusHistory(listings, ct);
            await UpdateJobTimestamp(job, ct);

            return new JobRunResult(job.Id, true, searchResults.Count(), newSearchResultIds.Length, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}: {Message}", job.Id, ex.Message);
            return new JobRunResult(job.Id, false, 0, 0, ex.Message);
        }
    }

    private async Task<IEnumerable<IEbayProductSummary>> SearchEbay(ScrapeJob job, CancellationToken ct)
    {
        var allResults = new List<IEbayProductSummary>();
        var seenIds = new HashSet<string>();

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

        _logger.LogInformation("Search complete: {TotalCount} unique listings found", allResults.Count);

        return allResults;
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

    private async Task<string[]> FilterNewListings(
        int jobId,
        IEnumerable<IEbayProductSummary> searchResults,
        CancellationToken ct)
    {
        var existingListingIds = (await _dbContext.Listings
            .Where(l => l.ScrapeJobId == jobId)
            .Select(l => l.ListingId)
            .ToListAsync(ct))
            .ToHashSet();

        var newListingIds = searchResults
            .Where(s => s.ListingId != null && !existingListingIds.Contains(s.ListingId))
            .Select(s => s.ListingId!)
            .ToArray();

        _logger.LogInformation("{NewCount} new listings to fetch (skipping {ExistingCount} existing)",
            newListingIds.Length, searchResults.Count() - newListingIds.Length);

        return newListingIds;
    }

    private async Task<IEnumerable<EbayProduct>> FetchEbayProducts(string[] listingIds)
    {
        var items = await _ebayScraper.GetItemsFromListings(listingIds);
        var ebayProducts = items.ToList();
        _logger.LogInformation("Fetched {Count} full listings", ebayProducts.Count);
        return ebayProducts;
    }

    private async Task<IEnumerable<Listing>> SaveEbayListings(
        IEnumerable<EbayProduct> ebayProducts,
        int jobId,
        CancellationToken ct)
    {
        var newListings = ebayProducts
            .Where(p => p.ListingId != null)
            .Select(p => MapToListing(p, jobId))
            .ToList();

        _dbContext.Listings.AddRange(newListings);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Saved {Count} listings to database", newListings.Count);
        return newListings;
    }

    private async Task SaveInitialStatusHistory(IEnumerable<Listing> listings, CancellationToken ct)
    {
        var historyRecords = listings.Select(l => new ListingStatusHistory
        {
            ListingId = l.Id,
            ListingStatus = l.ListingStatus ?? "Unknown",
            Price = l.Price,
            SoldDateUtc = l.EndDateUtc,
            RecordedUtc = DateTime.UtcNow,
            Source = "InitialScrape"
        }).ToList();

        _dbContext.ListingStatusHistory.AddRange(historyRecords);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Created {Count} initial history records", historyRecords.Count);
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
