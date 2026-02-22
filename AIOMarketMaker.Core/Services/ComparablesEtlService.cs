using System.Collections.Concurrent;
using System.Diagnostics;
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
    int VectorQueries,
    int CandidatePairsFound,
    int CacheHits,
    int LlmCallsRequired,
    int LlmCallsMade,
    int ComparablesFound
);

public class ComparablesEtlService : IComparablesEtlService
{
    private const int VectorTopK = 100;
    private const float MinSimilarityScore = 0.70f;
    private const int MaxSearchConcurrency = 8;
    private const int ClassifyBatchSize = 256;
    private const int SaveEveryNPairs = 5120;

    private readonly ISemanticSearchService _searchService;
    private readonly IVariantClassifierClient _classifier;
    private readonly IDbContextFactory<EtlDbContext> _dbContextFactory;
    private readonly ILogger<ComparablesEtlService> _logger;

    public ComparablesEtlService(
        ISemanticSearchService searchService,
        IVariantClassifierClient classifier,
        IDbContextFactory<EtlDbContext> dbContextFactory,
        ILogger<ComparablesEtlService> logger)
    {
        _searchService = searchService;
        _classifier = classifier;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<ComparablesEtlResult> Run(bool dryRun, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting comparables ETL pipeline (dryRun={DryRun})", dryRun);
        var sw = Stopwatch.StartNew();

        // Phase 1: Load data from DB
        List<Listing> activeListings;
        HashSet<int> soldIdSet;
        Dictionary<string, Listing> allListings;
        Dictionary<int, Listing> allListingsById;
        HashSet<(int, int)> existingVerdicts;

        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync(ct))
        {
            activeListings = await dbContext.Listings
                .AsNoTracking()
                .Where(l => l.ListingStatus == "Active")
                .ToListAsync(ct);

            var soldListingIds = await dbContext.Listings
                .AsNoTracking()
                .Where(l => l.ListingStatus == "Sold")
                .Select(l => l.Id)
                .ToListAsync(ct);

            soldIdSet = soldListingIds.ToHashSet();

            allListings = await dbContext.Listings
                .AsNoTracking()
                .ToDictionaryAsync(l => l.ListingId, ct);

            allListingsById = allListings.Values.ToDictionary(l => l.Id);

            _logger.LogInformation("Loaded {Active} active listings, {Sold} sold listings in {Elapsed}s",
                activeListings.Count, soldListingIds.Count, sw.Elapsed.TotalSeconds.ToString("F1"));

            if (activeListings.Count == 0)
            {
                return new ComparablesEtlResult(0, 0, 0, 0, 0, 0, 0);
            }

            existingVerdicts = await LoadExistingVerdicts(dbContext, ct);
            _logger.LogInformation("Loaded {Count} existing verdicts", existingVerdicts.Count);
        }

        // Phase 2: Vector search — collect all candidate pairs
        var seenPairs = new ConcurrentDictionary<(int, int), byte>();
        var candidatePairs = new ConcurrentBag<CandidatePair>();
        var cacheHits = 0;

        var searchSw = Stopwatch.StartNew();
        var queriedCount = 0;

        await Parallel.ForEachAsync(
            activeListings,
            new ParallelOptions { MaxDegreeOfParallelism = MaxSearchConcurrency, CancellationToken = ct },
            async (listing, innerCt) =>
            {
                var searchResult = await _searchService.FindSimilar(
                    listing.ListingId,
                    topK: VectorTopK,
                    ct: innerCt);

                var count = Interlocked.Increment(ref queriedCount);
                if (count % 5000 == 0)
                {
                    _logger.LogInformation("Search: {Count}/{Total} ({Pct}%), {Pairs} pairs, {Hits} cache hits",
                        count, activeListings.Count,
                        (100.0 * count / activeListings.Count).ToString("F0"),
                        candidatePairs.Count, cacheHits);
                }

                foreach (var hit in searchResult.Hits)
                {
                    if (hit.Score < MinSimilarityScore)
                    {
                        continue;
                    }

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

                    if (seenPairs.TryAdd(key, 0))
                    {
                        candidatePairs.Add(new CandidatePair(listing.Id, matched.Id, hit.Score, key));
                    }
                }
            });

        searchSw.Stop();
        _logger.LogInformation("Search phase complete: {Pairs} candidate pairs, {CacheHits} cache hits in {Elapsed}s",
            candidatePairs.Count, cacheHits, searchSw.Elapsed.TotalSeconds.ToString("F1"));

        if (dryRun)
        {
            return new ComparablesEtlResult(
                ListingsProcessed: activeListings.Count,
                VectorQueries: activeListings.Count,
                CandidatePairsFound: candidatePairs.Count,
                CacheHits: cacheHits,
                LlmCallsRequired: candidatePairs.Count,
                LlmCallsMade: 0,
                ComparablesFound: 0);
        }

        // Phase 3: Classify and save — single-threaded with batched DB saves
        var totalClassified = 0;
        var comparablesFound = 0;
        var pairsList = candidatePairs.ToList();
        var totalBatches = (pairsList.Count + ClassifyBatchSize - 1) / ClassifyBatchSize;

        _logger.LogInformation("Classifying {Total} pairs in {Batches} batches of {Size}",
            pairsList.Count, totalBatches, ClassifyBatchSize);

        var classifySw = Stopwatch.StartNew();
        var pendingEntities = new List<ListingRelationship>();

        for (var i = 0; i < pairsList.Count; i += ClassifyBatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batchSize = Math.Min(ClassifyBatchSize, pairsList.Count - i);
            var batch = pairsList.GetRange(i, batchSize);

            var requests = batch.Select(p =>
            {
                var a = allListingsById[p.CanonicalKey.Item1];
                var b = allListingsById[p.CanonicalKey.Item2];
                return new ClassifyPairRequest(
                    a.Title ?? "", a.Description ?? "",
                    b.Title ?? "", b.Description ?? "",
                    p.Score);
            }).ToList();

            var results = await _classifier.Classify(requests, ct);

            for (var j = 0; j < batch.Count; j++)
            {
                var pair = batch[j];
                var result = results[j];

                if (result.IsComparable)
                {
                    comparablesFound++;
                }

                pendingEntities.Add(new ListingRelationship
                {
                    ListingIdA = pair.CanonicalKey.Item1,
                    ListingIdB = pair.CanonicalKey.Item2,
                    IsComparable = result.IsComparable,
                    Explanation = $"Model: confidence={result.Confidence:F3}",
                    SimilarityScore = pair.Score,
                    CreatedUtc = DateTime.UtcNow
                });
            }

            totalClassified += batchSize;

            // Save accumulated results every SaveEveryNPairs or at the end
            if (pendingEntities.Count >= SaveEveryNPairs || i + ClassifyBatchSize >= pairsList.Count)
            {
                await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
                dbContext.ListingRelationships.AddRange(pendingEntities);
                await dbContext.SaveChangesAsync(ct);
                pendingEntities.Clear();
            }

            // Log progress every ~10,000 pairs
            if (totalClassified % 10240 < ClassifyBatchSize)
            {
                var elapsed = classifySw.Elapsed;
                var pairsPerSec = totalClassified / elapsed.TotalSeconds;
                var remaining = TimeSpan.FromSeconds(Math.Max(1, (pairsList.Count - totalClassified) / pairsPerSec));
                _logger.LogInformation(
                    "Progress: {Classified}/{Total} ({Pct}%), {Comps} comps, {Rate}/s, ETA {ETA}",
                    totalClassified, pairsList.Count,
                    (100.0 * totalClassified / pairsList.Count).ToString("F1"),
                    comparablesFound,
                    pairsPerSec.ToString("F0"),
                    remaining.ToString(@"d\.hh\:mm\:ss"));
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "ETL complete: {Active} active, {Pairs} classified, {Comparables} comparables in {Elapsed}",
            activeListings.Count, totalClassified, comparablesFound, sw.Elapsed.ToString(@"hh\:mm\:ss"));

        return new ComparablesEtlResult(
            ListingsProcessed: activeListings.Count,
            VectorQueries: activeListings.Count,
            CandidatePairsFound: candidatePairs.Count,
            CacheHits: cacheHits,
            LlmCallsRequired: candidatePairs.Count,
            LlmCallsMade: totalClassified,
            ComparablesFound: comparablesFound);
    }

    private static async Task<HashSet<(int, int)>> LoadExistingVerdicts(
        EtlDbContext dbContext, CancellationToken ct)
    {
        var verdicts = await dbContext.ListingRelationships
            .AsNoTracking()
            .Select(r => new { r.ListingIdA, r.ListingIdB })
            .ToListAsync(ct);

        return verdicts.Select(v => (v.ListingIdA, v.ListingIdB)).ToHashSet();
    }

    private static (int, int) GetCanonicalKey(int idA, int idB)
    {
        return idA < idB ? (idA, idB) : (idB, idA);
    }

    private record CandidatePair(int ListingIdA, int ListingIdB, float Score, (int, int) CanonicalKey);
}
