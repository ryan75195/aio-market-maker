using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pinecone;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Core.Services;

public interface IComparablesRefreshService
{
    Task<ComparablesRefreshResult> Refresh(
        IEnumerable<Listing> activeListings,
        CancellationToken ct = default);
}

public record ComparablesRefreshResult(int ListingsProcessed, int ComparablesFound);

public class ComparablesRefreshService : IComparablesRefreshService
{
    private const int TopK = 50;
    private const int MaxConcurrency = 10;

    private static readonly Metadata SoldFilter = new()
    {
        ["listingStatus"] = new Metadata { ["$eq"] = "Sold" }
    };

    private readonly ISemanticSearchService _searchService;
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<ComparablesRefreshService> _logger;

    public ComparablesRefreshService(
        ISemanticSearchService searchService,
        EtlDbContext dbContext,
        ILogger<ComparablesRefreshService> logger)
    {
        _searchService = searchService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ComparablesRefreshResult> Refresh(
        IEnumerable<Listing> activeListings,
        CancellationToken ct = default)
    {
        var listings = activeListings.ToList();
        var totalComparables = 0;

        var semaphore = new SemaphoreSlim(MaxConcurrency);
        var results = new List<(Listing Listing, SemanticSearchResult Result)>();

        // Query Pinecone in parallel
        var tasks = listings.Select(async listing =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var result = await _searchService.FindSimilar(
                    listing.ListingId, metadataFilter: SoldFilter, topK: TopK, ct: ct);
                lock (results)
                {
                    results.Add((listing, result));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to find similar listings for {ListingId}, skipping",
                    listing.ListingId);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Collect all hit listing IDs for batch DB lookup
        var allHitListingIds = results
            .SelectMany(r => r.Result.Hits.Select(h => h.ListingId))
            .Distinct()
            .ToList();

        var listingIdToDbId = await _dbContext.Listings
            .Where(l => allHitListingIds.Contains(l.ListingId))
            .ToDictionaryAsync(l => l.ListingId, l => l.Id, ct);

        // Delete old comparables for all processed listings
        var processedListingIds = results.Select(r => r.Listing.Id).ToList();
        var oldComparables = await _dbContext.ListingPricingComparables
            .Where(c => processedListingIds.Contains(c.ListingId))
            .ToListAsync(ct);
        _dbContext.ListingPricingComparables.RemoveRange(oldComparables);

        // Insert new comparables
        foreach (var (listing, result) in results)
        {
            foreach (var hit in result.Hits)
            {
                if (!listingIdToDbId.TryGetValue(hit.ListingId, out var comparableDbId))
                {
                    continue;
                }

                _dbContext.ListingPricingComparables.Add(new ListingPricingComparable
                {
                    ListingId = listing.Id,
                    ComparableListingId = comparableDbId,
                    SimilarityScore = hit.Score
                });
                totalComparables++;
            }
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Refreshed comparables: {ListingsProcessed} listings, {ComparablesFound} comparables",
            listings.Count, totalComparables);

        return new ComparablesRefreshResult(listings.Count, totalComparables);
    }
}

public class NullComparablesRefreshService : IComparablesRefreshService
{
    public Task<ComparablesRefreshResult> Refresh(
        IEnumerable<Listing> activeListings, CancellationToken ct = default)
        => Task.FromResult(new ComparablesRefreshResult(0, 0));
}
