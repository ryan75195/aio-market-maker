# Summary Update Optimization Design

## Problem

The scrape pipeline currently discards all data from search results except listing IDs. For existing active listings, it enqueues a full page scrape just to check if price/status changed — even though the search results page already provides that information.

## Solution

Use search result summary data (`EbayProductSummary`) to update existing active listings directly, reserving full scrapes for new listings and sold transitions.

## Classification Logic

After both sold and active searches complete, merge all summaries (deduplicating by ListingId — if a listing appears in both, the sold one wins). Then classify each:

1. **Look up in DB** by `ListingId` + `ScrapeJobId`
2. **Terminal status** (existing is Sold/Ended/OutOfStock) → skip entirely
3. **Sold heuristic** (`IsSoldListing` check on search result element) → full scrape (important transition, need accurate sold date/price)
4. **New listing** (not in DB) → full scrape (need description, item specifics, images)
5. **Existing + still active** → update from summary data (price, condition, shipping)

```
Search Results (EbayProductSummary with IsSold flag)
        │
        ├── Terminal in DB ──────────────► Skip (no work)
        ├── New (not in DB) ─────────────► Full scrape via EnqueueScrapeWork
        ├── Existing + IsSold=true ──────► Full scrape via EnqueueScrapeWork
        └── Existing + IsSold=false ─────► Update from summary in-place
```

## Summary Update Path

For "existing + still active" listings, `ScrapeJobProcessor` handles updates synchronously (no queue):

1. Compare price, condition, shipping against existing `Listing` record
2. If changed:
   - Update listing fields + `UpdatedUtc`
   - Create `ListingStatusHistory` with `Source = "SummaryUpdate"`
   - Increment `ListingsUpdated` on `ScrapeRun`
3. If unchanged:
   - Increment `ListingsSkipped` on `ScrapeRun`

No `ScrapeRunListing` records for summary updates — those track async scrape pipeline items only.

## Data Model Changes

**`EbayProductSummary`** — add `bool IsSold` to the record. The parser already computes `IsSoldListing(li)` internally but doesn't expose it.

**`ClassifiedListings`** — new record replacing `FilterResult`:
```csharp
public record ClassifiedListings(
    List<EbayProductSummary> ToScrape,
    List<EbayProductSummary> ToUpdateFromSummary,
    int TerminalCount);
```

## ScrapeRun Counter Handling

- Summary updates increment `ListingsUpdated` or `ListingsSkipped` (same counters as full scrape path)
- `ListingsProcessed` includes summary-updated and skipped listings
- `TotalListingsFound` still reflects total from search
- Completion check still works: `ListingsProcessed >= TotalListingsFound - ListingsFilteredPreQueue`

## Files Changed

**Core:**
- `IEbayProductSummary.cs` — add `IsSold` property
- `EbaySearchParser.cs` — pass `IsSoldListing(li)` into the new field

**ETL:**
- `ScrapeJobProcessor.cs`:
  - `SearchListings` returns `List<EbayProductSummary>` instead of `HashSet<string>`
  - `FilterNewListings` → `ClassifyListings` returning `ClassifiedListings`
  - New `UpdateListingsFromSummary` method
  - `CreateAndEnqueueListings` takes `List<EbayProductSummary>` instead of `List<string>`
  - `RunScrape` updated to call both paths
- `FilterResult` record replaced by `ClassifiedListings`

**Tests:**
- Update `ScrapeJobProcessor_UnitTests` to use summaries with `IsSold` flag
- New tests: summary update with price change, summary skip when unchanged, sold heuristic routes to scrape, new listing routes to scrape

**Unchanged:**
- `ProcessListingEndpoint` — still handles full scrapes
- `ScrapeRunService` / triggers — upstream unchanged
- `WebscraperClient.EnqueueScrapeWork` — still used for scrape batch
