# Local Vector Index Migration — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace Pinecone cloud vector database with a local USearch HNSW index, eliminating $50-144/month costs and achieving 30-100x faster queries.

**Architecture:** New `IVectorIndex` abstraction wrapping USearch native HNSW engine. `SemanticSearchService` and `ListingIndexingService` simplified to use pure vector operations (no metadata). String↔ulong ID mapping persisted alongside the index file.

**Tech Stack:** Cloud.Unum.USearch 2.23.0 (NuGet), .NET 8.0, System.Text.Json for ID map serialization

**Design doc:** `docs/plans/2026-02-16-local-vector-index-design.md`

---

## Behavioral Parity Checklist

### Behaviors to Preserve
- [x] `ISemanticSearchService` interface stays unchanged for consumers
- [x] `ComparablesEtlService` works without modification (uses `ISemanticSearchService`)
- [x] `FindSimilar` returns neighbors by vector similarity with self-exclusion
- [x] `FindSimilar` applies similarity threshold filtering
- [x] `FindSimilar` requests TopK+1 then excludes self-match
- [x] `Search` by text query: embed text → query index → filter by threshold
- [x] `IndexListings` batch: embed text → upsert vectors, skip empty content
- [x] `IndexListings` batch: capture errors per batch, continue processing
- [x] `ListingIndexingService.Index` with `embedContent: true`: embed + upsert
- [x] `ListingIndexingService.Index`: skip when no title/description
- [x] `Delete`: remove vectors by listing ID
- [x] `Exists`: check if listing ID has a vector in the index
- [x] ETL `--validate`: query neighbors by listing ID, exclude self, classify pairs
- [x] ETL `--k-analysis`: query K neighbors, classify by rank

### Behaviors Intentionally Changed
| Old Behavior | New Behavior | Impact |
|---|---|---|
| `FindSimilar` `Metadata? metadataFilter` param | Removed from interface | No production caller uses it (`ComparablesEtlService` passes `null`) |
| `FindSimilar` `filterToListingIds` param | Removed from interface | No production caller uses it (`ComparablesEtlService` passes `null`) |
| `Search` `filterToListingIds` param | Removed from interface | No production caller uses this parameter |
| `ListingIndexingService.Index(embedContent: false)` metadata-only update | Returns `Skipped` (no-op) | `ScrapeJobProcessor:278` calls this for re-scraped active listings. With no metadata in the index, there's nothing to update. The vector is already indexed from the initial scrape. |
| `IndexingAction.MetadataUpdated` enum value | Removed | Replaced by `Skipped` for the `embedContent: false` path |
| `PineconeConfig.UpsertBatchSize` | `VectorIndexConfig.UpsertBatchSize` (same default 100) | No functional change |
| Pinecone cloud storage | Local disk file (`vectors.usearch` + `vectors-idmap.json`) | ~3.6 GB RAM, 2-5s cold start |

---

### Task 1: Create IVectorIndex Interface and VectorIndexConfig

**Files:**
- Create: `AIOMarketMaker.Core/Services/IVectorIndex.cs`
- Modify: `AIOMarketMaker.Core/Services/SemanticSearchModels.cs` (add `VectorIndexConfig`)

**Step 1: Create the IVectorIndex interface**

Create `AIOMarketMaker.Core/Services/IVectorIndex.cs`:

```csharp
namespace AIOMarketMaker.Core.Services;

public record VectorSearchHit(string Id, float Score);

public interface IVectorIndex
{
    void Upsert(string id, float[] vector);
    void UpsertBatch(IEnumerable<(string Id, float[] Vector)> items);
    IEnumerable<VectorSearchHit> Search(float[] queryVector, int topK);
    IEnumerable<VectorSearchHit> SearchById(string id, int topK);
    void Remove(IEnumerable<string> ids);
    bool Contains(string id);
    int Count { get; }
    void Save();
    void Load();
}
```

Note: `Save()` and `Load()` are parameterless — paths come from `VectorIndexConfig` injected at construction time.

**Step 2: Add VectorIndexConfig to SemanticSearchModels.cs**

