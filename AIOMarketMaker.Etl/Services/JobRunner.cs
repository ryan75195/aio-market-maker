using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Data.Models;
using AIOMarketMaker.Etl.Services.EntityResolution;
using AIOMarketMaker.Etl.Services.VectorSearch;
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
    private readonly IEntityResolutionService _entityResolutionService;
    private readonly IProductNameIndexer _productNameIndexer;
    private readonly ILogger<JobRunner> _logger;

    public JobRunner(
        EtlDbContext dbContext,
        IEbayScraper ebayScraper,
        IEntityResolutionService entityResolutionService,
        IProductNameIndexer productNameIndexer,
        ILogger<JobRunner> logger)
    {
        _dbContext = dbContext;
        _ebayScraper = ebayScraper;
        _entityResolutionService = entityResolutionService;
        _productNameIndexer = productNameIndexer;
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
            var existingListingIds = (await _dbContext.Listings
                .Where(l => l.ScrapeJobId == job.Id)
                .Select(l => l.ListingId)
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
            var ebayProducts = items.ToList();

            _logger.LogInformation("Fetched {Count} full listings", ebayProducts.Count);

            // Convert to Listing entities (raw data) and save
            var newListings = ebayProducts
                .Where(p => p.ListingId != null)
                .Select(p => MapToListing(p, job.Id))
                .ToList();

            _dbContext.Listings.AddRange(newListings);
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Saved {Count} raw listings to database", newListings.Count);

            // Create initial history records for each listing
            var historyRecords = newListings.Select(l => new ListingStatusHistory
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

            // Entity resolution - classify and normalize products (throws on failure)
            _logger.LogInformation("Starting entity resolution for {Count} products", ebayProducts.Count);
            var resolutionResults = await _entityResolutionService.ResolveAsync(ebayProducts, ct);

            // Build lookup from eBay ListingId to database Listing
            var listingLookup = newListings.ToDictionary(l => l.ListingId, l => l);

            // Map resolution results to Product entities
            var normalizedProducts = resolutionResults
                .Where(r => listingLookup.ContainsKey(r.ListingId))
                .Select(r => MapToProduct(r, listingLookup[r.ListingId]))
                .ToList();

            _dbContext.Products.AddRange(normalizedProducts);
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Saved {Count} normalized products to database", normalizedProducts.Count);

            // Index new product names in Pinecone for future similarity search
            try
            {
                await _productNameIndexer.IndexNewProductNamesAsync(normalizedProducts, ct);
            }
            catch (Exception ex)
            {
                // Log but don't fail the job - Pinecone indexing is an enhancement
                _logger.LogWarning(ex, "Failed to index product names in Pinecone: {Message}", ex.Message);
            }

            // Update job's last run time
            job.LastRunUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(ct);

            return new JobRunResult(job.Id, true, summaries.Count, newListingIds.Length, normalizedProducts.Count, null);
        }
        catch (EntityResolutionException ex)
        {
            _logger.LogError(ex, "Entity resolution failed for job {JobId}: {Message}", job.Id, ex.Message);
            throw; // Re-throw - no fallback
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}: {Message}", job.Id, ex.Message);
            return new JobRunResult(job.Id, false, 0, 0, 0, ex.Message);
        }
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
            Images = ebayProduct.Images != null ? JsonConvert.SerializeObject(ebayProduct.Images) : null,
            Location = ebayProduct.Location,
            EndDateUtc = ebayProduct.EndDateUtc,
            CreatedUtc = DateTime.UtcNow
        };
    }

    private static Product MapToProduct(EntityResolutionResult resolution, Listing listing)
    {
        // Determine dates based on listing status
        DateTime? listedDateUtc = null;
        DateTime? soldDateUtc = null;

        if (listing.ListingStatus == "Sold")
        {
            // For sold items, use EndDateUtc as the sold date
            soldDateUtc = listing.EndDateUtc;
        }
        else
        {
            // For active/buy listings, use CreatedUtc (when we first scraped it)
            // since eBay doesn't expose the original listing date
            listedDateUtc = listing.CreatedUtc;
        }

        return new Product
        {
            ListingId = listing.Id,
            Category = resolution.Category,
            CategoryConfidence = resolution.CategoryConfidence,
            ProductName = resolution.ProductName,
            Brand = resolution.Attributes.Brand,
            Model = resolution.Attributes.Model,
            StorageCapacity = resolution.Attributes.StorageCapacity,
            Color = resolution.Attributes.Color,
            Edition = resolution.Attributes.Edition,
            VariantType = resolution.Attributes.VariantType,
            BundledItems = resolution.BundledItems != null
                ? JsonConvert.SerializeObject(resolution.BundledItems)
                : null,
            // Denormalized from Listing
            EbayListingId = listing.ListingId,
            Title = listing.Title,
            Price = listing.Price,
            Currency = listing.Currency,
            ShippingCost = listing.ShippingCost,
            Url = listing.Url,
            Condition = listing.Condition,
            ListingStatus = listing.ListingStatus,
            PurchaseFormat = listing.PurchaseFormat,
            Location = listing.Location,
            EndDateUtc = listing.EndDateUtc,
            ListedDateUtc = listedDateUtc,
            SoldDateUtc = soldDateUtc,
            ResolvedUtc = DateTime.UtcNow
        };
    }
}
