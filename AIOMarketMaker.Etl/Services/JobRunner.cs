using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Data.Models;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AIOMarketMaker.Etl.Services;

public record JobRunResult(
    int JobId,
    bool Success,
    int ListingsFound,
    int NewListingsFetched,
    int ProductsSaved,
    string? Error
);

public interface IJobRunner
{
    Task<JobRunResult> RunJobAsync(int jobId, CancellationToken ct = default);
    Task<JobRunResult> RunJobAsync(ScrapeJob job, CancellationToken ct = default);
}

public class JobRunner : IJobRunner
{
    private readonly EtlDbContext _dbContext;
    private readonly IEbayScraper _ebayScraper;
    private readonly ILogger<JobRunner> _logger;

    public JobRunner(
        EtlDbContext dbContext,
        IEbayScraper ebayScraper,
        ILogger<JobRunner> logger)
    {
        _dbContext = dbContext;
        _ebayScraper = ebayScraper;
        _logger = logger;
    }

    public async Task<JobRunResult> RunJobAsync(int jobId, CancellationToken ct = default)
    {
        var job = await _dbContext.ScrapeJobs.FindAsync(new object[] { jobId }, ct);
        if (job == null)
        {
            return new JobRunResult(jobId, false, 0, 0, 0, $"Job {jobId} not found");
        }

        return await RunJobAsync(job, ct);
    }

    public async Task<JobRunResult> RunJobAsync(ScrapeJob job, CancellationToken ct = default)
    {
        _logger.LogInformation("Processing job {JobId}: '{SearchTerm}' ({SearchType})",
            job.Id, job.SearchTerm, job.SearchType);

        try
        {
            // Parse enums from job configuration
            var buyingFormat = Enum.Parse<BuyingFormat>(job.BuyingFormat, ignoreCase: true);
            var condition = Enum.Parse<Condition>(job.Condition, ignoreCase: true);

            IEnumerable<IEbayProductSummary> searchResults;

            if (job.SearchType.Equals("SOLD", StringComparison.OrdinalIgnoreCase))
            {
                var lookbackDays = job.LookbackDays ?? 7;
                var endDate = DateTime.UtcNow;
                var startDate = endDate.AddDays(-lookbackDays);

                searchResults = await _ebayScraper.SearchSoldListings(
                    job.SearchTerm,
                    buyingFormat,
                    condition,
                    startDate,
                    endDate);
            }
            else
            {
                searchResults = await _ebayScraper.SearchActiveListings(
                    job.SearchTerm,
                    buyingFormat,
                    condition,
                    itemLimit: job.ItemLimit ?? 100);
            }

            var summaries = searchResults.ToList();
            _logger.LogInformation("Found {Count} listings from search", summaries.Count);

            // Filter out listings we already have in the database
            var existingListingIds = (await _dbContext.Products
                .Where(p => p.ScrapeJobId == job.Id)
                .Select(p => p.ListingId)
                .ToListAsync(ct))
                .ToHashSet();

            var newListingIds = summaries
                .Where(s => s.ListingId != null && !existingListingIds.Contains(s.ListingId))
                .Select(s => s.ListingId!)
                .ToArray();

            _logger.LogInformation("{NewCount} new listings to fetch (skipping {ExistingCount} existing)",
                newListingIds.Length, summaries.Count - newListingIds.Length);

            if (newListingIds.Length == 0)
            {
                _logger.LogInformation("No new listings for job {JobId}, skipping fetch", job.Id);

                // Still update last run time
                job.LastRunUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(ct);

                return new JobRunResult(job.Id, true, summaries.Count, 0, 0, null);
            }

            // Fetch full listing details
            var items = await _ebayScraper.GetItemsFromListings(newListingIds);
            var products = items.ToList();

            _logger.LogInformation("Fetched {Count} full listings", products.Count);

            // Convert to database entities and save
            var newProducts = products
                .Where(p => p.ListingId != null)
                .Select(p => MapToProduct(p, job.Id))
                .ToList();

            _dbContext.Products.AddRange(newProducts);
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Saved {Count} products to database", newProducts.Count);

            // Create initial history records for each product
            var historyRecords = newProducts.Select(p => new ProductStatusHistory
            {
                ProductId = p.Id,
                ListingStatus = p.ListingStatus ?? "Unknown",
                Price = p.Price,
                SoldDateUtc = p.EndDateUtc,
                RecordedUtc = DateTime.UtcNow,
                Source = "InitialScrape"
            }).ToList();

            _dbContext.ProductStatusHistory.AddRange(historyRecords);
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Created {Count} initial history records", historyRecords.Count);

            // Update job's last run time
            job.LastRunUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(ct);

            return new JobRunResult(job.Id, true, summaries.Count, newListingIds.Length, newProducts.Count, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}: {Message}", job.Id, ex.Message);
            return new JobRunResult(job.Id, false, 0, 0, 0, ex.Message);
        }
    }

    private static Product MapToProduct(EbayProduct ebayProduct, int scrapeJobId)
    {
        return new Product
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
            Images = ebayProduct.Images != null ? JsonConvert.SerializeObject(ebayProduct.Images) : null,
            Location = ebayProduct.Location,
            EndDateUtc = ebayProduct.EndDateUtc,
            CreatedUtc = DateTime.UtcNow
        };
    }
}
