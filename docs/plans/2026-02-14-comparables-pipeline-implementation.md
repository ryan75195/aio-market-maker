# Comparables Pipeline Optimization — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Optimize `ComparablesEtlService` from ~18 hours to ~40 minutes by batching ONNX inference, filtering to active-only queries, and pipelining Pinecone + GPU work.

**Architecture:** Replace sequential bulk stages with a Channel-based producer-consumer pipeline. Pinecone producers query for active listings' sold neighbors. A single consumer collects pairs into batches of 128, classifies them via batched ONNX inference (`[N, 256]` tensors), saves verdicts, and computes predictions incrementally. The service depends directly on `IVariantClassifierClient` instead of `IListingComparisonService` to enable batched classify calls.

**Tech Stack:** .NET 8.0, ONNX Runtime, `System.Threading.Channels`, EF Core, Pinecone

**Design doc:** `docs/plans/2026-02-14-comparables-pipeline-optimization.md`

---

### Task 1: Batched ONNX Inference

Change `OnnxVariantClassifier.Classify()` from looping single-pair inference (`[1, 256]`) to true batched inference (`[N, 256]`). Add configurable `BatchSize`.

**Files:**
- Modify: `AIOMarketMaker.Core/Services/OnnxVariantClassifier.cs`
- Modify: `AIOMarketMaker.Tests/Unit/Services/OnnxVariantClassifier_UnitTests.cs`
- Modify: `AIOMarketMaker.Tests/Integration/OnnxVariantClassifier_IntegrationTests.cs`

**Step 1: Add BatchSize to OnnxClassifierConfig**

In `OnnxVariantClassifier.cs`, change the config record:

```csharp
public record OnnxClassifierConfig(
    string ModelPath,
    string VocabPath,
    string MergesPath,
    int MaxLength = 256,
    int BatchSize = 128);
```

**Step 2: Write failing unit test for batch softmax**

In `OnnxVariantClassifier_UnitTests.cs`, add:

```csharp
[Test]
public void Should_apply_batch_softmax_correctly()
{
    // Two pairs of logits
    var batchLogits = new float[,]
    {
        { -2.284080f, 3.348258f },
        { 1.5f, -1.5f }
    };

    var results = OnnxVariantClassifier.BatchSoftmax(batchLogits);

    Assert.Multiple(() =>
    {
        Assert.That(results, Has.Length.EqualTo(2));
        // Pair 1: same as single softmax test
        Assert.That(results[0][0], Is.EqualTo(0.003567f).Within(0.0001f));
        Assert.That(results[0][1], Is.EqualTo(0.996433f).Within(0.0001f));
        // Pair 2
        Assert.That(results[1][0], Is.EqualTo(0.9526f).Within(0.001f));
        Assert.That(results[1][1], Is.EqualTo(0.0474f).Within(0.001f));
    });
}
```

Run: `dotnet test --filter "Should_apply_batch_softmax_correctly"`
Expected: FAIL — `BatchSoftmax` method doesn't exist

**Step 3: Implement BatchSoftmax**

In `OnnxVariantClassifier.cs`, add:

```csharp
public static float[][] BatchSoftmax(float[,] batchLogits)
{
    var batchSize = batchLogits.GetLength(0);
    var numClasses = batchLogits.GetLength(1);
    var results = new float[batchSize][];

    for (var i = 0; i < batchSize; i++)
    {
        var logits = new float[numClasses];
        for (var j = 0; j < numClasses; j++)
        {
            logits[j] = batchLogits[i, j];
        }
        results[i] = Softmax(logits);
    }

    return results;
}
```

Run: `dotnet test --filter "Should_apply_batch_softmax_correctly"`
Expected: PASS

**Step 4: Rewrite Classify() for batched tensor inference**

Replace the existing `Classify()` method and `RunInference()` in `OnnxVariantClassifier.cs`:

