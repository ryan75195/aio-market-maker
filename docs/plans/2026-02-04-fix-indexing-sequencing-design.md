# Fix Indexing Sequencing Design

**Problem:** New listings are never indexed into Pinecone. Indexing only happens for existing listings updated via `UpdateListingsFromSummary`. On a fresh database, the first scrape run creates listings and enqueues descriptions, but `RefreshComparables` runs before descriptions are fetched and before anything is in Pinecone. Result: 0 comparables, 0 vectors.

**Solution:** Index each listing to Pinecone when its description is processed in `ListingProcessorService`. Move `RefreshComparables` from `ScrapeJobProcessor` (runs too early) to the completion path (runs after all listings are indexed).

## Design Decisions

1. **Only index when description is complete** - Listings with missing/failed descriptions are not indexed. Description content is critical for meaningful similarity matching.

2. **Pinecone is a required dependency** - No `NullListingIndexingService` fallback. ETL should fail at startup if Pinecone isn't configured.

3. **Fail fast on indexing errors** - If OpenAI or Pinecone throws during indexing, the exception propagates. The description is already saved, but the listing isn't marked complete. Workers can retry.

4. **RefreshComparables moves to completion** - Called by `ScrapeRunCounterService` when the last listing is processed, and by `CompletionCheckTrigger` as a fallback.

5. **`UpdateListingsFromSummary` indexing stays** - It updates metadata (`isNew: false`) for already-indexed vectors. No duplication with the new `isNew: true` calls.

## Changes

| Change | File |
|--------|------|
| Add `IListingIndexingService`, index after description saved | `ListingProcessorService.cs` |
| Remove `RefreshComparables` call from `ExecuteScrape` | `ScrapeJobProcessor.cs` |
| Add `scrapeJobId` param to `Increment`, refresh on completion | `IScrapeRunCounterService` + impls |
| Refresh on fallback completion | `CompletionCheckTrigger.cs` |
| Register `IListingIndexingService` as required | ETL `Program.cs` |
| Update tests for new dependencies and behavior | 3 test files |