Add this record to `AIOMarketMaker.Core/Services/SemanticSearchModels.cs` (keep `PineconeConfig` for now — it's removed in Task 8):

```csharp
public record VectorIndexConfig(
    string IndexPath,
    string IdMapPath,
    int TopK = 30,
    float SimilarityThreshold = 0.80f,
    int Dimensions = 3072,
    int Connectivity = 16,
    int ExpansionAdd = 128,
    int ExpansionSearch = 64,
    int UpsertBatchSize = 100
);
```

**Step 3: Build to verify compilation**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Services/IVectorIndex.cs AIOMarketMaker/AIOMarketMaker.Core/Services/SemanticSearchModels.cs
git commit -m "feat: add IVectorIndex interface and VectorIndexConfig record"
```

---

### Task 2: Implement USearchVectorIndex

**Files:**
- Modify: `AIOMarketMaker.Core/AIOMarketMaker.Core.csproj` (add USearch NuGet)
- Create: `AIOMarketMaker.Core/Services/USearchVectorIndex.cs`
- Create: `AIOMarketMaker.Tests/Unit/Services/USearchVectorIndex_UnitTests.cs`

**Step 1: Add USearch NuGet package**

```bash
dotnet add AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj package Cloud.Unum.USearch --version 2.23.0
```

**Step 2: Write failing tests for USearchVectorIndex**

Create `AIOMarketMaker.Tests/Unit/Services/USearchVectorIndex_UnitTests.cs`:

```csharp
using AIOMarketMaker.Core.Services;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class USearchVectorIndex_UnitTests
{
    private VectorIndexConfig _config = null!;
    private USearchVectorIndex _index = null!;
    private string _tempDir = null!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"usearch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _config = new VectorIndexConfig(
            IndexPath: Path.Combine(_tempDir, "test.usearch"),
            IdMapPath: Path.Combine(_tempDir, "test-idmap.json"),
            Dimensions: 4,
            Connectivity: 16,
            ExpansionAdd: 128,
            ExpansionSearch: 64);
        _index = new USearchVectorIndex(_config);
    }

    [TearDown]
    public void TearDown()
    {
        _index?.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Test]
    public void Should_start_empty()
    {
        Assert.That(_index.Count, Is.EqualTo(0));
    }

    [Test]
    public void Should_upsert_and_contain()
    {
        _index.Upsert("listing-1", new float[] { 1, 0, 0, 0 });

        Assert.Multiple(() =>
        {
            Assert.That(_index.Count, Is.EqualTo(1));
            Assert.That(_index.Contains("listing-1"), Is.True);
            Assert.That(_index.Contains("listing-2"), Is.False);
        });
    }

    [Test]
    public void Should_upsert_batch()
    {
        var items = new[]
        {
            ("a", new float[] { 1, 0, 0, 0 }),
            ("b", new float[] { 0, 1, 0, 0 }),
            ("c", new float[] { 0, 0, 1, 0 })
        };

        _index.UpsertBatch(items);

        Assert.That(_index.Count, Is.EqualTo(3));
    }

    [Test]
    public void Should_search_by_vector_and_return_most_similar()
    {
        _index.Upsert("a", new float[] { 1, 0, 0, 0 });
        _index.Upsert("b", new float[] { 0.9f, 0.1f, 0, 0 });
        _index.Upsert("c", new float[] { 0, 0, 0, 1 });

        var hits = _index.Search(new float[] { 1, 0, 0, 0 }, topK: 2).ToList();

        Assert.That(hits, Has.Count.EqualTo(2));
        Assert.That(hits[0].Id, Is.EqualTo("a"));
        Assert.That(hits[1].Id, Is.EqualTo("b"));
    }

    [Test]
    public void Should_search_by_id()
    {
        _index.Upsert("a", new float[] { 1, 0, 0, 0 });
        _index.Upsert("b", new float[] { 0.9f, 0.1f, 0, 0 });
        _index.Upsert("c", new float[] { 0, 0, 0, 1 });

        var hits = _index.SearchById("a", topK: 3).ToList();

        // Should return all 3 (including self)
        Assert.That(hits, Has.Count.EqualTo(3));
        Assert.That(hits[0].Id, Is.EqualTo("a"));
    }

    [Test]
    public void Should_remove_vectors()
    {
        _index.Upsert("a", new float[] { 1, 0, 0, 0 });
        _index.Upsert("b", new float[] { 0, 1, 0, 0 });

        _index.Remove(new[] { "a" });

        Assert.Multiple(() =>
        {
            Assert.That(_index.Contains("a"), Is.False);
            Assert.That(_index.Contains("b"), Is.True);
            Assert.That(_index.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void Should_handle_remove_nonexistent_id()
    {
        _index.Upsert("a", new float[] { 1, 0, 0, 0 });

        // Should not throw
        Assert.DoesNotThrow(() => _index.Remove(new[] { "nonexistent" }));
        Assert.That(_index.Count, Is.EqualTo(1));
    }

    [Test]
    public void Should_overwrite_on_duplicate_upsert()
    {
        _index.Upsert("a", new float[] { 1, 0, 0, 0 });
        _index.Upsert("a", new float[] { 0, 1, 0, 0 });

        Assert.That(_index.Count, Is.EqualTo(1));

        var hits = _index.Search(new float[] { 0, 1, 0, 0 }, topK: 1).ToList();
        Assert.That(hits[0].Id, Is.EqualTo("a"));
    }

    [Test]
    public void Should_save_and_load()
    {
        _index.Upsert("a", new float[] { 1, 0, 0, 0 });
        _index.Upsert("b", new float[] { 0, 1, 0, 0 });
        _index.Save();

        // Create new index and load
        using var loaded = new USearchVectorIndex(_config);
        loaded.Load();

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Count, Is.EqualTo(2));
            Assert.That(loaded.Contains("a"), Is.True);
            Assert.That(loaded.Contains("b"), Is.True);
        });

        // Verify search still works after load
        var hits = loaded.Search(new float[] { 1, 0, 0, 0 }, topK: 1).ToList();
        Assert.That(hits[0].Id, Is.EqualTo("a"));
    }

    [Test]
    public void Should_return_cosine_similarity_scores()
    {
        // Identical vector should have score ~1.0
        _index.Upsert("same", new float[] { 1, 0, 0, 0 });

        var hits = _index.Search(new float[] { 1, 0, 0, 0 }, topK: 1).ToList();

        Assert.That(hits[0].Score, Is.GreaterThan(0.99f));
    }

    [Test]
    public void Should_return_empty_when_searching_empty_index()
    {
        var hits = _index.Search(new float[] { 1, 0, 0, 0 }, topK: 5).ToList();
        Assert.That(hits, Is.Empty);
    }

    [Test]
    public void Should_clamp_topk_to_index_count()
    {
        _index.Upsert("a", new float[] { 1, 0, 0, 0 });
        _index.Upsert("b", new float[] { 0, 1, 0, 0 });

        var hits = _index.Search(new float[] { 1, 0, 0, 0 }, topK: 100).ToList();

        Assert.That(hits, Has.Count.EqualTo(2));
    }
}
```

**Step 3: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~USearchVectorIndex"`
Expected: Build error — `USearchVectorIndex` does not exist yet.