```csharp
public Task<IReadOnlyList<PairResult>> Classify(
    IEnumerable<ClassifyPairRequest> pairs,
    CancellationToken ct = default)
{
    var pairList = pairs.ToList();
    if (pairList.Count == 0)
    {
        return Task.FromResult<IReadOnlyList<PairResult>>(Array.Empty<PairResult>());
    }

    ct.ThrowIfCancellationRequested();

    // Tokenize all pairs
    var allInputIds = new long[pairList.Count * _maxLength];
    var allAttentionMask = new long[pairList.Count * _maxLength];

    for (var i = 0; i < pairList.Count; i++)
    {
        var pair = pairList[i];
        var textA = $"{pair.TitleA} | {pair.DescriptionA}";
        var textB = $"{pair.TitleB} | {pair.DescriptionB}";
        var (inputIds, attentionMask) = TokenizePairInternal(textA, textB);

        Array.Copy(inputIds, 0, allInputIds, i * _maxLength, _maxLength);
        Array.Copy(attentionMask, 0, allAttentionMask, i * _maxLength, _maxLength);
    }

    // Build batched tensors [N, maxLength]
    var inputTensor = new DenseTensor<long>(allInputIds, [pairList.Count, _maxLength]);
    var maskTensor = new DenseTensor<long>(allAttentionMask, [pairList.Count, _maxLength]);
    var inputs = new List<NamedOnnxValue>
    {
        NamedOnnxValue.CreateFromTensor("input_ids", inputTensor),
        NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor)
    };

    // Single inference call for entire batch
    using var output = _session.Run(inputs);
    var logitsTensor = output.First().AsTensor<float>();

    // Parse results
    var results = new List<PairResult>(pairList.Count);
    for (var i = 0; i < pairList.Count; i++)
    {
        var logits = new float[] { logitsTensor[i, 0], logitsTensor[i, 1] };
        var probs = Softmax(logits);
        var isComparable = probs[1] > probs[0];
        var confidence = probs.Max();
        results.Add(new PairResult(isComparable, confidence));
    }

    return Task.FromResult<IReadOnlyList<PairResult>>(results);
}
```

Remove the old `RunInference(long[] inputIds, long[] attentionMask)` private method — it's no longer used.

