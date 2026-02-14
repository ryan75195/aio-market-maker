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

    private readonly ISemanticSearchService _searchService;
    private readonly IVariantClassifierClient _classifier;
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<ComparablesEtlService> _logger;

    public ComparablesEtlService(
        ISemanticSearchService searchService,
        IVariantClassifierClient classifier,
        EtlDbContext dbContext,
        ILogger<ComparablesEtlService> logger)
    {
        _searchService = searchService;
        _classifier = classifier;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ComparablesEtlResult> Run(bool dryRun, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting comparables ETL pipeline (dryRun={DryRun})", dryRun);

        // Step 1: Load active listings and sold listing IDs
        var activeListings = await _dbContext.Listings
            .AsNoTracking()
            .Where(l => l.ListingStatus == "Active")
            .ToListAsync(ct);

        var soldListingIds = await _dbContext.Listings
            .AsNoTracking()
            .Where(l => l.ListingStatus == "Sold")
            .Select(l => l.Id)
            .ToListAsync(ct);

        var soldIdSet = soldListingIds.ToHashSet();

        // Build lookup: all listings by ListingId (need sold listings for neighbor resolution)
        var allListings = await _dbContext.Listings
            .AsNoTracking()
            .ToDictionaryAsync(l => l.ListingId, ct);

        var allListingsById = allListings.Values.ToDictionary(l => l.Id);

        _logger.LogInformation("Loaded {Active} active listings, {Sold} sold listings",
            activeListings.Count, soldListingIds.Count);

        if (activeListings.Count == 0)
        {
            return new ComparablesEtlResult(0, 0, 0, 0, 0, 0, 0, 0);
        }

        // Load existing verdicts for cache check
        var existingVerdicts = await LoadExistingVerdicts(ct);

        // Step 2: Query Pinecone for sold neighbors of active listings
        var candidatePairs = await QueryPineconeForCandidates(
            activeListings, allListings, soldIdSet, existingVerdicts, ct);

        var cacheHits = existingVerdicts.Count;
        _logger.LogInformation("Found {Count} candidate pairs to evaluate ({CacheHits} cached verdicts skipped)",
            candidatePairs.Count, cacheHits);

        if (dryRun)
        {
            _logger.LogInformation("Dry run complete. Would classify {Count} pairs", candidatePairs.Count);
            return new ComparablesEtlResult(
                ListingsProcessed: activeListings.Count,
                PineconeQueries: activeListings.Count,
                CandidatePairsFound: candidatePairs.Count,
                CacheHits: cacheHits,
                LlmCallsRequired: candidatePairs.Count,
                LlmCallsMade: 0,
                ComparablesFound: 0,
                PredictionsWritten: 0);
        }

        // Step 3: Classify pairs in batches via ONNX model
        var verdicts = await ClassifyPairs(candidatePairs, allListingsById, ct);
        var comparablesFound = verdicts.Count(v => v.IsComparable);

        // Step 4: Store verdicts
        await StoreVerdicts(verdicts, ct);

        // Step 5: Compute predictions
        var predictionsWritten = await ComputeAndStorePredictions(allListingsById, ct);

        _logger.LogInformation(
            "ETL complete: {Active} active listings, {Pairs} pairs classified, {Comparables} comparables, {Predictions} predictions",
            activeListings.Count, verdicts.Count, comparablesFound, predictionsWritten);

        return new ComparablesEtlResult(
            ListingsProcessed: activeListings.Count,
            PineconeQueries: activeListings.Count,
            CandidatePairsFound: candidatePairs.Count,
            CacheHits: cacheHits,
            LlmCallsRequired: candidatePairs.Count,
            LlmCallsMade: verdicts.Count,
            ComparablesFound: comparablesFound,
            PredictionsWritten: predictionsWritten);
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
        List<Listing> activeListings,
        Dictionary<string, Listing> allListingsByListingId,
        HashSet<int> soldIdSet,
        HashSet<(int, int)> existingVerdicts,
        CancellationToken ct)
    {
        var allPairs = new HashSet<(int, int)>();
        var results = new List<CandidatePair>();
        var semaphore = new SemaphoreSlim(MaxPineconeConcurrency);
        var queriedCount = 0;

        var tasks = activeListings.Select(async listing =>
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

                Interlocked.Increment(ref queriedCount);
                if (queriedCount % 1000 == 0)
                {
                    _logger.LogInformation("Pinecone progress: {Count}/{Total} queries",
                        queriedCount, activeListings.Count);
                }

                var pairs = new List<CandidatePair>();
                foreach (var hit in searchResult.Hits)
                {
                    if (!allListingsByListingId.TryGetValue(hit.ListingId, out var matchedListing))
                    {
                        continue;
                    }

                    // Only keep pairs where neighbor is sold
                    if (!soldIdSet.Contains(matchedListing.Id))
                    {
                        continue;
                    }

                    var canonicalKey = GetCanonicalKey(listing.Id, matchedListing.Id);

                    // Skip already-evaluated pairs
                    if (existingVerdicts.Contains(canonicalKey))
                    {
                        continue;
                    }

                    pairs.Add(new CandidatePair(
                        listing.Id,
                        matchedListing.Id,
                        hit.Score,
                        canonicalKey));
                }
                return pairs;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var allResults = await Task.WhenAll(tasks);

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

    private async Task<List<VerdictResult>> ClassifyPairs(
        List<CandidatePair> pairs,
        Dictionary<int, Listing> listingsById,
        CancellationToken ct)
    {
        var results = new List<VerdictResult>();
        const int batchSize = 128;

        for (var i = 0; i < pairs.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = pairs.Skip(i).Take(batchSize).ToList();
            var requests = batch.Select(p =>
            {
                var a = listingsById[p.CanonicalKey.Item1];
                var b = listingsById[p.CanonicalKey.Item2];
                return new ClassifyPairRequest(
                    a.Title ?? "", a.Description ?? "",
                    b.Title ?? "", b.Description ?? "");
            }).ToList();

            var classifyResults = await _classifier.Classify(requests, ct);

            for (var j = 0; j < batch.Count; j++)
            {
                var pair = batch[j];
                var result = classifyResults[j];
                results.Add(new VerdictResult(
                    pair.CanonicalKey.Item1,
                    pair.CanonicalKey.Item2,
                    result.IsComparable,
                    $"Model: confidence={result.Confidence:F3}",
                    pair.Score));
            }

            _logger.LogInformation("Classified batch {Batch}/{Total} ({Count} pairs)",
                i / batchSize + 1, (pairs.Count + batchSize - 1) / batchSize, batch.Count);
        }

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
