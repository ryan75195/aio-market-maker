using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Data.Models;
using AIOMarketMaker.Core.Services.EntityResolution;
using AIOMarketMaker.Core.Services.VectorSearch;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Core.Services;
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
    Task<JobRunResult> RunJob(int jobId, CancellationToken ct = default);
    Task<JobRunResult> RunJob(ScrapeJob job, CancellationToken ct = default);
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
        _logger.LogInformation("Processing job {JobId}: '{SearchTerm}' ({SearchType})",
            job.Id, job.SearchTerm, job.SearchType);

        try
        {
            var searchResults = await SearchEbay(job, ct);
            var newSearchResultIds = await FilterNewListings(job.Id, searchResults, ct);

            if (newSearchResultIds.Length == 0)
            {
                _logger.LogInformation("No new listings for job {JobId}, skipping fetch", job.Id);
                await UpdateJobTimestamp(job, ct);
                return new JobRunResult(job.Id, true, searchResults.Count, 0, 0, null);
            }

            var ebayProducts = await FetchEbayProducts(newSearchResultIds);
            var listings = await SaveEbayListings(ebayProducts, job.Id, ct);
            await SaveInitialStatusHistory(listings, ct);

            var resolved = await ResolveEntities(ebayProducts, ct);
            var products = await SaveNormalizedProducts(resolved, listings, ct);

            await TryIndexProductNames(products, ct);
            await UpdateJobTimestamp(job, ct);

            return new JobRunResult(job.Id, true, searchResults.Count, newSearchResultIds.Length, products.Count, null);
        }
        catch (EntityResolutionException ex)
        {
            _logger.LogError(ex, "Entity resolution failed for job {JobId}: {Message}", job.Id, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}: {Message}", job.Id, ex.Message);
            return new JobRunResult(job.Id, false, 0, 0, 0, ex.Message);
        }
    }

    #region Pipeline Steps

    private async Task<List<IEbayProductSummary>> SearchEbay(ScrapeJob job, CancellationToken ct)
    {
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
        return summaries;
    }

    private async Task<string[]> FilterNewListings(
        int jobId,
        List<IEbayProductSummary> searchResults,
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
            newListingIds.Length, searchResults.Count - newListingIds.Length);

        return newListingIds;
    }

    private async Task<List<EbayProduct>> FetchEbayProducts(string[] listingIds)
    {
        var items = await _ebayScraper.GetItemsFromListings(listingIds);
        var ebayProducts = items.ToList();
        _logger.LogInformation("Fetched {Count} full listings", ebayProducts.Count);
        return ebayProducts;
    }

    private async Task<List<Listing>> SaveEbayListings(
        List<EbayProduct> ebayProducts,
        int jobId,
        CancellationToken ct)
    {
        var newListings = ebayProducts
            .Where(p => p.ListingId != null)
            .Select(p => MapToListing(p, jobId))
            .ToList();

        _dbContext.Listings.AddRange(newListings);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Saved {Count} raw listings to database", newListings.Count);
        return newListings;
    }

    private async Task SaveInitialStatusHistory(List<Listing> listings, CancellationToken ct)
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

    private async Task<IReadOnlyList<EntityResolutionResult>> ResolveEntities(
        List<EbayProduct> ebayProducts,
        CancellationToken ct)
    {
        _logger.LogInformation("Starting entity resolution for {Count} products", ebayProducts.Count);
        return await _entityResolutionService.Resolve(ebayProducts, ct);
    }

    private async Task<List<Product>> SaveNormalizedProducts(
        IReadOnlyList<EntityResolutionResult> resolved,
        List<Listing> listings,
        CancellationToken ct)
    {
        var listingLookup = listings.ToDictionary(l => l.ListingId, l => l);

        var normalizedProducts = resolved
            .Where(r => listingLookup.ContainsKey(r.ListingId))
            .Select(r => MapToProduct(r, listingLookup[r.ListingId]))
            .ToList();

        _dbContext.Products.AddRange(normalizedProducts);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Saved {Count} normalized products to database", normalizedProducts.Count);
        return normalizedProducts;
    }

    private async Task TryIndexProductNames(List<Product> products, CancellationToken ct)
    {
        try
        {
            await _productNameIndexer.IndexNewProductNames(products, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to index product names in Pinecone: {Message}", ex.Message);
        }
    }

    private async Task UpdateJobTimestamp(ScrapeJob job, CancellationToken ct)
    {
        job.LastRunUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
    }

    #endregion

    #region Mappers

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
        DateTime? listedDateUtc = null;
        DateTime? soldDateUtc = null;

        if (listing.ListingStatus == "Sold")
        {
            soldDateUtc = listing.EndDateUtc;
        }
        else
        {
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

    #endregion
}
