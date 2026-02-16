# Local Vector Index Migration Design

## Goal

Replace Pinecone cloud vector database with a local USearch HNSW index to eliminate cloud costs ($50-144/month) and achieve 30-100x faster batch queries.

## Current State

- **Pinecone index**: `arbitrage`, 208K vectors, 3072 dimensions (text-embedding-3-large), cosine similarity
- **Storage**: ~2.4 GB, exceeding Pinecone free tier (2 GB limit)
- **Main consumer**: `ComparablesEtlService` queries all 114K active listings (topK=50, threshold=0.80)
- **Current latency**: 50-150ms per query (network round-trip to us-east-1), ~20-30 min for full ETL run
- **Metadata**: Stored on each vector (listingId, price, condition, etc.) but unused in hot path — filtering done post-query in application code

## Decision: USearch

Library: [Cloud.Unum.USearch](https://www.nuget.org/packages/Cloud.Unum.USearch/2.23.0) (v2.23.0, Jan 2026)

- Native C++ HNSW engine with .NET Standard 2.0 bindings
- Actively maintained, used by Google/ClickHouse/DuckDB
- Built-in disk persistence (save/load)
- Cosine similarity, configurable connectivity and expansion parameters
- 482 KB NuGet package, no dependencies

Rejected alternatives:
- **HNSW (pure C#)**: 5-10x slower for batch operations at 200K+ scale
- **FaissNet**: Dormant (last update July 2023), 44 MB package, overkill

## Architecture

```
Before:  SemanticSearchService → IPineconeIndexClient → Pinecone Cloud
After:   SemanticSearchService → IVectorIndex → USearchVectorIndex (local)
```

### Key Decisions

1. **No metadata in the vector index.** Pinecone stored metadata (price, condition, status) on each vector, but the hot path (`ComparablesEtlService`) passes `null` for all metadata filters. All metadata is already in SQL. Removing metadata simplifies the index to pure vector storage.

2. **Keep OpenAI embeddings.** Only the storage/search layer changes. `text-embedding-3-large` continues to generate 3072-dim vectors. Embedding costs are negligible (~$0.01 per 1K listings).

3. **Disk persistence alongside SQLite.** The USearch index file lives next to the SQLite database. Path configured in `appsettings.json`.

4. **String-to-ulong ID mapping.** USearch uses `ulong` keys internally. A `ConcurrentDictionary<string, ulong>` maps eBay listing IDs to USearch keys. The mapping is serialized alongside the index.

## Interface

```csharp
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
    void Save(string path);
    void Load(string path);
}
```

Synchronous methods (no `Task`) — USearch operations are CPU-bound and sub-millisecond. Wrapping in `Task.Run` at the call site if needed for async contexts.

## What Changes

| Component | Change |
|-----------|--------|
| `IVectorIndex` | **New** — clean vector-only interface |
| `USearchVectorIndex` | **New** — wraps USearch + string↔ulong ID mapping |
| `VectorIndexConfig` | **New** — replaces `PineconeConfig` (path, topK, threshold) |
| `SemanticSearchService` | **Simplified** — uses `IVectorIndex`, drops Pinecone `Metadata` types and filter logic |
| `ListingIndexingService` | **Simplified** — uses `IVectorIndex`, drops `BuildMetadata()` and metadata-only update path |
| `Program.cs` (API + ETL) | **Modified** — DI registration swap, load index from disk at startup |
| `IPineconeIndexClient` | **Deleted** |
| `PineconeIndexClientWrapper` | **Deleted** |
| `PineconeConfig` | **Deleted** |
| `Pinecone.Client` NuGet | **Removed** from `.csproj` |

## What Stays Unchanged

| Component | Why |
|-----------|-----|
| `ISemanticSearchService` interface | Consumers don't see the storage layer |
| `ComparablesEtlService` | Talks to `ISemanticSearchService`, not the index directly |
| `IEmbeddingService` / `EmbeddingService` | Still uses OpenAI `text-embedding-3-large` |
| `SemanticSearchModels.cs` | `SemanticSearchResult`, `SemanticSearchHit`, `IndexResult` records stay |
| All tests that mock `ISemanticSearchService` | Interface unchanged |

## Migration Strategy

1. **Export from Pinecone**: Console task fetches all 208K vectors from Pinecone (Fetch API in batches of 1000 by listing ID from the database), builds USearch index, saves to disk.
2. **Swap implementation**: Replace DI registrations, update `SemanticSearchService` and `ListingIndexingService`.
3. **Verify**: Run comparables ETL against local index, compare results with Pinecone output.
4. **Remove Pinecone**: Delete SDK reference, wrapper, config. Delete legacy Pinecone indexes.

## Performance Expectations

| Metric | Pinecone (current) | Local USearch |
|--------|--------------------|---------------|
| Single query | 50-150ms | <1ms |
| 114K ETL queries | 20-30 min | 10-60 sec |
| Memory footprint | N/A (cloud) | ~3.6 GB RAM |
| Monthly cost | $50-144 | $0 |
| Cold start | Instant (cloud) | ~2-5 sec (load from disk) |

## Configuration

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
