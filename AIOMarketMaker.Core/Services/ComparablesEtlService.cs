using System.Threading.Channels;
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

        // Step 2: Pipeline — Pinecone producers → Channel → ONNX batch consumer
        var channel = Channel.CreateUnbounded<CandidatePair>();
        var seenPairs = new HashSet<(int, int)>();
        var pairsWritten = 0;
        var cacheHits = 0;

        // Producer: query Pinecone for each active listing, write sold-neighbor pairs to channel
        var producerTask = Task.Run(async () =>
        {
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

                    var count = Interlocked.Increment(ref queriedCount);
                    if (count % 1000 == 0)
                    {
                        _logger.LogInformation("Pinecone progress: {Count}/{Total}",
                            count, activeListings.Count);
                    }

                    foreach (var hit in searchResult.Hits)
                    {
                        if (!allListings.TryGetValue(hit.ListingId, out var matched))
                        {
                            continue;
                        }

                        if (!soldIdSet.Contains(matched.Id))
                        {
                            continue;
                        }

                        var key = GetCanonicalKey(listing.Id, matched.Id);

                        if (existingVerdicts.Contains(key))
                        {
                            Interlocked.Increment(ref cacheHits);
                            continue;
                        }

                        bool added;
                        lock (seenPairs)
                        {
                            added = seenPairs.Add(key);
                        }

                        if (added)
                        {
                            Interlocked.Increment(ref pairsWritten);
                            await channel.Writer.WriteAsync(
                                new CandidatePair(listing.Id, matched.Id, hit.Score, key), ct);
                        }
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            channel.Writer.Complete();
        }, ct);

        if (dryRun)
        {
            await producerTask;
            return new ComparablesEtlResult(
                ListingsProcessed: activeListings.Count,
                PineconeQueries: activeListings.Count,
                CandidatePairsFound: pairsWritten,
                CacheHits: cacheHits,
                LlmCallsRequired: pairsWritten,
                LlmCallsMade: 0,
                ComparablesFound: 0,
                PredictionsWritten: 0);
        }

        // Consumer: collect batches, classify, save verdicts
        var totalClassified = 0;
        var comparablesFound = 0;
        var batch = new List<CandidatePair>(128);

        await foreach (var pair in channel.Reader.ReadAllAsync(ct))
        {
            batch.Add(pair);

            if (batch.Count >= 128)
            {
                var batchResult = await ProcessBatch(batch, allListingsById, ct);
                totalClassified += batchResult.Classified;
                comparablesFound += batchResult.Comparables;
                batch.Clear();
            }
        }

        // Flush remaining
        if (batch.Count > 0)
        {
            var batchResult = await ProcessBatch(batch, allListingsById, ct);
            totalClassified += batchResult.Classified;
            comparablesFound += batchResult.Comparables;
        }

        await producerTask;

        // Compute predictions for all active listings (includes relationships from previous runs)
        var allActiveIds = activeListings.Select(l => l.Id).ToHashSet();
        var predictionsWritten = await ComputePredictionsForListings(allActiveIds, allListingsById, soldIdSet, ct);

        _logger.LogInformation(
            "ETL complete: {Active} active, {Pairs} classified, {Comparables} comparables, {Predictions} predictions",
            activeListings.Count, totalClassified, comparablesFound, predictionsWritten);

        return new ComparablesEtlResult(
            ListingsProcessed: activeListings.Count,
            PineconeQueries: activeListings.Count,
            CandidatePairsFound: pairsWritten,
            CacheHits: cacheHits,
            LlmCallsRequired: pairsWritten,
            LlmCallsMade: totalClassified,
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

    private record BatchResult(int Classified, int Comparables);

    private async Task<BatchResult> ProcessBatch(
        List<CandidatePair> batch,
        Dictionary<int, Listing> listingsById,
        CancellationToken ct)
    {
        // Build classify requests
        var requests = batch.Select(p =>
        {
            var a = listingsById[p.CanonicalKey.Item1];
            var b = listingsById[p.CanonicalKey.Item2];
            return new ClassifyPairRequest(
                a.Title ?? "", a.Description ?? "",
                b.Title ?? "", b.Description ?? "");
        }).ToList();

        var results = await _classifier.Classify(requests, ct);

        // Save verdicts
        var comparables = 0;
        for (var i = 0; i < batch.Count; i++)
        {
            var pair = batch[i];
            var result = results[i];

            if (result.IsComparable)
            {
                comparables++;
            }

            _dbContext.ListingRelationships.Add(new ListingRelationship
            {
                ListingIdA = pair.CanonicalKey.Item1,
                ListingIdB = pair.CanonicalKey.Item2,
                IsComparable = result.IsComparable,
                Explanation = $"Model: confidence={result.Confidence:F3}",
                SimilarityScore = pair.Score,
                CreatedUtc = DateTime.UtcNow
            });
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Batch: {Count} pairs, {Comps} comparable",
            batch.Count, comparables);

        return new BatchResult(batch.Count, comparables);
    }

    private async Task<int> ComputePredictionsForListings(
        HashSet<int> activeListingIds,
        Dictionary<int, Listing> listingsById,
        HashSet<int> soldIdSet,
        CancellationToken ct)
    {
        if (activeListingIds.Count == 0)
        {
            return 0;
        }

        var predictionsWritten = 0;

        foreach (var activeId in activeListingIds)
        {
            if (!listingsById.TryGetValue(activeId, out var activeListing))
            {
                continue;
            }

            // Find comparable sold listings from all relationships (including previous runs)
            var comparableSoldIds = await _dbContext.ListingRelationships
                .AsNoTracking()
                .Where(r => r.IsComparable &&
                    ((r.ListingIdA == activeId && soldIdSet.Contains(r.ListingIdB)) ||
                     (r.ListingIdB == activeId && soldIdSet.Contains(r.ListingIdA))))
                .Select(r => r.ListingIdA == activeId ? r.ListingIdB : r.ListingIdA)
                .Distinct()
                .ToListAsync(ct);

            if (comparableSoldIds.Count == 0)
            {
                continue;
            }

            var soldPrices = new List<decimal>();
            foreach (var soldId in comparableSoldIds)
            {
                var historyPrice = await _dbContext.ListingStatusHistory
                    .AsNoTracking()
                    .Where(h => h.ListingId == soldId && h.ListingStatus == "Sold")
                    .OrderByDescending(h => h.RecordedUtc)
                    .Select(h => h.Price)
                    .FirstOrDefaultAsync(ct);

                var price = historyPrice ?? listingsById.GetValueOrDefault(soldId)?.Price ?? 0m;
                if (price > 0)
                {
                    soldPrices.Add(price);
                }
            }

            if (soldPrices.Count == 0)
            {
                continue;
            }

            var avgSoldPrice = soldPrices.Average();
            var potentialProfit = avgSoldPrice - (activeListing.Price ?? 0m);

            var existing = await _dbContext.ListingPredictions
                .FirstOrDefaultAsync(p => p.ListingId == activeId, ct);

            if (existing != null)
            {
                existing.AverageSoldPrice = avgSoldPrice;
                existing.SimilarSoldCount = soldPrices.Count;
                existing.PotentialProfit = potentialProfit;
                existing.ComputedUtc = DateTime.UtcNow;
            }
            else
            {
                _dbContext.ListingPredictions.Add(new ListingPrediction
                {
                    ListingId = activeId,
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
}