**Step 4: Implement USearchVectorIndex**

Create `AIOMarketMaker.Core/Services/USearchVectorIndex.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text.Json;
using Cloud.Unum.USearch;

namespace AIOMarketMaker.Core.Services;

public class USearchVectorIndex : IVectorIndex, IDisposable
{
    private readonly VectorIndexConfig _config;
    private USearchIndex _index;
    private readonly ConcurrentDictionary<string, ulong> _idToKey = new();
    private readonly ConcurrentDictionary<ulong, string> _keyToId = new();
    private ulong _nextKey;
    private readonly object _lock = new();

    public USearchVectorIndex(VectorIndexConfig config)
    {
        _config = config;
        _index = new USearchIndex(
            metricKind: MetricKind.Cos,
            quantization: ScalarKind.Float32,
            dimensions: (ulong)config.Dimensions,
            connectivity: (ulong)config.Connectivity,
            expansionAdd: (ulong)config.ExpansionAdd,
            expansionSearch: (ulong)config.ExpansionSearch);
    }

    public int Count => (int)_index.Size();

    public void Upsert(string id, float[] vector)
    {
        lock (_lock)
        {
            if (_idToKey.TryGetValue(id, out var existingKey))
            {
                _index.Remove(existingKey);
                _index.Add(existingKey, vector);
            }
            else
            {
                var key = _nextKey++;
                _idToKey[id] = key;
                _keyToId[key] = id;
                _index.Add(key, vector);
            }
        }
    }

    public void UpsertBatch(IEnumerable<(string Id, float[] Vector)> items)
    {
        foreach (var (id, vector) in items)
        {
            Upsert(id, vector);
        }
    }

    public IEnumerable<VectorSearchHit> Search(float[] queryVector, int topK)
    {
        if (_index.Size() == 0)
        {
            return [];
        }

        var actualTopK = (int)Math.Min(topK, _index.Size());
        var matches = _index.Search(queryVector, actualTopK, out ulong[] keys, out float[] distances);

        var hits = new List<VectorSearchHit>(matches);
        for (var i = 0; i < matches; i++)
        {
            if (_keyToId.TryGetValue(keys[i], out var id))
            {
                // USearch returns cosine distance; convert to similarity
                hits.Add(new VectorSearchHit(id, 1f - distances[i]));
            }
        }

        return hits;
    }

    public IEnumerable<VectorSearchHit> SearchById(string id, int topK)
    {
        if (!_idToKey.TryGetValue(id, out var key))
        {
            return [];
        }

        // Retrieve the vector for this key, then search
        var vector = _index.Get(key, (ulong)_config.Dimensions);
        return Search(vector, topK);
    }

    public void Remove(IEnumerable<string> ids)
    {
        lock (_lock)
        {
            foreach (var id in ids)
            {
                if (_idToKey.TryRemove(id, out var key))
                {
                    _index.Remove(key);
                    _keyToId.TryRemove(key, out _);
                }
            }
        }
    }

    public bool Contains(string id) => _idToKey.ContainsKey(id);

    public void Save()
    {
        var indexDir = Path.GetDirectoryName(_config.IndexPath);
        if (!string.IsNullOrEmpty(indexDir))
        {
            Directory.CreateDirectory(indexDir);
        }

        _index.Save(_config.IndexPath);

        var idMap = _idToKey.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var json = JsonSerializer.Serialize(idMap);
        File.WriteAllText(_config.IdMapPath, json);
    }

    public void Load()
    {
        if (!File.Exists(_config.IndexPath) || !File.Exists(_config.IdMapPath))
        {
            return;
        }

        _index.Dispose();
        _index = new USearchIndex(_config.IndexPath, view: false);

        var json = File.ReadAllText(_config.IdMapPath);
        var idMap = JsonSerializer.Deserialize<Dictionary<string, ulong>>(json) ?? new();

        _idToKey.Clear();
        _keyToId.Clear();
        _nextKey = 0;

        foreach (var (id, key) in idMap)
        {
            _idToKey[id] = key;
            _keyToId[key] = id;
            if (key >= _nextKey)
            {
                _nextKey = key + 1;
            }
        }
    }

    public void Dispose()
    {
        _index?.Dispose();
    }
}
```

