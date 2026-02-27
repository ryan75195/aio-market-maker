using System.Collections.Concurrent;
using System.Diagnostics;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public interface IComparablesEtlService
{
    Task<ComparablesEtlResult> RunForJob(int jobId, CancellationToken ct = default);
    Task<ComparablesEtlResult> RunForListings(IEnumerable<int> listingIds, CancellationToken ct = default);
}

public record ComparablesEtlResult(
    int ListingsProcessed,
    int VectorQueries,
    int CandidatePairsFound,
    int CacheHits,
    int PairsClassified,
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

    public async Task<ComparablesEtlResult> RunForJob(int jobId, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting comparables ETL pipeline (scoped, jobId={JobId})", jobId);
        var sw = Stopwatch.StartNew();

        // Phase 1: Load only the job's active listings and their existing verdicts
        List<Listing> activeListings;
        HashSet<(int, int)> existingVerdicts;

        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync(ct))
        {
            activeListings = await dbContext.Listings
                .AsNoTracking()
                .Where(l => l.ListingStatus == "Active" && l.ScrapeJobId == jobId)
                .ToListAsync(ct);

            _logger.LogInformation("Loaded {Active} active listings for job {JobId} in {Elapsed}s",
                activeListings.Count, jobId, sw.Elapsed.TotalSeconds.ToString("F1"));

            if (activeListings.Count == 0)
            {
                return new ComparablesEtlResult(0, 0, 0, 0, 0, 0);
            }

            // Load only verdicts involving this job's listings (both sides of the pair)
            var activeIds = activeListings.Select(l => l.Id).ToHashSet();
            existingVerdicts = await LoadExistingVerdictsForListings(dbContext, activeIds, ct);
            _logger.LogInformation("Loaded {Count} existing verdicts for job listings", existingVerdicts.Count);
        }

        return await SearchAndClassify(activeListings, existingVerdicts, sw, ct);
    }

    public async Task<ComparablesEtlResult> RunForListings(IEnumerable<int> listingIds, CancellationToken ct = default)
    {
        var idSet = listingIds.ToHashSet();
        if (idSet.Count == 0)
        {
            return new ComparablesEtlResult(0, 0, 0, 0, 0, 0);
        }

        _logger.LogInformation("Starting comparables ETL for {Count} specific listings", idSet.Count);
        var sw = Stopwatch.StartNew();

        List<Listing> targetListings;
        HashSet<(int, int)> existingVerdicts;

        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync(ct))
        {
            targetListings = await dbContext.Listings
                .AsNoTracking()
                .Where(l => idSet.Contains(l.Id))
                .ToListAsync(ct);

            _logger.LogInformation("Loaded {Count} target listings in {Elapsed}s",
                targetListings.Count, sw.Elapsed.TotalSeconds.ToString("F1"));

            if (targetListings.Count == 0)
            {
                return new ComparablesEtlResult(0, 0, 0, 0, 0, 0);
            }

            var targetIds = targetListings.Select(l => l.Id).ToHashSet();
            existingVerdicts = await LoadExistingVerdictsForListings(dbContext, targetIds, ct);
            _logger.LogInformation("Loaded {Count} existing verdicts for target listings", existingVerdicts.Count);
        }

        return await SearchAndClassify(targetListings, existingVerdicts, sw, ct);
    }

    private async Task<ComparablesEtlResult> SearchAndClassify(
        List<Listing> targetListings,
        HashSet<(int, int)> existingVerdicts,
        Stopwatch sw,
        CancellationToken ct)
    {
        // Phase 2: Vector search — collect candidate listing IDs (strings) for later resolution
        var seenPairs = new ConcurrentDictionary<(int, string), byte>(); // (activeId, matchedListingId)
        var candidateHits = new ConcurrentBag<ScopedSearchHit>();
        var cacheHits = 0;

        var searchSw = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            targetListings,
            new ParallelOptions { MaxDegreeOfParallelism = MaxSearchConcurrency, CancellationToken = ct },
            async (listing, innerCt) =>
            {
                var searchResult = await _searchService.FindSimilar(
                    listing.ListingId,
                    topK: VectorTopK,
                    ct: innerCt);

                foreach (var hit in searchResult.Hits)
                {
                    if (hit.Score < MinSimilarityScore)
                    {
                        continue;
                    }

                    // Skip self-matches
                    if (hit.ListingId == listing.ListingId)
                    {
                        continue;
                    }

                    if (seenPairs.TryAdd((listing.Id, hit.ListingId), 0))
                    {
                        candidateHits.Add(new ScopedSearchHit(listing.Id, hit.ListingId, hit.Score));
                    }
                }
            });

        searchSw.Stop();
        _logger.LogInformation("Search phase complete: {Hits} candidate hits in {Elapsed}s",
            candidateHits.Count, searchSw.Elapsed.TotalSeconds.ToString("F1"));

        if (candidateHits.IsEmpty)
        {
            return new ComparablesEtlResult(targetListings.Count, targetListings.Count, 0, 0, 0, 0);
        }

        // Phase 2b: Resolve candidate listing IDs to entities via a single batch query.
        // This replaces the global allListings dictionary — only fetches what vector search returned.
        var candidateListingIds = candidateHits.Select(h => h.MatchedListingId).Distinct().ToList();
        Dictionary<string, Listing> matchedListings;

        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync(ct))
        {
            matchedListings = await dbContext.Listings
                .AsNoTracking()
                .Where(l => candidateListingIds.Contains(l.ListingId))
                .ToDictionaryAsync(l => l.ListingId, ct);
        }

        _logger.LogInformation("Resolved {Count} candidate listings from DB", matchedListings.Count);

        // Build the target listings lookup for classification
        var targetListingsById = targetListings.ToDictionary(l => l.Id);

        // Filter to sold matches and deduplicate canonical pairs
        var candidatePairs = new List<CandidatePair>();
        var seenCanonical = new HashSet<(int, int)>();

        foreach (var hit in candidateHits)
        {
            if (!matchedListings.TryGetValue(hit.MatchedListingId, out var matched))
            {
                continue;
            }

            if (matched.ListingStatus != "Sold")
            {
                continue;
            }

            var key = GetCanonicalKey(hit.ActiveListingId, matched.Id);

            if (existingVerdicts.Contains(key))
            {
                cacheHits++;
                continue;
            }

            if (seenCanonical.Add(key))
            {
                candidatePairs.Add(new CandidatePair(hit.ActiveListingId, matched.Id, hit.Score, key));
            }
        }

        _logger.LogInformation("Filtered to {Pairs} candidate pairs, {CacheHits} cache hits",
            candidatePairs.Count, cacheHits);

        // Phase 3: Classify and save — merge target + matched listings (matched may overlap)
        var allListingsById = new Dictionary<int, Listing>(targetListingsById);
        foreach (var matched in matchedListings.Values)
        {
            allListingsById.TryAdd(matched.Id, matched);
        }

        var result = await ClassifyAndSave(candidatePairs, allListingsById, ct);

        sw.Stop();
        _logger.LogInformation(
            "ETL complete: {Processed} listings, {Pairs} classified, {Comparables} comparables in {Elapsed}",
            targetListings.Count, result.Classified, result.ComparablesFound, sw.Elapsed.ToString(@"hh\:mm\:ss"));

        return new ComparablesEtlResult(
            ListingsProcessed: targetListings.Count,
            VectorQueries: targetListings.Count,
            CandidatePairsFound: candidatePairs.Count,
            CacheHits: cacheHits,
            PairsClassified: result.Classified,
            ComparablesFound: result.ComparablesFound);
    }

    /// <summary>
    /// Classify candidate pairs and persist results in batches.
    /// </summary>
    private async Task<ClassifyResult> ClassifyAndSave(
        List<CandidatePair> pairsList,
        Dictionary<int, Listing> listingsById,
        CancellationToken ct)
    {
        var totalClassified = 0;
        var comparablesFound = 0;
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
                var a = listingsById[p.CanonicalKey.Item1];
                var b = listingsById[p.CanonicalKey.Item2];
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
                    ClassifierConfidence = result.Confidence,
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

        return new ClassifyResult(totalClassified, comparablesFound);
    }

    /// <summary>
    /// Loads only verdicts where at least one side is in the provided listing IDs.
    /// </summary>
    private static async Task<HashSet<(int, int)>> LoadExistingVerdictsForListings(
        EtlDbContext dbContext, HashSet<int> listingIds, CancellationToken ct)
    {
        var verdicts = await dbContext.ListingRelationships
            .AsNoTracking()
            .Where(r => listingIds.Contains(r.ListingIdA) || listingIds.Contains(r.ListingIdB))
            .Select(r => new { r.ListingIdA, r.ListingIdB })
            .ToListAsync(ct);

        return verdicts.Select(v => (v.ListingIdA, v.ListingIdB)).ToHashSet();
    }

    private static (int, int) GetCanonicalKey(int idA, int idB)
    {
        return idA < idB ? (idA, idB) : (idB, idA);
    }

    private record CandidatePair(int ListingIdA, int ListingIdB, float Score, (int, int) CanonicalKey);
    private record ScopedSearchHit(int ActiveListingId, string MatchedListingId, float Score);
    private record ClassifyResult(int Classified, int ComparablesFound);
}