**Step 5: Run all existing tests**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit" --verbosity quiet`
Expected: All pass (the interface hasn't changed, only the internal batching)

**Step 6: Write integration test for batch classification**

The existing `Should_classify_multiple_pairs_in_single_call` test already covers this — it sends 2 pairs and checks both results. Run it to verify batched inference produces correct results:

Run: `dotnet test --filter "Should_classify_multiple_pairs_in_single_call" -- NUnit.DefaultTestNamePattern="{m}"`
Expected: PASS (requires model files on E: drive)

**Step 7: Commit**

```bash
git add AIOMarketMaker.Core/Services/OnnxVariantClassifier.cs AIOMarketMaker.Tests/Unit/Services/OnnxVariantClassifier_UnitTests.cs
git commit -m "feat: batch ONNX inference with [N, 256] tensors"
```

---

### Task 2: Active-Only Queries with Sold-Neighbor Filtering

Refactor `ComparablesEtlService` to query Pinecone only for active listings and discard non-sold neighbors. Switch from `IListingComparisonService` to `IVariantClassifierClient` for direct batched classification.

**Files:**
- Modify: `AIOMarketMaker.Core/Services/ComparablesEtlService.cs`
- Modify: `AIOMarketMaker.Tests/Unit/Services/ComparablesEtlService_UnitTests.cs`
- Modify: `AIOMarketMaker.Api/Program.cs` (DI — remove `IListingComparisonService` dependency from `ComparablesEtlService`)
- Modify: `AIOMarketMaker.Etl/Program.cs` (DI)

**Step 1: Update the ComparablesEtlService constructor and dependencies**

Replace `IListingComparisonService` with `IVariantClassifierClient`:

```csharp
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
```

**Step 2: Update Run() to load active-only + sold IDs**

Replace the beginning of `Run()`:

```csharp
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
```

**Step 3: Remove FilterToIndexedListings()**

Delete the `FilterToIndexedListings()` method entirely. Pinecone queries will naturally return empty results for non-indexed listings (the `FindSimilar` call will just return no hits).

**Step 4: Update QueryPineconeForCandidates() to filter to sold neighbors**

```csharp
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
```

**Step 5: Replace EvaluatePairsWithLlm() with batched classifier**

Replace the old method with direct `IVariantClassifierClient` batched calls:

```csharp
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
```

**Step 6: Wire the updated Run() method together**

Update the middle of `Run()` to call the new methods:

```csharp
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
```

**Step 7: Update DI in Program.cs (API)**

In `AIOMarketMaker.Api/Program.cs`, change `ComparablesEtlService` registration. It no longer needs `IListingComparisonService` — it takes `IVariantClassifierClient` directly:

```csharp
// ComparablesEtlService (uses classifier directly for batched inference)
builder.Services.AddScoped<IComparablesEtlService, ComparablesEtlService>();
```

No explicit factory needed — `IVariantClassifierClient` is already registered as a singleton.

**Step 8: Update DI in Program.cs (ETL)**

In `AIOMarketMaker.Etl/Program.cs`, same change — `ComparablesEtlService` resolves `IVariantClassifierClient` from DI automatically.

**Step 9: Update unit tests**

In `ComparablesEtlService_UnitTests.cs`, update `SetUp()` and the mock:

```csharp
private Mock<IVariantClassifierClient> _classifierMock = null!;

[SetUp]
public void Setup()
{
    _dbContext = InMemoryDbContextFactory.Create();
    _searchMock = new Mock<ISemanticSearchService>();
    _classifierMock = new Mock<IVariantClassifierClient>();
    _loggerMock = new Mock<ILogger<ComparablesEtlService>>();

    _searchMock.Setup(s => s.FindSimilar(
            It.IsAny<string>(),
            It.IsAny<IEnumerable<string>?>(),
            It.IsAny<Pinecone.Metadata?>(),
            It.IsAny<int?>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(new SemanticSearchResult(new List<SemanticSearchHit>()));

    _service = new ComparablesEtlService(
        _searchMock.Object,
        _classifierMock.Object,
        _dbContext,
        _loggerMock.Object);
}
```

Update `Should_call_llm_for_uncached_pair_and_store_verdict` — mock the classifier instead of `IListingComparisonService`:

```csharp
_classifierMock.Setup(c => c.Classify(
        It.IsAny<IEnumerable<ClassifyPairRequest>>(),
        It.IsAny<CancellationToken>()))
    .ReturnsAsync(new[] { new PairResult(true, 0.95f) });
```

And update the assertion to check the new explanation format:

```csharp
Assert.That(verdict!.Explanation, Does.Contain("Model: confidence="));
```

Update `Should_skip_llm_call_when_verdict_already_cached` — verify classifier was never called:

```csharp
_classifierMock.Verify(
    c => c.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()),
    Times.Never);
```

Update `Should_use_canonical_ordering_for_verdict_storage` — mock classifier:

```csharp
_classifierMock.Setup(c => c.Classify(
        It.IsAny<IEnumerable<ClassifyPairRequest>>(),
        It.IsAny<CancellationToken>()))
    .ReturnsAsync(new[] { new PairResult(true, 0.92f) });
```

Remove `_comparisonMock` field entirely — it's no longer used.

Remove `Should_skip_listings_not_indexed_in_pinecone` test — the existence check is gone. (Pinecone returns empty results for non-indexed listings naturally.)

Update `Should_report_counts_without_making_llm_calls_in_dry_run`:

```csharp
_classifierMock.Verify(
    c => c.Classify(It.IsAny<IEnumerable<ClassifyPairRequest>>(), It.IsAny<CancellationToken>()),
    Times.Never);
```

**Step 10: Write new test for sold-only neighbor filtering**

```csharp
[Test]
public async Task Should_skip_active_neighbors_and_only_classify_sold()
{
    var active1 = SeedListing(1, "iPhone 15 Pro", "Active", 800m);
    var active2 = SeedListing(2, "iPhone 15 Pro Black", "Active", 810m);
    var sold1 = SeedListing(3, "iPhone 15 Pro 256GB", "Sold", 850m);

    // Pinecone returns both active2 and sold1 as neighbors of active1
    MockPineconeResult("1", ("2", 0.91), ("3", 0.89));

    _classifierMock.Setup(c => c.Classify(
            It.IsAny<IEnumerable<ClassifyPairRequest>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(new[] { new PairResult(true, 0.95f) });

    var result = await _service.Run(dryRun: false);

    // Should only classify 1 pair (active1 ↔ sold1), not active1 ↔ active2
    Assert.That(result.LlmCallsMade, Is.EqualTo(1));
    var verdict = _dbContext.ListingRelationships.Single();
    Assert.Multiple(() =>
    {
        Assert.That(verdict.ListingIdA, Is.EqualTo(1));
        Assert.That(verdict.ListingIdB, Is.EqualTo(3));
    });
}
```

**Step 11: Run all tests**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit" --verbosity quiet`
Expected: All pass

**Step 12: Commit**

```bash
git add AIOMarketMaker.Core/Services/ComparablesEtlService.cs AIOMarketMaker.Tests/Unit/Services/ComparablesEtlService_UnitTests.cs AIOMarketMaker.Api/Program.cs AIOMarketMaker.Etl/Program.cs
git commit -m "feat: active-only Pinecone queries with sold-neighbor filtering and batched classify"
```

---

### Task 3: Pipelined Producer-Consumer Architecture

Replace the sequential "query all → classify all → save all" with a Channel-based pipeline where Pinecone queries and GPU inference overlap. Save verdicts and compute predictions incrementally.

**Files:**
- Modify: `AIOMarketMaker.Core/Services/ComparablesEtlService.cs`
- Modify: `AIOMarketMaker.Tests/Unit/Services/ComparablesEtlService_UnitTests.cs`

**Step 1: Add Channel-based pipeline to Run()**

Replace the middle of `Run()` (after loading data, before return) with a producer-consumer pipeline:

```csharp
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

    // Consumer: collect batches, classify, save verdicts + predictions
    var totalClassified = 0;
    var comparablesFound = 0;
    var predictionsWritten = 0;
    var batch = new List<CandidatePair>(128);

    await foreach (var pair in channel.Reader.ReadAllAsync(ct))
    {
        batch.Add(pair);

        if (batch.Count >= 128)
        {
            var (classified, comps, preds) = await ProcessBatch(batch, allListingsById, soldIdSet, ct);
            totalClassified += classified;
            comparablesFound += comps;
            predictionsWritten += preds;
            batch.Clear();
        }
    }

    // Flush remaining
    if (batch.Count > 0)
    {
        var (classified, comps, preds) = await ProcessBatch(batch, allListingsById, soldIdSet, ct);
        totalClassified += classified;
        comparablesFound += comps;
        predictionsWritten += preds;
    }

    await producerTask;

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
```

**Step 2: Add ProcessBatch() helper**

Add a helper method that classifies a batch, saves verdicts, and computes predictions for the active listings in the batch:

```csharp
private async Task<(int Classified, int Comparables, int Predictions)> ProcessBatch(
    List<CandidatePair> batch,
    Dictionary<int, Listing> listingsById,
    HashSet<int> soldIdSet,
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
    var activeIdsInBatch = new HashSet<int>();
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

        // Track which active listings were in this batch
        if (!soldIdSet.Contains(pair.CanonicalKey.Item1))
        {
            activeIdsInBatch.Add(pair.CanonicalKey.Item1);
        }
        if (!soldIdSet.Contains(pair.CanonicalKey.Item2))
        {
            activeIdsInBatch.Add(pair.CanonicalKey.Item2);
        }
    }

    await _dbContext.SaveChangesAsync(ct);

    // Compute predictions for active listings in this batch
    var predictions = await ComputePredictionsForListings(activeIdsInBatch, listingsById, soldIdSet, ct);

    _logger.LogInformation("Batch: {Count} pairs, {Comps} comparable, {Preds} predictions",
        batch.Count, comparables, predictions);

    return (batch.Count, comparables, predictions);
}
```

**Step 3: Add incremental ComputePredictionsForListings()**

Extract per-listing prediction logic from the old bulk method:

```csharp
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
```

**Step 4: Remove old methods**

Delete these methods which are now replaced:
- `FilterToIndexedListings()` — existence check removed
- `QueryPineconeForCandidates()` — inlined into producer
- `EvaluatePairsWithLlm()` — replaced by ProcessBatch
- `StoreVerdicts()` — inlined into ProcessBatch
- `ComputeAndStorePredictions()` — replaced by ComputePredictionsForListings

**Step 5: Add `using System.Threading.Channels;` to the top of the file**

**Step 6: Run all tests**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit" --verbosity quiet`
Expected: All pass

**Step 7: Commit**

```bash
git add AIOMarketMaker.Core/Services/ComparablesEtlService.cs AIOMarketMaker.Tests/Unit/Services/ComparablesEtlService_UnitTests.cs
git commit -m "feat: pipelined producer-consumer with incremental predictions"
```

---

### Task 4: Smoke Test

Run the ETL `--comparables --dry-run` to verify the pipeline starts correctly and reports accurate counts.

**Step 1: Run dry run**

```bash
cd AIOMarketMaker/AIOMarketMaker.Etl
dotnet run -- --comparables --dry-run
```

Expected output should show:
- Active listings loaded (~99.5K)
- Pinecone queries made (99.5K)
- Candidate pairs found (some number, after sold-only filtering)
- Cache hits (0 on first run)
- LLM calls required = candidate pairs found
- LLM calls made = 0 (dry run)

**Step 2: Run real (small scope) — optional**

If dry run looks good, test with a small real run. You could temporarily add a `.Take(100)` to the active listings query to process only 100 listings:

```csharp
var activeListings = await _dbContext.Listings
    .AsNoTracking()
    .Where(l => l.ListingStatus == "Active")
    .Take(100)  // TEMPORARY for smoke test
    .ToListAsync(ct);
```

Run: `dotnet run -- --comparables`

Verify:
- Verdicts appear in `ListingRelationships`
- Predictions appear in `ListingPredictions`
- No errors in output

Remove the `.Take(100)` after testing.

**Step 3: Commit any fixes**

```bash
git add -A
git commit -m "fix: smoke test fixes for comparables pipeline"
```
