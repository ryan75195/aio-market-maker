# Event-Driven ETL Simplification Design

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:writing-plans to create implementation plan from this design.

**Goal:** Replace batch-50 orchestrator polling with fire-and-forget event-driven processing using simplified blob paths and a junction table for progress tracking.

**Architecture:** JobOrchestrator submits all scrape jobs without waiting. Blob triggers fire independently for each listing. ListingEtlOrchestrator processes listings and updates progress via junction table lookup.

---

## Current State (Problems)

1. **Batch-50 bottleneck**: JobOrchestrator processes 50 listings at a time, waiting for each batch
2. **Sequential listing + description**: Must scrape listing before description (to get description URL)
3. **Complex blob paths**: `html/{scraperJobId}/{groupId}/{fileKey}.html` - scraperJobId is meaningless to ETL
4. **No link for progress**: ListingEtlOrchestrator doesn't know which ScrapeRun to update

## New Design

### 1. Simplified Blob Path

**Before:**
```
html/{scraperJobId}/{listingId}/listing.html
html/{scraperJobId}/{listingId}/description.html
```

**After:**
```
html/{listingId}/listing.html
html/{listingId}/description.html
```

- Remove scraperJobId from path (implementation detail of AIOWebScraper)
- ListingId is the only identifier needed
- Re-scraping overwrites (HTML is transient - parse, save, done)

### 2. Junction Table: `ScrapeRunListings`

```sql
CREATE TABLE IF NOT EXISTS ScrapeRunListings (
    ScrapeRunId INT NOT NULL,
    ScrapeJobId INT NOT NULL,
    ListingId VARCHAR(20) NOT NULL,
    Status TEXT DEFAULT 'Pending',  -- Pending, Processing, Complete, Failed
    CreatedUtc DATETIME DEFAULT CURRENT_TIMESTAMP,
    CompletedUtc DATETIME NULL,
    PRIMARY KEY (ScrapeRunId, ListingId),
    FOREIGN KEY (ScrapeRunId) REFERENCES ScrapeRuns(Id),
    FOREIGN KEY (ScrapeJobId) REFERENCES ScrapeJobs(Id)
);

CREATE INDEX IF NOT EXISTS IX_ScrapeRunListings_ListingId ON ScrapeRunListings(ListingId);
CREATE INDEX IF NOT EXISTS IX_ScrapeRunListings_Status ON ScrapeRunListings(ScrapeRunId, Status);
```

**Purpose:**
- Track which listings belong to which ScrapeRun
- Enable progress lookup from ListingEtlOrchestrator
- Provide analytics (listings per run, processing status)

### 3. Fire-and-Forget Flow

```
JobOrchestrator (ScrapeRunId=42, ScrapeJobId=1):
  │
  │  For each listingId discovered in search:
  │  ├─ INSERT INTO ScrapeRunListings (42, 1, listingId, 'Pending')
  │  ├─ SubmitScrapeJob(listingUrl, groupId=null, fileKey=listingId)
  │  └─ SubmitScrapeJob(descriptionUrl, groupId=null, fileKey=listingId)
  │
  │  SET ScrapeRuns.TotalListingsFound = count
  │  SET ScrapeRuns.CurrentPhase = 'Scraping'
  └─ DONE (no waiting)

Scraper (AIOWebScraper):
  │
  │  Processes queue at its own pace
  │  Saves blobs to: html/{listingId}/listing.html
  │                  html/{listingId}/description.html
  └─ (blob triggers fire)

ListingBlobTrigger / DescriptionBlobTrigger:
  │
  └─► ListingEtlOrchestrator(listingId)
        │
        ├─ Wait for both blobs (5 min timeout)
        ├─ SELECT ScrapeRunId, ScrapeJobId FROM ScrapeRunListings WHERE ListingId = ?
        ├─ Process listing + description
        ├─ UPSERT Listings (with ScrapeJobId)
        ├─ UPDATE ScrapeRunListings SET Status='Complete', CompletedUtc=NOW()
        └─ UPDATE ScrapeRuns SET ListingsProcessed = ListingsProcessed + 1
```