**Important notes for the implementer:**
- USearch's `Search()` returns cosine **distance** (0 = identical), not similarity. Convert: `similarity = 1 - distance`.
- `USearchIndex.Get(key, dims)` retrieves a stored vector by key — needed for `SearchById`.
- The USearch C# API may differ slightly from the docs. Check the actual API surface of `Cloud.Unum.USearch` v2.23.0. The constructor params, `Search` signature (out params vs return), and `Get` method may need adjustment.
- If USearch doesn't support `Remove()`, use a "tombstone" approach: mark the key as deleted in the ID map and skip it in results.

**Step 5: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~USearchVectorIndex"`
Expected: All tests pass.

**Step 6: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj AIOMarketMaker/AIOMarketMaker.Core/Services/USearchVectorIndex.cs AIOMarketMaker/AIOMarketMaker.Tests/Unit/Services/USearchVectorIndex_UnitTests.cs
git commit -m "feat: implement USearchVectorIndex with string-ulong ID mapping"
```

---

### Task 3: Refactor SemanticSearchService to Use IVectorIndex

**Files:**
- Modify: `AIOMarketMaker.Core/Services/SemanticSearchService.cs` (interface + implementation)
- Modify: `AIOMarketMaker.Tests/UnitTests/SemanticSearchServiceTests.cs`

**Step 1: Update ISemanticSearchService interface**

In `SemanticSearchService.cs`, change the interface. Key changes:
- Remove `Metadata? metadataFilter` from `FindSimilar` (Pinecone type, never used in production)
- Remove `IEnumerable<string>? filterToListingIds` from both `Search` and `FindSimilar` (Pinecone metadata filter, never used in production)

```csharp
public interface ISemanticSearchService
{
    Task<IndexResult> IndexListings(
        IEnumerable<Listing> listings,
        CancellationToken ct = default);

    Task<SemanticSearchResult> Search(
        string queryText,
        int? topK = null,
        CancellationToken ct = default);

    Task<SemanticSearchResult> FindSimilar(
        string listingId,
        int? topK = null,
        CancellationToken ct = default);

    Task Delete(
        IEnumerable<string> listingIds,
        CancellationToken ct = default);

    Task<bool> Exists(
        string listingId,
        CancellationToken ct = default);
}
```

**Step 2: Rewrite SemanticSearchService implementation**

Replace the class body. Key changes:
- Remove `using Pinecone;`
- Replace `IPineconeIndexClient _index` with `IVectorIndex _vectorIndex`
- Replace `PineconeConfig _config` with `VectorIndexConfig _config`
- Remove `BuildIdFilter()`, `MergeFilters()` — Pinecone-specific helpers
- `IndexListings`: use `_vectorIndex.UpsertBatch()` instead of Pinecone `UpsertRequest`
- `Search`: use `_vectorIndex.Search()` → filter by threshold
- `FindSimilar`: use `_vectorIndex.SearchById()` → exclude self → filter by threshold → take topK
- `Delete`: use `_vectorIndex.Remove()`
- `Exists`: use `_vectorIndex.Contains()`

```csharp
public class SemanticSearchService : ISemanticSearchService
{
    private readonly IVectorIndex _vectorIndex;
    private readonly IEmbeddingService _embeddingService;
    private readonly VectorIndexConfig _config;
    private readonly ILogger<SemanticSearchService> _logger;

