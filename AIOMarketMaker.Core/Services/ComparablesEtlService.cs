using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public interface IComparablesEtlService
{
    Task<ComparablesEtlResult> Run(bool dryRun, CancellationToken ct = default);
}

public record ComparablesEtlResult(
    int ListingsProcessed,
    int PineconeQueries,
    int CandidatePairsFound,
    int CacheHits,
    int LlmCallsRequired,
    int LlmCallsMade,
    int ComparablesFound,
    int PredictionsWritten
);

public class ComparablesEtlService : IComparablesEtlService
{
    private const int PineconeTopK = 50;
    private const int MaxPineconeConcurrency = 10;
    private const int MaxLlmConcurrency = 20;

    private readonly ISemanticSearchService _searchService;
    private readonly IListingComparisonService _comparisonService;
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<ComparablesEtlService> _logger;

    public ComparablesEtlService(
        ISemanticSearchService searchService,
        IListingComparisonService comparisonService,
        EtlDbContext dbContext,
        ILogger<ComparablesEtlService> logger)
    {
        _searchService = searchService;
        _comparisonService = comparisonService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ComparablesEtlResult> Run(bool dryRun, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting comparables ETL pipeline (dryRun={DryRun})", dryRun);

        // Step 1: Load all listings from DB
        var listings = await _dbContext.Listings
            .AsNoTracking()
            .ToListAsync(ct);

        _logger.LogInformation("Loaded {Count} listings from database", listings.Count);

        // Step 2: Filter to listings indexed in Pinecone
        var indexedListings = await FilterToIndexedListings(listings, ct);
        _logger.LogInformation("{Count} listings indexed in Pinecone", indexedListings.Count);

        if (indexedListings.Count == 0)
        {
            return new ComparablesEtlResult(0, 0, 0, 0, 0, 0, 0, 0);
        }

        // Build lookup dictionary for quick access
        var listingsById = listings.ToDictionary(l => l.Id);
        var listingsByListingId = listings.ToDictionary(l => l.ListingId);

        // Load existing verdicts into a HashSet for O(1) cache lookup
        var existingVerdicts = await LoadExistingVerdicts(ct);

        // Step 3: Query Pinecone for similar candidates (parallel)
        var candidatePairs = await QueryPineconeForCandidates(indexedListings, listingsByListingId, existingVerdicts, ct);
        _logger.LogInformation("Found {Count} candidate pairs to evaluate", candidatePairs.Count);

        // Step 4: Check verdict cache — filter to uncached pairs
        var uncachedPairs = candidatePairs
            .Where(p => !existingVerdicts.Contains(p.CanonicalKey))
            .ToList();

        var cacheHits = candidatePairs.Count - uncachedPairs.Count;
        _logger.LogInformation("{CacheHits} pairs already cached, {Uncached} pairs need LLM evaluation",
            cacheHits, uncachedPairs.Count);

        if (dryRun)
        {
            _logger.LogInformation("Dry run complete. Would make {Count} LLM calls", uncachedPairs.Count);
            return new ComparablesEtlResult(
                ListingsProcessed: indexedListings.Count,
                PineconeQueries: indexedListings.Count,
                CandidatePairsFound: candidatePairs.Count,
                CacheHits: cacheHits,
                LlmCallsRequired: uncachedPairs.Count,
                LlmCallsMade: 0,
                ComparablesFound: 0,
                PredictionsWritten: 0
            );
        }

        // Step 5: Call LLM for uncached pairs (parallel)
        var verdicts = await EvaluatePairsWithLlm(uncachedPairs, listingsById, ct);
        var comparablesFound = verdicts.Count(v => v.IsComparable);

        // Step 6: Store verdicts in ListingRelationships
        await StoreVerdicts(verdicts, ct);

        // Step 7: Compute aggregates and upsert to ListingPredictions
        var predictionsWritten = await ComputeAndStorePredictions(listingsById, ct);

        _logger.LogInformation("ETL complete: {Processed} listings, {LlmCalls} LLM calls, {Comparables} comparables, {Predictions} predictions",
            indexedListings.Count, verdicts.Count, comparablesFound, predictionsWritten);

        return new ComparablesEtlResult(
            ListingsProcessed: indexedListings.Count,
            PineconeQueries: indexedListings.Count,
            CandidatePairsFound: candidatePairs.Count,
            CacheHits: cacheHits,
            LlmCallsRequired: uncachedPairs.Count,
            LlmCallsMade: verdicts.Count,
            ComparablesFound: comparablesFound,
            PredictionsWritten: predictionsWritten
        );
    }

    private async Task<List<Listing>> FilterToIndexedListings(List<Listing> listings, CancellationToken ct)
    {
        var semaphore = new SemaphoreSlim(MaxPineconeConcurrency);
        var tasks = listings.Select(async listing =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var exists = await _searchService.Exists(listing.ListingId, ct);
                return exists ? listing : null;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(l => l != null).Cast<Listing>().ToList();
    }

    private async Task<HashSet<(int, int)>> LoadExistingVerdicts(CancellationToken ct)
    {
        var verdicts = await _dbContext.ListingRelationships
            .AsNoTracking()
            .Select(r => new { r.ListingIdA, r.ListingIdB })
            .ToListAsync(ct);

        return verdicts.Select(v => (v.ListingIdA, v.ListingIdB)).ToHashSet();
    }

    private async Task<List<CandidatePair>> QueryPineconeForCandidates(
        List<Listing> indexedListings,
        Dictionary<string, Listing> listingsByListingId,
        HashSet<(int, int)> existingVerdicts,
        CancellationToken ct)
    {
        var allPairs = new HashSet<(int, int)>();
        var results = new List<CandidatePair>();
        var semaphore = new SemaphoreSlim(MaxPineconeConcurrency);

        var tasks = indexedListings.Select(async listing =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var searchResult = await _searchService.FindSimilar(
                    listing.ListingId,
                    filterToListingIds: null,
                    metadataFilter: null,
                    topK: PineconeTopK,
                    ct: ct);

                var pairs = new List<CandidatePair>();
                foreach (var hit in searchResult.Hits)
                {
                    if (!listingsByListingId.TryGetValue(hit.ListingId, out var matchedListing))
                    {
                        continue;
                    }

                    var canonicalKey = GetCanonicalKey(listing.Id, matchedListing.Id);

                    pairs.Add(new CandidatePair(
                        listing.Id,
                        matchedListing.Id,
                        hit.Score,
                        canonicalKey
                    ));
                }
                return pairs;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var allResults = await Task.WhenAll(tasks);

        // Dedupe pairs using canonical ordering
        foreach (var pairList in allResults)
        {
            foreach (var pair in pairList)
            {
                if (allPairs.Add(pair.CanonicalKey))
                {
                    results.Add(pair);
                }
            }
        }

        return results;
    }

    private async Task<List<VerdictResult>> EvaluatePairsWithLlm(
        List<CandidatePair> pairs,
        Dictionary<int, Listing> listingsById,
        CancellationToken ct)
    {
        var semaphore = new SemaphoreSlim(MaxLlmConcurrency);
        var results = new List<VerdictResult>();
        var lockObj = new object();

        var tasks = pairs.Select(async pair =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var listingA = listingsById[pair.CanonicalKey.Item1];
                var listingB = listingsById[pair.CanonicalKey.Item2];

                var verdict = await _comparisonService.Compare(listingA, listingB, ct);

                lock (lockObj)
                {
                    results.Add(new VerdictResult(
                        pair.CanonicalKey.Item1,
                        pair.CanonicalKey.Item2,
                        verdict.IsComparable,
                        verdict.Explanation,
                        pair.Score
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to evaluate pair ({IdA}, {IdB})",
                    pair.CanonicalKey.Item1, pair.CanonicalKey.Item2);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task StoreVerdicts(List<VerdictResult> verdicts, CancellationToken ct)
    {
        foreach (var verdict in verdicts)
        {
            _dbContext.ListingRelationships.Add(new ListingRelationship
            {
                ListingIdA = verdict.ListingIdA,
                ListingIdB = verdict.ListingIdB,
                IsComparable = verdict.IsComparable,
                Explanation = verdict.Explanation,
                SimilarityScore = verdict.SimilarityScore,
                CreatedUtc = DateTime.UtcNow
            });
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    private async Task<int> ComputeAndStorePredictions(
        Dictionary<int, Listing> listingsById,
        CancellationToken ct)
    {
        // Get active listings
        var activeListingIds = listingsById.Values
            .Where(l => l.ListingStatus == "Active")
            .Select(l => l.Id)
            .ToHashSet();

        if (activeListingIds.Count == 0)
        {
            return 0;
        }

        // Load all comparable relationships for active listings
        var relationships = await _dbContext.ListingRelationships
            .AsNoTracking()
            .Where(r => r.IsComparable &&
                (activeListingIds.Contains(r.ListingIdA) || activeListingIds.Contains(r.ListingIdB)))
            .ToListAsync(ct);

        // Load sold status history to get sold prices
        var soldListingIds = listingsById.Values
            .Where(l => l.ListingStatus == "Sold")
            .Select(l => l.Id)
            .ToHashSet();

        var soldHistory = await _dbContext.ListingStatusHistory
            .AsNoTracking()
            .Where(h => soldListingIds.Contains(h.ListingId) && h.ListingStatus == "Sold")
            .GroupBy(h => h.ListingId)
            .Select(g => new { ListingId = g.Key, SoldPrice = g.OrderByDescending(h => h.RecordedUtc).First().Price })
            .ToDictionaryAsync(x => x.ListingId, x => x.SoldPrice, ct);

        // For each active listing, compute prediction
        var predictionsWritten = 0;
        foreach (var activeListingId in activeListingIds)
        {
            var activeListing = listingsById[activeListingId];

            // Find comparable sold listings
            var comparableSoldIds = relationships
                .Where(r =>
                    (r.ListingIdA == activeListingId && soldListingIds.Contains(r.ListingIdB)) ||
                    (r.ListingIdB == activeListingId && soldListingIds.Contains(r.ListingIdA)))
                .Select(r => r.ListingIdA == activeListingId ? r.ListingIdB : r.ListingIdA)
                .Distinct()
                .ToList();

            if (comparableSoldIds.Count == 0)
            {
                continue;
            }

            // Get sold prices from history, falling back to listing price
            var soldPrices = comparableSoldIds
                .Select(id =>
                {
                    if (soldHistory.TryGetValue(id, out var historyPrice) && historyPrice.HasValue)
                    {
                        return historyPrice.Value;
                    }
                    return listingsById[id].Price ?? 0m;
                })
                .Where(p => p > 0)
                .ToList();

            if (soldPrices.Count == 0)
            {
                continue;
            }

            var avgSoldPrice = soldPrices.Average();
            var potentialProfit = avgSoldPrice - (activeListing.Price ?? 0m);

            // Upsert prediction
            var existingPrediction = await _dbContext.ListingPredictions
                .FirstOrDefaultAsync(p => p.ListingId == activeListingId, ct);

            if (existingPrediction != null)
            {
                existingPrediction.AverageSoldPrice = avgSoldPrice;
                existingPrediction.SimilarSoldCount = soldPrices.Count;
                existingPrediction.PotentialProfit = potentialProfit;
                existingPrediction.ComputedUtc = DateTime.UtcNow;
            }
            else
            {
                _dbContext.ListingPredictions.Add(new ListingPrediction
                {
                    ListingId = activeListingId,
                    AverageSoldPrice = avgSoldPrice,
                    SimilarSoldCount = soldPrices.Count,
                    PotentialProfit = potentialProfit,
                    ComputedUtc = DateTime.UtcNow
                });
            }

            predictionsWritten++;
        }

        await _dbContext.SaveChangesAsync(ct);
        return predictionsWritten;
    }

    private static (int, int) GetCanonicalKey(int idA, int idB)
    {
        return idA < idB ? (idA, idB) : (idB, idA);
    }

    private record CandidatePair(int ListingIdA, int ListingIdB, float Score, (int, int) CanonicalKey);

    private record VerdictResult(int ListingIdA, int ListingIdB, bool IsComparable, string Explanation, double SimilarityScore);
}
