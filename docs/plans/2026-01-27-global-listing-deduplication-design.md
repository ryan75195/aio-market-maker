# Global Listing Deduplication Design

## Problem

Currently, the `FilterNewListings` logic checks for duplicates per-job:
```csharp
WHERE ScrapeJobId = @JobId
```

This means:
- The same eBay listing can be stored multiple times if found by different jobs
- Sold listings are re-scraped unnecessarily across jobs
- No global deduplication despite the DB having a unique index on `ListingId`

## Solution

Change from "filter + insert" to "global upsert with status protection":
1. Check globally if listing exists (not per-job)
2. Insert new listings, update existing ones
3. Never downgrade status (Sold -> Active)
4. Preserve `CreatedUtc` (first discovery date)

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Job association | First-writer wins | Only for debugging/logging |
| Active listings | Can be updated by any job | Need ongoing status monitoring |
| Sold listings | Never overwrite status | Once sold, status is final |
| Re-scraping | Allowed | Dedup happens at save time, not scrape time |
| Protected fields | `CreatedUtc`, `ScrapeJobId` | Preserve first discovery |

## Status Hierarchy

Status can only progress forward, never backward:
```
Active < OutOfStock < Ended < Sold
```

## Upsert Logic

```
When saving a listing:
1. Check: Does this ListingId exist globally?
2. If NO  -> INSERT new record (CreatedUtc = now, ScrapeJobId = current job)
3. If YES -> Check existing status:
   - If existing is Sold          -> Skip status update, optionally update other fields
   - If existing is Active/other  -> UPDATE with new data (respecting status hierarchy)
   - Always preserve CreatedUtc and ScrapeJobId
   - Always set UpdatedUtc = now
```

## Code Changes

### 1. JobRunner.cs

**Remove or simplify `FilterNewListings`** - no longer filtering by job

**Replace `SaveEbayListings` with `UpsertListings`:**
```csharp
private async Task<int> UpsertListings(
    IEnumerable<EbayProduct> ebayProducts,
    int jobId,
    CancellationToken ct)
{
    var insertCount = 0;
    var updateCount = 0;

    foreach (var product in ebayProducts.Where(p => p.ListingId != null))
    {
        var existing = await _dbContext.Listings
            .FirstOrDefaultAsync(l => l.ListingId == product.ListingId, ct);

        if (existing == null)
        {
            // INSERT new listing
            _dbContext.Listings.Add(MapToListing(product, jobId));
            insertCount++;
        }
        else
        {
            // UPDATE existing listing (with status protection)
            UpdateExistingListing(existing, product);
            updateCount++;
        }
    }

    await _dbContext.SaveChangesAsync(ct);
    _logger.LogInformation("Upserted listings: {Inserted} inserted, {Updated} updated",
        insertCount, updateCount);

    return insertCount;
}
```

**Add status hierarchy helper:**
```csharp
private static readonly Dictionary<string, int> StatusRank = new()
{
    { "Active", 0 },
    { "OutOfStock", 1 },
    { "Ended", 2 },
    { "Sold", 3 }
};

private static bool CanUpdateStatus(string? existingStatus, string? newStatus)
{
    var existingRank = StatusRank.GetValueOrDefault(existingStatus ?? "", -1);
    var newRank = StatusRank.GetValueOrDefault(newStatus ?? "", -1);
    return newRank >= existingRank;
}

private void UpdateExistingListing(Listing existing, EbayProduct product)
{
    // Only update status if it's a forward progression
    if (CanUpdateStatus(existing.ListingStatus, product.ListingStatus?.ToString()))
    {
        existing.ListingStatus = product.ListingStatus?.ToString();
        existing.EndDateUtc = product.EndDateUtc;
    }

    // Always update data fields (price may change for active listings)
    existing.Title = product.Title;
    existing.Price = product.Price;
    existing.Currency = product.Currency;
    // ... other fields

    // Preserve CreatedUtc and ScrapeJobId (don't touch them)
    existing.UpdatedUtc = DateTime.UtcNow;
}
```

### 2. FilterNewListingsActivity.cs (Durable Functions)

Apply same logic for the Azure Functions path. Either:
- Remove this activity entirely (upsert handles everything)
- Or change to return all listings (let upsert handle dedup)

### 3. DetectAndUpdateSoldListings

Update to check global DB state before re-scraping:
```csharp
// Before re-scraping, check if another job already updated this listing
var alreadySold = await _dbContext.Listings
    .Where(l => transitionedListingIds.Contains(l.ListingId) && l.ListingStatus == "Sold")
    .Select(l => l.ListingId)
    .ToListAsync(ct);

// Only re-scrape listings not already marked as Sold
var needsRescrape = transitionedListingIds.Except(alreadySold).ToArray();
```

## Database

### Schema Changes
None required - `ListingId` already has a unique index.

### Data Cleanup
If duplicates exist (shouldn't, but verify):
```sql
-- Check for duplicates
SELECT ListingId, COUNT(*) as cnt
FROM Listings
GROUP BY ListingId
HAVING COUNT(*) > 1;

-- Remove duplicates (keep earliest)
DELETE FROM Listings
WHERE Id NOT IN (
    SELECT MIN(Id)
    FROM Listings
    GROUP BY ListingId
);
```

## Testing

1. **Unit tests for status hierarchy:**
   - Active -> Sold: allowed
   - Sold -> Active: blocked
   - Active -> Active: allowed (data update)

2. **Unit tests for upsert logic:**
   - New listing: inserted with CreatedUtc
   - Existing listing: updated, CreatedUtc preserved
   - Existing sold listing: status not changed

3. **Integration test:**
   - Two jobs find same listing
   - First job inserts
   - Second job updates (no duplicate)

## Migration Path

1. Deploy code changes (upsert logic)
2. Run duplicate check SQL (should find none)
3. If duplicates found, run cleanup SQL
4. Monitor logs for "inserted" vs "updated" counts
