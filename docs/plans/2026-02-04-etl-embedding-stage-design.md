# ETL Embedding Stage Design

## Goal

After the ETL pipeline processes a listing, embed it and index it in Pinecone with metadata so that similar listings can be found for price comparison and arbitrage detection.

## Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Embedding text | Title + Description | Maximum context for similarity matching |
| Filtering approach | Pinecone metadata | Single round-trip, no ID cap, Pinecone-optimized. Avoids `$in` anti-pattern (10K limit, full namespace scan) |
| When to embed | Inline after each listing processes | No separate phase needed. Failures don't block other listings |
| Re-scrape handling | Metadata-only update (no re-embedding) | Title+description don't change on updates. Saves OpenAI API call. Uses Pinecone `UpdateAsync` |
| Error handling | Fail-fast, propagate exceptions | Embedding failure = listing processing failure. ScrapeRunListing stays incomplete, retryable |
| Pre-existing unindexed listings | Ignore for now | Metadata updates on unindexed listings silently do nothing. Backfill is a separate concern |
| Async suffix convention | Remove from touched interfaces | Codebase convention: no `Async` suffix. Rename `IPineconeIndexClient`, `IEmbeddingService`, `ISemanticSearchService` methods |

## Metadata Per Vector

| Field | Type | Source | Purpose |
|-------|------|--------|---------|
| `listingId` | string | `Listing.ListingId` | Correlation back to DB |
| `scrapeJobId` | long | `Listing.ScrapeJobId` | Filter by product category |
| `price` | double | `Listing.Price` | Price range filtering |
| `shippingCost` | double | `Listing.ShippingCost` | Total cost comparison |
| `condition` | string | `Listing.Condition` | Apples-to-apples comparison |
| `listingStatus` | string | `Listing.ListingStatus` | Active vs Sold filtering |
| `purchaseFormat` | string | `Listing.PurchaseFormat` | Auction vs BuyItNow filtering |
| `soldDateUtc` | string (ISO 8601) | `ListingStatusHistory.SoldDateUtc` | Time-scoped sold queries |
| `createdUtc` | string (ISO 8601) | `Listing.CreatedUtc` | Discovery date filtering |

## Component Architecture

### New: `IListingIndexingService`

Lives in `AIOMarketMaker.Core/Services/`. Handles the embed-or-update decision and Pinecone interaction.

```csharp
public interface IListingIndexingService
{
    Task<IndexingResult> Index(Listing listing, bool isNew);
}

public record IndexingResult(IndexingAction Action, string? Error);

public enum IndexingAction
{
    Embedded,        // New listing: generated embedding + upserted
    MetadataUpdated, // Existing listing: metadata-only update
    Skipped          // No title or description to embed
}
```

Logic:
- `isNew: true` -> Build text from Title + Description -> `IEmbeddingService.GetEmbedding` -> Pinecone upsert with vector + metadata
- `isNew: false` -> `IPineconeIndexClient.Update` with current metadata only (no OpenAI call)
- Title and description both null/empty -> return `Skipped`
- On exception -> rethrow (fail-fast)

### Modified: `IPineconeIndexClient`

Add `Update` method. Rename all methods to drop `Async` suffix:

```csharp
public interface IPineconeIndexClient
{
    Task Upsert(UpsertRequest request, CancellationToken ct = default);
    Task<QueryResponse> Query(QueryRequest request, CancellationToken ct = default);
    Task Delete(DeleteRequest request, CancellationToken ct = default);
    Task<FetchResponse> Fetch(FetchRequest request, CancellationToken ct = default);
    Task Update(UpdateRequest request, CancellationToken ct = default); // new
}
```

### Modified: `IEmbeddingService`

Rename methods to drop `Async` suffix:

- `GetEmbeddingAsync` -> `GetEmbedding`
- `GetEmbeddingsAsync` -> `GetEmbeddings`

### Modified: `ISemanticSearchService`

Rename methods to drop `Async` suffix:

- `IndexListingsAsync` -> `IndexListings`
- `SearchAsync` -> `Search`
- `FindSimilarAsync` -> `FindSimilar`
- `DeleteAsync` -> `Delete`
- `ExistsAsync` -> `Exists`

## Integration Points

### 1. `ListingProcessorService.Process` (new and re-scraped listings)

After upsert + status history, before marking ScrapeRunListing as Complete:

```
8.  Upsert listing (create new or update existing)
9.  Create status history
9a. Index in Pinecone (embed if new, metadata-update if existing)  <-- NEW
10. Mark ScrapeRunListing as Complete
11. Increment counters
```

- `wasCreated = true` -> `Index(listing, isNew: true)` -> full embedding + upsert
- `wasCreated = false` -> `Index(listing, isNew: false)` -> metadata-only update

If indexing throws, ScrapeRunListing stays incomplete and is retryable.

### 2. `ScrapeJobProcessor.UpdateListingsFromSummary` (price/shipping updates from search page)

Pinecone metadata updates happen **after** `SaveChangesAsync`, in a second pass over updated listings. This ensures DB changes are committed before Pinecone calls. If Pinecone fails, the DB is consistent and the next run will attempt metadata updates again.

```
1. Loop: compare summary vs existing, track changes, update entities
2. SaveChangesAsync (commit all DB changes)
3. Loop over changed listings: Index(listing, isNew: false)  <-- NEW
```

`ScrapeJobProcessor` gets `IListingIndexingService` as a new constructor dependency.

## Error Handling

- Embedding/Pinecone failures propagate (fail-fast per codebase convention)
- In `ListingProcessorService`: if `Index` throws, ScrapeRunListing stays incomplete -> retryable on next run
- In `UpdateListingsFromSummary`: DB changes are committed first via `SaveChangesAsync`. Pinecone updates happen in a second pass. If Pinecone fails mid-batch, some metadata updates are lost but DB is consistent. Next run will re-attempt.

## Testing Plan

### `ListingIndexingService` unit tests

- `Should_embed_and_upsert_when_new` -- verifies `IEmbeddingService.GetEmbedding` called, `IPineconeIndexClient.Upsert` called with correct metadata
- `Should_update_metadata_only_when_not_new` -- verifies `Update` called, `GetEmbedding` NOT called
- `Should_skip_when_no_title_or_description` -- returns `Skipped`, no external calls
- `Should_include_all_metadata_fields` -- verifies price, condition, status, dates etc. in upsert/update metadata

### `ListingProcessorService` integration with indexing

- `Should_index_new_listing_after_upsert` -- mock `IListingIndexingService`, verify called with `isNew: true`
- `Should_index_updated_listing_after_upsert` -- verify called with `isNew: false`
- `Should_fail_processing_when_indexing_fails` -- verify ScrapeRunListing not marked Complete

### `ScrapeJobProcessor.UpdateListingsFromSummary` integration

- `Should_update_pinecone_metadata_for_changed_listings` -- verify `Index` called for price-changed listings
- `Should_not_update_pinecone_for_unchanged_listings` -- verify `Index` not called when skipped

## DI Registration

In `Startup.cs` (ETL console app), add:

```csharp
services.AddSingleton<IListingIndexingService, ListingIndexingService>();
```

No new configuration needed -- `ListingIndexingService` depends on `IEmbeddingService`, `IPineconeIndexClient`, and `EmbeddingConfig` which are already registered.
