# Real-Time Progress Updates Design

**Goal:** Add real-time progress tracking to the History view showing scrape job completion percentage.

**Approach:** Enhanced polling (2-second interval) with progress bar showing `ListingsProcessed / TotalListingsFound`.

---

## Data Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                         API (Functions)                          │
├─────────────────────────────────────────────────────────────────┤
│ 1. POST /api/scrape/start                                        │
│    → Search eBay, get listing IDs                                │
│    → Filter out existing listings                                │
│    → Create ScrapeRun (TotalListingsFound = filtered count)      │
│    → Submit URLs to scraper with GroupId/FileKey                 │
│    → Return { runId }                                            │
├─────────────────────────────────────────────────────────────────┤
│ 2. GET /api/history                                              │
│    → Returns ScrapeRuns with ListingsProcessed/TotalListingsFound│
│    → UI calculates percentage                                    │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    Scraper (WebScraper)                          │
│    Saves HTML to blob: html/{scrapeRunId}/{listingId}/listing.html│
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                         ETL (Blob Triggers)                      │
├─────────────────────────────────────────────────────────────────┤
│ ProcessListingActivity:                                          │
│    → Parse listing HTML                                          │
│    → Save to Listings table                                      │
│    → Increment ScrapeRun.ListingsProcessed (atomic)              │
│    → Mark complete when ListingsProcessed >= TotalListingsFound  │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                      UI (Electron Desktop)                       │
│    Polls /api/history every 2 seconds when jobs running          │
│    Shows progress bar: ListingsProcessed / TotalListingsFound    │
└─────────────────────────────────────────────────────────────────┘
```

---

## API Changes

### `POST /api/scrape/start` (New)

Orchestrates initial job setup:

1. Get enabled ScrapeJobs from DB
2. For each job:
   - Build search URL
   - Submit to scraper, wait for HTML
   - Parse search results → listing IDs
3. Filter out listing IDs already in Listings table
4. Create ScrapeRun record:
   - `Status = "Running"`
   - `TotalListingsFound = filtered count`
   - `ListingsProcessed = 0`
5. For each new listing ID:
   - Build listing URL + description URL
   - Submit to scraper with `GroupId=listingId`, `FileKey="listing"/"description"`
   - Use `ScrapeRun.Id` as the scraper job ID
6. Return `{ runId }`

### `GET /api/history` (Existing)

No changes needed - already returns `ListingsProcessed` and `TotalListingsFound`.

---

## ETL Changes

### Update `ProcessListingInput`

Add `ScrapeRunId` field:

```csharp
public record ProcessListingInput(
    string JobId,          // Same as ScrapeRunId
    string ListingId,
    int ScrapeJobId,
    int ScrapeRunId,       // NEW: For progress tracking
    bool HasDescription
);
```

### Update `ListingEtlInput`

Add `ScrapeRunId` field (parsed from blob path `{jobId}`):

```csharp
public record ListingEtlInput(
    string JobId,          // This IS the ScrapeRunId
    string ListingId,
    TriggerSource TriggerSource
);
```

### Update `ProcessListingActivity`

After saving listing, increment progress atomically:

```csharp
// After _dbContext.SaveChangesAsync()...

// Increment progress and check completion atomically
// Status change is soft indicator - doesn't gate ETL processing
await _dbContext.Database.ExecuteSqlRawAsync(@"
    UPDATE ScrapeRuns
    SET ListingsProcessed = ListingsProcessed + 1,
        Status = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                      AND Status = 'Running' THEN 'Completed' ELSE Status END,
        CompletedUtc = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                            AND Status = 'Running' THEN datetime('now') ELSE CompletedUtc END
    WHERE Id = {0}", int.Parse(input.JobId));
```

**Important:** Blob triggers fire independently of `ScrapeRun.Status`. The status is a UI indicator only - ETL processes all blobs regardless.

---

## UI Changes

### Modify History view in `app.js`

**1. Faster polling interval:**

Change from 5000ms to 2000ms when jobs are running:

```javascript
// In refreshHistory or mounted
const hasRunning = this.history.some(r => r.status === 'Running');
const interval = hasRunning ? 2000 : 5000;
```

**2. Enhanced progress bar display:**

```html
<div class="progress" style="height: 20px;">
  <div class="progress-bar"
       :class="getProgressClass(run)"
       :style="{ width: getProgressPercent(run) + '%' }">
    {{ run.listingsProcessed }}/{{ run.totalListingsFound }} ({{ getProgressPercent(run) }}%)
  </div>
</div>
```

**3. Progress calculation method:**

```javascript
getProgressPercent(run) {
  if (!run.totalListingsFound || run.totalListingsFound === 0) return 0;
  return Math.round((run.listingsProcessed / run.totalListingsFound) * 100);
}
```

---

## Blob Path Convention

Use `ScrapeRun.Id` as the scraper job ID:

```
html/{scrapeRunId}/{listingId}/listing.html
html/{scrapeRunId}/{listingId}/description.html
```

This allows ETL to parse `ScrapeRunId` directly from the blob path for progress updates.

---

## Summary of Changes

| Component | File | Change |
|-----------|------|--------|
| API | `ScrapeJobsApi.cs` | Add `POST /api/scrape/start` endpoint |
| ETL | `ListingEtlInput.cs` | JobId is ScrapeRunId (no model change needed) |
| ETL | `ProcessListingActivity.cs` | Add atomic progress increment after save |
| UI | `app.js` | Faster polling (2s), enhanced progress bar display |

---

## Design Decisions

1. **Enhanced polling over WebSocket/SSE** - Cheapest option, near-zero cost on Azure Functions consumption plan
2. **Direct DB update from ETL** - Simpler than calling API, ETL already has DB access
3. **TotalListingsFound = post-filter count** - Reflects actual work to be done, not raw search results
4. **ScrapeRunId as scraper jobId** - Natural mapping, no extra lookup needed
5. **Soft completion detection** - Status is UI indicator only, doesn't gate ETL processing
