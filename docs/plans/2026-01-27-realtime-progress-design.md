# Real-Time Progress Updates Design

**Goal:** Add real-time progress tracking to the History view showing scrape job completion percentage.

**Approach:** Enhanced polling (2-second interval) with progress bar showing `ListingsProcessed / TotalListingsFound`.

---

## Data Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                      ETL (Durable Functions)                     │
├─────────────────────────────────────────────────────────────────┤
│ 1. POST /api/scrape/start (HTTP Trigger)                         │
│    → Creates ScrapeRun record (Status = "Running")               │
│    → Starts ScrapeOrchestrator with runId                        │
│    → Returns { runId } immediately                               │
├─────────────────────────────────────────────────────────────────┤
│ 2. ScrapeOrchestrator (Durable Orchestration)                    │
│    → For each enabled ScrapeJob:                                 │
│      → Search eBay, parse listing IDs                            │
│      → Filter out existing listings                              │
│      → Update ScrapeRun.TotalListingsFound                       │
│      → Submit URLs to scraper with GroupId/FileKey               │
│    → Orchestration completes (blob triggers take over)           │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                         API (Functions)                          │
├─────────────────────────────────────────────────────────────────┤
│ GET /api/history                                                 │
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

## ETL Changes

### `POST /api/scrape/start` (New HTTP Trigger)

HTTP trigger that starts the scrape orchestration:

1. Create ScrapeRun record with `Status = "Running"`
2. Start `ScrapeOrchestrator` with `runId`
3. Return `{ runId }` immediately (async)

### `ScrapeOrchestrator` (New Durable Orchestration)

Long-running orchestration that handles the full scrape workflow:

1. Get enabled ScrapeJobs from DB
2. For each job:
   - Call `SearchEbayActivity` to search and parse listing IDs
   - Call `FilterListingsActivity` to remove duplicates
3. Update `ScrapeRun.TotalListingsFound` = total filtered count
4. For each new listing ID:
   - Build listing URL + description URL
   - Call `SubmitUrlsActivity` to submit to scraper with `GroupId=listingId`
5. Orchestration completes (blob triggers handle individual processing)

### Activities to Add

- `SearchEbayActivity` - Search eBay, return listing IDs
- `FilterListingsActivity` - Filter out existing listings
- `SubmitUrlsActivity` - Submit URLs to WebscraperClient
- `UpdateScrapeRunActivity` - Update TotalListingsFound/status

## API Changes

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

### Add ETL URL to config

Add `etlApi` section to `config.json`:

```json
{
  "marketMakerApi": { "baseUrl": "...", "functionKey": "..." },
  "etlApi": { "baseUrl": "http://localhost:7072/api" },
  ...
}
```

### Modify `app.js`

**1. Add ETL API call method:**

```javascript
async etlApiCall(endpoint, options = {}) {
  const baseUrl = this.config.etlApi?.baseUrl || 'http://localhost:7072/api';
  // Similar to apiCall but for ETL endpoints
}
```

**2. Update `startScrape()` to call ETL:**

```javascript
async startScrape() {
  const data = await this.etlApiCall('/scrape/start', { method: 'POST', body: ... });
  // ...
}
```

**3. Faster polling interval:**

Change from 5000ms to 2000ms when jobs are running:

```javascript
const interval = hasRunning ? 2000 : 5000;
```

**4. Progress bar already works** - `progressPercent(run)` method exists

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
| ETL | `Triggers/StartScrapeTrigger.cs` | New HTTP trigger for `/api/scrape/start` |
| ETL | `Orchestrators/ScrapeOrchestrator.cs` | New orchestration for search/filter/submit workflow |
| ETL | `Activities/SearchEbayActivity.cs` | Search eBay and parse listing IDs |
| ETL | `Activities/FilterListingsActivity.cs` | Filter out existing listings |
| ETL | `Activities/SubmitUrlsActivity.cs` | Submit URLs to scraper |
| ETL | `Activities/UpdateScrapeRunActivity.cs` | Update TotalListingsFound/status |
| ETL | `ProcessListingActivity.cs` | Add atomic progress increment after save |
| UI | `config.json` | Add `etlApi.baseUrl` config |
| UI | `app.js` | Add `etlApiCall()`, update `startScrape()`, faster polling (2s) |

---

## Design Decisions

1. **Enhanced polling over WebSocket/SSE** - Cheapest option, near-zero cost on Azure Functions consumption plan
2. **Direct DB update from ETL** - Simpler than calling API, ETL already has DB access
3. **TotalListingsFound = post-filter count** - Reflects actual work to be done, not raw search results
4. **ScrapeRunId as scraper jobId** - Natural mapping, no extra lookup needed
5. **Soft completion detection** - Status is UI indicator only, doesn't gate ETL processing
