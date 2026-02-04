# Opportunities Enrichment Design

## Goal

Show the most profitable arbitrage opportunities by computing average sold price from similar listings, estimated time to sell, and potential profit. Order by potential profit descending.

## Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| When to compute | After each scrape run (ETL phase) | 100k+ listings makes live Pinecone queries infeasible |
| Storage approach | Junction table (`ListingPricingComparables`) | Average price computed from live data via SQL, never stale. Captures which listings are comparable for future use |
| FK type | Integer `Listings.Id` | Clean joins, referential integrity |
| Similar listings source | Pinecone `FindSimilar` with `listingStatus == "Sold"` metadata filter | Server-side filtering, only returns sold listings |
| TopK | 50 | Balance between accuracy and Pinecone query cost |
| Concurrency | 10 parallel Pinecone queries | Avoid overwhelming Pinecone API |
| Price calculation | SQL AVG at query time | Always reflects latest prices without recomputation |
| Time to sell | AVG days between `CreatedUtc` and `SoldDateUtc` from comparables | Uses existing data, no new ETL needed |

## New Table: `ListingPricingComparables`

| Column | Type | Purpose |
|--------|------|---------|
| `Id` | int (PK, identity) | Primary key |
| `ListingId` | int (FK -> Listings.Id) | The active listing being priced |
| `ComparableListingId` | int (FK -> Listings.Id) | The similar sold listing |
| `SimilarityScore` | float | Pinecone cosine similarity score |
| `CreatedUtc` | DateTime | When this relationship was discovered |

Indexes:
- `IX_ListingPricingComparables_ListingId` on `ListingId` (for the JOIN)
- `IX_ListingPricingComparables_ComparableListingId` on `ComparableListingId`

## Component Changes

### Modified: `ISemanticSearchService.FindSimilar`

Add optional `Metadata? metadataFilter` parameter to support filtering by listing status:

```csharp
Task<SemanticSearchResult> FindSimilar(
    string listingId,
    IEnumerable<string>? filterToListingIds = null,
    Metadata? metadataFilter = null,
    int? topK = null,
    CancellationToken ct = default);
```

The existing `BuildIdFilter` merges with the metadata filter before passing to Pinecone.

### New: `IComparablesRefreshService`

Lives in `AIOMarketMaker.Core/Services/`. Handles the Pinecone query + DB write for comparables.

```csharp
public interface IComparablesRefreshService
{
    Task<ComparablesRefreshResult> Refresh(
        IEnumerable<Listing> activeListings,
        CancellationToken ct = default);
}

public record ComparablesRefreshResult(int ListingsProcessed, int ComparablesFound);
```

Logic:
1. For each active listing, call `FindSimilar(listingId, metadataFilter: soldFilter, topK: 50)`
2. Resolve Pinecone string IDs to integer `Listing.Id` via batch DB lookup
3. Delete existing comparables for these listings
4. Insert new comparables
5. Run with `SemaphoreSlim(10)` for parallel Pinecone queries

### Modified: `ScrapeJobProcessor`

Add new phase after `UpdateListingsFromSummary` / `EnqueueListingsForScrape`:

```
1. Search sold + active listings
2. Classify listings
3. Update from summary (existing)
4. Enqueue for scrape (existing)
5. Refresh comparables for active listings  <-- NEW
6. Mark completed
```

### Modified: `GET /api/listings/active`

Replace the current simple query with a query that computes average sold price, sold count, estimated days to sell, and potential profit:

```sql
SELECT l.*,
       AVG(comp.Price) as AverageSoldPrice,
       COUNT(comp.Id) as SimilarSoldCount,
       AVG(DATEDIFF(day, comp.CreatedUtc, lsh.SoldDateUtc)) as EstimatedDaysToSell,
       (AVG(comp.Price) - l.Price) as PotentialProfit
FROM Listings l
LEFT JOIN ListingPricingComparables lpc ON l.Id = lpc.ListingId
LEFT JOIN Listings comp ON lpc.ComparableListingId = comp.Id
LEFT JOIN ListingStatusHistory lsh
    ON comp.Id = lsh.ListingId AND lsh.SoldDateUtc IS NOT NULL
WHERE l.ListingStatus = 'Active'
GROUP BY l.Id, l.ListingId, l.Title, l.Price, l.Currency, l.ShippingCost,
         l.Url, l.Condition, l.ListingStatus, l.Location, l.EndDateUtc,
         l.CreatedUtc, l.ScrapeJobId
ORDER BY (AVG(comp.Price) - l.Price) DESC
OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY
```

Returns a named record (not anonymous type) with all listing fields plus enrichment fields.

### Modified: UI (`index.html` + `app.js`)

Add columns to the Opportunities table:
- **Avg Sold Price** — e.g. "£95.50 (47 sold)"
- **Est. Time to Sell** — e.g. "~12 days"
- **Potential Profit** — e.g. "+£15.50" (green) or "-£5.00" (red)

Table is already ordered by potential profit from the API.

## Error Handling

- If Pinecone is not configured (NullListingIndexingService), skip the comparables refresh phase
- If a listing is not indexed in Pinecone, skip it (no comparables)
- Pinecone query failures for individual listings are logged and skipped (don't fail the entire refresh)
- Listings with no comparables show "-" for all enrichment fields in the UI

## Testing Plan

### `ComparablesRefreshService` unit tests

- `Should_query_pinecone_for_each_active_listing` — verifies FindSimilar called per listing with sold filter
- `Should_resolve_pinecone_ids_to_db_ids` — verifies string-to-int ID resolution
- `Should_replace_old_comparables` — verifies delete + insert
- `Should_skip_listings_not_in_pinecone` — verifies graceful handling
- `Should_limit_concurrency` — verifies parallel queries don't exceed limit

### `ScrapeJobProcessor` integration tests

- `Should_refresh_comparables_after_scrape` — verifies phase runs
- `Should_skip_comparables_when_pinecone_not_configured` — verifies NullListingIndexingService path

### `GetActiveListings` endpoint tests

- `Should_return_average_sold_price_from_comparables` — verifies SQL aggregation
- `Should_return_estimated_days_to_sell` — verifies time calculation
- `Should_order_by_potential_profit_descending` — verifies sort order
- `Should_handle_listings_with_no_comparables` — verifies null handling

## DI Registration

```csharp
services.AddScoped<IComparablesRefreshService, ComparablesRefreshService>();
```

Depends on `ISemanticSearchService` (Singleton) and `EtlDbContext` (Scoped). Register as Scoped.