### 4. Parallel Listing + Description

Since we know the description URL pattern (via `BuildDescriptionUrl`), we can submit both scrape jobs at once:

```csharp
// In JobOrchestrator - submit BOTH in parallel
var listingUrl = _urlBuilder.BuildListingUrl(listingId);
var descriptionUrl = _urlBuilder.BuildDescriptionUrl(listingId);

await SubmitScrapeJob(listingUrl, fileKey: $"{listingId}-listing");
await SubmitScrapeJob(descriptionUrl, fileKey: $"{listingId}-description");
```

**Blob paths:**
```
html/{listingId}-listing.html
html/{listingId}-description.html
```

Or keep nested structure:
```
html/{listingId}/listing.html
html/{listingId}/description.html
```

### 5. Progress Tracking

**ScrapeRun fields (already exist):**
- `TotalListingsFound` - Set by JobOrchestrator after search
- `ListingsProcessed` - Incremented by ListingEtlOrchestrator
- `CurrentPhase` - "Searching", "Scraping", "Complete"
- `Status` - "Running", "Completed", "Failed"

**Completion detection:**
- When `ListingsProcessed == TotalListingsFound`, mark run as Complete
- Or: Query `SELECT COUNT(*) FROM ScrapeRunListings WHERE Status='Pending'`

### 6. Blob Trigger Patterns

**Update triggers to match simplified paths:**

```csharp
// ListingBlobTrigger.cs
[BlobTrigger("html/{listingId}/listing.html", Connection = "blobStorageConnectionString")]

// DescriptionBlobTrigger.cs
[BlobTrigger("html/{listingId}/description.html", Connection = "blobStorageConnectionString")]
```

---

## Changes Required

### AIOWebScraper (Scraper API)

1. **BlobPathBuilder** - Support path without scraperJobId prefix
   - Add option: `useSimplePath: true` → `{fileKey}.html` or `{groupId}/{fileKey}.html`
   - Or: Always use `{groupId}/{fileKey}.html` when provided, ignore scraperJobId

### AIOMarketMaker.Etl

1. **Migration** - Create `ScrapeRunListings` table

2. **JobOrchestrator** - Fire-and-forget scrape submission
   - Insert to ScrapeRunListings before submitting
   - Submit listing + description in parallel
   - Don't wait for results
   - Set TotalListingsFound and exit

3. **ListingEtlOrchestrator** - Lookup and update progress
   - Query ScrapeRunListings for ScrapeRunId/ScrapeJobId
   - Update status to Complete after processing
   - Increment ScrapeRuns.ListingsProcessed

4. **Blob Triggers** - Update path patterns

5. **Delete/Archive** - Remove batch-50 code from JobOrchestrator
   - FetchListingOrchestrator no longer needed for ETL

---

## Benefits

| Aspect | Before (Batch-50) | After (Event-Driven) |
|--------|-------------------|----------------------|
| Listing + Description | Sequential | Parallel |
| Concurrency | 50 at a time | Unlimited (scraper throttles) |
| Blob path | Complex (scraperJobId) | Simple (listingId only) |
| Progress tracking | Orchestrator counts | Junction table lookup |
| Failure isolation | Batch fails together | Each listing independent |
| Code complexity | High (polling, batching) | Low (fire-and-forget) |

---

## Testing Strategy

1. **Unit tests** for ScrapeRunListings repository methods
2. **Integration test** with Azurite: Submit job → verify blobs created → verify trigger fires
3. **Progress test**: Verify ListingsProcessed increments correctly

---

## Open Questions (Resolved)

- ~~How to link ListingEtlOrchestrator to ScrapeRun?~~ → Junction table
- ~~Need scraperJobId in path?~~ → No, it's transient
- ~~Need new table?~~ → Yes, ScrapeRunListings junction table