    public SemanticSearchService(
        VectorIndexConfig config,
        IVectorIndex vectorIndex,
        IEmbeddingService embeddingService,
        ILogger<SemanticSearchService> logger)
    {
        _config = config;
        _vectorIndex = vectorIndex;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    // IndexListings: same batching logic, but upsert to IVectorIndex
    // Search: embed text, _vectorIndex.Search(), filter by threshold
    // FindSimilar: _vectorIndex.SearchById(topK+1), exclude self, filter by threshold, take topK
    // Delete: _vectorIndex.Remove()
    // Exists: _vectorIndex.Contains()
    // Keep BuildEmbeddingText() unchanged
}
```

**Step 3: Update all callers of FindSimilar**

The simplified `FindSimilar` no longer takes `filterToListingIds` or `metadataFilter`. Update:

`ComparablesEtlService.cs:99-104` — change:
```csharp
var searchResult = await _searchService.FindSimilar(
    listing.ListingId,
    filterToListingIds: null,
    metadataFilter: null,
    topK: PineconeTopK,
    ct: ct);
```
to:
```csharp
var searchResult = await _searchService.FindSimilar(
    listing.ListingId,
    topK: PineconeTopK,
    ct: ct);
```

Also update `ComparablesEtlService` field names: `PineconeTopK` → `VectorTopK`, `MaxPineconeConcurrency` → `MaxSearchConcurrency`. And in `ComparablesEtlResult`: `PineconeQueries` → `VectorQueries`.

**Step 4: Rewrite SemanticSearchServiceTests**

Replace `Mock<IPineconeIndexClient>` with `Mock<IVectorIndex>`, replace `PineconeConfig` with `VectorIndexConfig`. Remove all Pinecone SDK types (`UpsertRequest`, `QueryRequest`, `QueryResponse`, `ScoredVector`, `FetchRequest`, `FetchResponse`, `DeleteRequest`, `Metadata`, `Vector`).

Tests to keep (with modified assertions):
- `Should_return_zero_counts_when_indexing_empty_list`
- `Should_skip_listing_with_no_content` (6 cases)
- `Should_index_listing_with_valid_content` (5 cases) — verify `UpsertBatch` called
- `Should_throw_when_searching_with_invalid_query` (5 cases)
- `Should_filter_by_similarity_threshold` (6 cases)
- `Should_exclude_self_when_finding_similar`
- `Should_not_call_delete_when_list_is_empty`
- `Should_call_delete_with_correct_ids` → verify `Remove()` called
- `Should_return_true_when_listing_exists` → verify `Contains()` called
- `Should_return_false_when_listing_does_not_exist`
- `Should_use_config_topk_when_not_specified`
- `Should_override_topk_when_specified`
- `Should_batch_upserts_according_to_config`
- `Should_continue_processing_and_capture_errors_when_batch_fails`
- `Should_handle_mixed_valid_and_invalid_listings_in_batch`
- `Should_request_topk_plus_one_when_finding_similar`
- `Should_return_empty_hits_when_matches_is_null` → `Should_return_empty_hits_when_index_empty`

Tests to remove:
- `Should_pass_filter_to_pinecone_when_filtering_by_listing_ids` — parameter removed
- `Should_not_pass_filter_when_no_listing_ids_specified` — parameter removed
- `Should_pass_metadata_filter_to_pinecone_query` — parameter removed

**Step 5: Update ComparablesEtlService tests**

`AIOMarketMaker.Tests/Unit/Services/ComparablesEtlService_UnitTests.cs` — update `FindSimilar` mock setups to match the new simplified signature (no `filterToListingIds`, no `metadataFilter`).

**Step 6: Run all tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit"`
Expected: All unit tests pass.

**Step 7: Commit**

```bash
git add -A
git commit -m "refactor: rewrite SemanticSearchService to use IVectorIndex, drop Pinecone types"
```

---

### Task 4: Simplify ListingIndexingService

**Files:**
- Modify: `AIOMarketMaker.Core/Services/ListingIndexingService.cs`
- Modify: `AIOMarketMaker.Tests/Unit/Services/ListingIndexingService_UnitTests.cs`

**Step 1: Rewrite ListingIndexingService**

Key changes:
- Remove `using Pinecone;`
- Replace `IPineconeIndexClient _pinecone` with `IVectorIndex _vectorIndex`
- Remove `BuildMetadata()` entirely (no metadata in local index)
- When `embedContent: true`: embed text → `_vectorIndex.Upsert(id, embedding)` → return `Embedded`
- When `embedContent: false`: nothing to do (vector already indexed, no metadata to update) → return `Skipped`
- Remove `IndexingAction.MetadataUpdated` enum value

```csharp
public enum IndexingAction
{
    Embedded,
    Skipped
}

public class ListingIndexingService : IListingIndexingService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorIndex _vectorIndex;
    private readonly ILogger<ListingIndexingService> _logger;

    public ListingIndexingService(
        IEmbeddingService embeddingService,
        IVectorIndex vectorIndex,
        ILogger<ListingIndexingService> logger)
    {
        _embeddingService = embeddingService;
        _vectorIndex = vectorIndex;
        _logger = logger;
    }

    public async Task<IndexingResult> Index(Listing listing, bool embedContent, CancellationToken ct = default)
    {
        var text = BuildEmbeddingText(listing);

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogDebug("Skipping indexing for {ListingId}: no title or description", listing.ListingId);
            return new IndexingResult(IndexingAction.Skipped);
        }

        if (!embedContent)
        {
            return new IndexingResult(IndexingAction.Skipped);
        }

        var embedding = await _embeddingService.GetEmbedding(text, ct);
        _vectorIndex.Upsert(listing.ListingId, embedding);

        _logger.LogInformation("Embedded and indexed listing {ListingId}", listing.ListingId);
        return new IndexingResult(IndexingAction.Embedded);
    }

    // Keep BuildEmbeddingText unchanged
}
```

**Step 2: Rewrite ListingIndexingService_UnitTests**

Replace `Mock<IPineconeIndexClient>` with `Mock<IVectorIndex>`. Remove all Pinecone types.

Tests to keep:
- `Should_embed_and_upsert_when_new` — verify `_vectorIndex.Upsert()` called with ID and embedding
- `Should_skip_when_no_title_or_description` (5 cases) — unchanged
- `Should_return_empty_when_searching_empty_index` (rename to match)

Tests to modify:
- `Should_update_metadata_only_when_not_new` → `Should_skip_when_embed_content_is_false` — verify no embedding call, return `Skipped`

Tests to remove:
- `Should_include_all_metadata_fields_in_upsert` — no metadata
- `Should_omit_price_when_null` — no metadata
- `Should_default_null_condition_to_empty_string_in_metadata` — no metadata

**Step 3: Check for references to IndexingAction.MetadataUpdated**

Search the codebase for any code that checks for `IndexingAction.MetadataUpdated` and update to `Skipped`.

```bash
grep -r "MetadataUpdated" --include="*.cs" AIOMarketMaker/
```

**Step 4: Run all unit tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit"`
Expected: All unit tests pass.

**Step 5: Commit**

```bash
git add -A
git commit -m "refactor: simplify ListingIndexingService, drop metadata and Pinecone types"
```

---

### Task 5: Update DI Registrations

**Files:**
- Modify: `AIOMarketMaker.Api/Program.cs`
- Modify: `AIOMarketMaker.Api/appsettings.json`
- Modify: `AIOMarketMaker.Etl/Program.cs`

**Step 1: Update API Program.cs**

Replace the Pinecone DI block (lines 92-109):

```csharp
// Semantic search service (Pinecone)
var pineconeApiKey = configuration.GetValue<string>("Pinecone:ApiKey")
    ?? throw new InvalidOperationException("Pinecone:ApiKey is required.");
var pineconeConfig = new PineconeConfig(...);
builder.Services.AddSingleton(pineconeConfig);
builder.Services.AddSingleton<IPineconeIndexClient>(...);
builder.Services.AddSingleton<ISemanticSearchService, SemanticSearchService>();
builder.Services.AddSingleton<IListingIndexingService, ListingIndexingService>();
```

With:

```csharp
// Vector index (local USearch)
var vectorIndexConfig = new VectorIndexConfig(
    IndexPath: configuration.GetValue<string>("VectorIndex:IndexPath") ?? "./data/vectors.usearch",
    IdMapPath: configuration.GetValue<string>("VectorIndex:IdMapPath") ?? "./data/vectors-idmap.json",
    TopK: configuration.GetValue<int>("VectorIndex:TopK", 30),
    SimilarityThreshold: configuration.GetValue<float>("VectorIndex:SimilarityThreshold", 0.80f),
    Dimensions: configuration.GetValue<int>("VectorIndex:Dimensions", 3072),
    Connectivity: configuration.GetValue<int>("VectorIndex:Connectivity", 16),
    ExpansionAdd: configuration.GetValue<int>("VectorIndex:ExpansionAdd", 128),
    ExpansionSearch: configuration.GetValue<int>("VectorIndex:ExpansionSearch", 64));
builder.Services.AddSingleton(vectorIndexConfig);
builder.Services.AddSingleton<IVectorIndex>(sp =>
{
    var config = sp.GetRequiredService<VectorIndexConfig>();
    var index = new USearchVectorIndex(config);
    if (File.Exists(config.IndexPath) && File.Exists(config.IdMapPath))
    {
        index.Load();
        var logger = sp.GetRequiredService<ILogger<USearchVectorIndex>>();
        logger.LogInformation("Loaded vector index with {Count} vectors from {Path}",
            index.Count, config.IndexPath);
    }
    return index;
});
builder.Services.AddSingleton<ISemanticSearchService, SemanticSearchService>();
builder.Services.AddSingleton<IListingIndexingService, ListingIndexingService>();
```

**Step 2: Update appsettings.json**

Replace the `"Pinecone"` section (lines 41-46):

```json
"VectorIndex": {
    "IndexPath": "./data/vectors.usearch",
    "IdMapPath": "./data/vectors-idmap.json",
    "TopK": 30,
    "SimilarityThreshold": 0.80,
    "Dimensions": 3072,
    "Connectivity": 16,
    "ExpansionAdd": 128,
    "ExpansionSearch": 64
}
```

**Step 3: Update ETL Program.cs**

Replace the Pinecone DI block (lines 153-177) with the same `VectorIndexConfig` + `USearchVectorIndex` registration pattern. The ETL has additional complexity:
- It reads config from `local.settings.json` under `"Values"` section
- It still needs `IPineconeIndexClient` temporarily for the export migration script (Task 7) — but for now, replace the main DI registration

Also update the `IVectorIndex` registration to match the API pattern (load from disk at startup).

**Step 4: Build the full solution**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: Build succeeded. There may be compilation errors in `--validate` and `--k-analysis` if they reference `IPineconeIndexClient` — those are fixed in Task 6.

**Step 5: Commit**

```bash
git add -A
git commit -m "feat: wire up USearchVectorIndex DI in API and ETL, replace Pinecone config"
```

---

### Task 6: Update ETL --validate and --k-analysis Commands

**Files:**
- Modify: `AIOMarketMaker.Etl/Program.cs` (lines 251-559)

**Step 1: Update --validate command (lines 251-360)**

Replace `IPineconeIndexClient` usage with `IVectorIndex`:

Line 257: `var pinecone = scope.ServiceProvider.GetRequiredService<IPineconeIndexClient>();`
→ `var vectorIndex = scope.ServiceProvider.GetRequiredService<IVectorIndex>();`

Lines 305-313: Replace the Pinecone query:
```csharp
var request = new Pinecone.QueryRequest
{
    Id = listing.ListingId,
    TopK = (uint)(neighborsPerListing + 1),
    IncludeMetadata = false,
    IncludeValues = false
};
var response = await pinecone.Query(request);
var neighbors = response.Matches?
    .Where(m => m.Id != listing.ListingId)
    .Take(neighborsPerListing)
    .ToList() ?? [];
```

With:
```csharp
var neighbors = vectorIndex.SearchById(listing.ListingId, neighborsPerListing + 1)
    .Where(h => h.Id != listing.ListingId)
    .Take(neighborsPerListing)
    .ToList();
```

Update the loop that reads `neighbor.Score` → now `neighbor` is a `VectorSearchHit` with `.Score`.

**Step 2: Update --k-analysis command (lines 362-559)**

Same pattern — replace `IPineconeIndexClient` with `IVectorIndex`:

Line 373: `var pinecone = scope.ServiceProvider.GetRequiredService<IPineconeIndexClient>();`
→ `var vectorIndex = scope.ServiceProvider.GetRequiredService<IVectorIndex>();`

Lines 418-431: Replace Pinecone query with:
```csharp
var neighbors = vectorIndex.SearchById(listing.ListingId, maxK + 1)
    .Where(h => h.Id != listing.ListingId)
    .Take(maxK)
    .ToList();
```

**Step 3: Remove `using Pinecone;` from ETL Program.cs**

Line 1: Remove `using Pinecone;`

**Step 4: Build to verify**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeded.

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Program.cs
git commit -m "refactor: update ETL validate and k-analysis commands to use IVectorIndex"
```

---

### Task 7: Create Pinecone Export Migration Script

**Files:**
- Create: `AIOMarketMaker.Etl/ExportPineconeToUsearch.cs` (or add a `--export-vectors` command to ETL Program.cs)

**Step 1: Add --export-vectors command to ETL Program.cs**

This command needs to:
1. Create a temporary `PineconeIndexClientWrapper` (using the Pinecone API key from config) — this is the ONLY remaining Pinecone usage
2. Fetch all listing IDs from the database
3. Batch-fetch vectors from Pinecone (Fetch API, batches of 1000 IDs)
4. Build a new `USearchVectorIndex` with those vectors
5. Save to disk

Add this before the `host.Run()` line in `Program.cs`:

```csharp
if (args.Contains("--export-vectors"))
{
    await ExportVectorsFromPinecone(host, args);
    return;
}
```

**Important pre-run checklist for the user:**
- **Pinecone API key** must be in `local.settings.json` under `Pinecone:ApiKey`
- **Batch size**: 1000 IDs per Fetch call (Pinecone limit)
- **Expected duration**: ~208 batches × ~0.5s = ~2 minutes
- **Expected output**: ~2.4 GB index file + ~5 MB ID map JSON
- **Disk space required**: ~2.5 GB at the configured `VectorIndex:IndexPath`

```csharp
static async Task ExportVectorsFromPinecone(IHost host, string[] args)
{
    // 1. Read Pinecone config from settings
    // 2. Create PineconeIndexClientWrapper directly (not via DI)
    // 3. Load all listing IDs from database
    // 4. Create VectorIndexConfig from settings
    // 5. Create USearchVectorIndex
    // 6. Batch-fetch from Pinecone (1000 IDs at a time)
    //    - pinecone.Fetch(new FetchRequest { Ids = batch })
    //    - For each vector in response.Vectors: index.Upsert(id, values)
    // 7. Save index to disk
    // 8. Report: X vectors exported, Y missing, file sizes
}
```

**Step 2: Build and verify**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeded.

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Program.cs
git commit -m "feat: add --export-vectors command to migrate Pinecone data to local USearch index"
```

**Step 4: Run the export (REQUIRES USER APPROVAL)**

This makes ~208 Pinecone API calls and writes ~2.5 GB to disk. Get user approval before running.

```bash
dotnet run --project AIOMarketMaker/AIOMarketMaker.Etl -- --export-vectors
```

---

### Task 8: Remove Pinecone SDK and Clean Up

**Files:**
- Delete: `AIOMarketMaker.Core/Services/IPineconeIndexClient.cs`
- Modify: `AIOMarketMaker.Core/Services/SemanticSearchModels.cs` (remove `PineconeConfig`)
- Modify: `AIOMarketMaker.Core/AIOMarketMaker.Core.csproj` (remove `Pinecone.Client`)
- Modify: `AIOMarketMaker.Etl/Program.cs` (remove temporary Pinecone import for export)

**Step 1: Delete IPineconeIndexClient.cs**

Remove: `AIOMarketMaker.Core/Services/IPineconeIndexClient.cs`

This deletes `IPineconeIndexClient` interface and `PineconeIndexClientWrapper` class.

**Step 2: Remove PineconeConfig from SemanticSearchModels.cs**

Delete the `PineconeConfig` record (lines 3-9). The remaining records (`SemanticSearchHit`, `SemanticSearchResult`, `IndexResult`) stay.

**Step 3: Remove Pinecone NuGet package**

```bash
dotnet remove AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj package Pinecone.Client
```

**Step 4: Search for any remaining Pinecone references**

```bash
grep -r "Pinecone\|pinecone\|IPineconeIndexClient\|PineconeConfig\|PineconeIndexClientWrapper" --include="*.cs" AIOMarketMaker/
```

Fix any remaining references. Expected locations that may still reference Pinecone:
- Integration tests (`SemanticSearchServiceIntegrationTests.cs`) — update or delete
- Any `using Pinecone;` statements left behind

**Step 5: Build the entire solution**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: Build succeeded with no Pinecone references.

**Step 6: Run all unit tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit"`
Expected: All tests pass.

**Step 7: Commit**

```bash
git add -A
git commit -m "chore: remove Pinecone SDK, delete IPineconeIndexClient, clean up references"
```

---

### Task 9: End-to-End Verification

**Step 1: Verify vector index loads at startup**

Start the API:
```bash
dotnet run --project AIOMarketMaker/AIOMarketMaker.Api
```

Look for log message: `Loaded vector index with {Count} vectors from {Path}`

**Step 2: Run comparables ETL dry run**

```bash
dotnet run --project AIOMarketMaker/AIOMarketMaker.Etl -- --comparables --dry-run
```

Compare the output (listings processed, queries, comparables found) with a previous Pinecone-backed run to verify behavioral parity.

**Step 3: Run --validate**

```bash
dotnet run --project AIOMarketMaker/AIOMarketMaker.Etl -- --validate
```

Verify neighbors are returned and classified correctly.

**Step 4: Commit if any final adjustments needed**

```bash
git add -A
git commit -m "chore: final cleanup after local vector index migration"
```
