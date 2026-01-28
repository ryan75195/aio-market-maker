# ETL Flow: UI to Listing Index

This document describes the event-driven ETL pipeline from trigger to indexed listing.

## Entry Points

| Trigger | Endpoint/Schedule | Description |
|---------|-------------------|-------------|
| **Manual HTTP** | `POST /api/scrape/start` | User-initiated via API call |
| **Nightly Timer** | `0 0 2 * * *` (2 AM UTC) | Automated daily scrape |

### Manual Trigger Request

```http
POST /api/scrape/start
Content-Type: application/json

{
  "maxListingsToFetch": 100,    // Optional: limit for testing
  "lookbackDays": 7             // Optional: sold listings lookback
}
```

**Response:**
```json
{
  "runId": 42,
  "instanceId": "scrape-run-42"
}
```

---

## Flow Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              ENTRY POINTS                                    │
│                                                                             │
│   POST /api/scrape/start          TimerTrigger (2 AM UTC)                   │
│   [StartScrapeTrigger]            [NightlyScrapeTrigger]                    │
└─────────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         ScrapeOrchestrator                                   │
│  - Creates ScrapeRun record (Status: Running)                               │
│  - Gets active ScrapeJobs from DB                                           │
│  - Calls JobOrchestrator for each job                                       │
└─────────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                          JobOrchestrator                                     │
│                                                                             │
│  SEARCH PHASE:                                                              │
│  1. Search sold listings (page by page via ScrapeUrlOrchestrator)           │
│  2. Detect Active→Sold transitions, re-scrape for accurate price/date       │
│  3. Search active listings (page by page)                                   │
│                                                                             │
│  FILTER PHASE:                                                              │
│  4. FilterNewListingsActivity - remove listings already in DB               │
│                                                                             │
│  SUBMIT PHASE (Fire-and-Forget):                                            │
│  5. InsertScrapeRunListingsActivity - populate junction table               │
│  6. SubmitScrapeJobsActivity - submit all scrape jobs to queue              │
│  7. Set phase to "Scraping" and EXIT (no waiting!)                          │
└─────────────────────────────────────────────────────────────────────────────┘
                                     │
                    ┌────────────────┴────────────────┐
                    ▼                                 ▼
┌──────────────────────────────────┐    ┌──────────────────────────────────┐
│   Scraper Queue (Listing)        │    │  Scraper Queue (Description)     │
│   URL: ebay.com/itm/{listingId}  │    │  URL: vi.vipr.ebaydesc.com/...   │
│   groupId: {listingId}           │    │  groupId: {listingId}            │
│   fileKey: "listing"             │    │  fileKey: "description"          │
└──────────────────────────────────┘    └──────────────────────────────────┘
                    │                                 │
                    ▼                                 ▼
┌──────────────────────────────────┐    ┌──────────────────────────────────┐
│   AIOWebScraper Worker           │    │   AIOWebScraper Worker           │
│   (Playwright browser)           │    │   (Playwright browser)           │
│   - Stealth evasions             │    │   - Stealth evasions             │
│   - Proxy rotation               │    │   - Proxy rotation               │
└──────────────────────────────────┘    └──────────────────────────────────┘
                    │                                 │
                    ▼                                 ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Azure Blob Storage                                   │
│                                                                             │
│  html/{listingId}/listing.html       html/{listingId}/description.html      │
│       │                                      │                              │
│       ▼                                      ▼                              │
│  ┌─────────────────────┐              ┌─────────────────────┐               │
│  │ ListingBlobTrigger  │              │ DescriptionBlobTrigger              │
│  │ (fires on upload)   │              │ (fires on upload)   │               │
│  └─────────────────────┘              └─────────────────────┘               │
└─────────────────────────────────────────────────────────────────────────────┘
                    │                                 │
                    └────────────┬────────────────────┘
                                 ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                      ListingEtlOrchestrator                                  │
│  Instance ID: etl-{listingId}                                               │
│                                                                             │
│  1. LookupScrapeRunActivity(listingId)                                      │
│     └─► SELECT ScrapeRunId, ScrapeJobId FROM ScrapeRunListings              │
│         WHERE ListingId = ? AND Status = 'Pending'                          │
│     └─► If not found, skip (orphan blob)                                    │
│                                                                             │
│  2. CheckBlobsActivity(listingId)                                           │
│     └─► Check if both listing.html and description.html exist               │
│                                                                             │
│  3. Wait for partner blob (if only one arrived)                             │
│     └─► 5-minute timeout with external event                                │
│     └─► Proceeds with what's available after timeout                        │
│                                                                             │
│  4. ProcessListingActivity(listingId, scrapeJobId, hasDescription)          │
│     └─► Parse listing HTML (AngleSharp)                                     │
│     └─► Parse description HTML if available                                 │
│     └─► UPSERT to Listings table                                            │
│                                                                             │
│  5. UpdateScrapeRunListingActivity(scrapeRunId, listingId, "Complete")      │
│     └─► UPDATE ScrapeRunListings SET Status='Complete', CompletedUtc=NOW()  │
│     └─► UPDATE ScrapeRuns SET ListingsProcessed += 1                        │
│     └─► Auto-complete run when ListingsProcessed >= TotalListingsFound      │
└─────────────────────────────────────────────────────────────────────────────┘
                                 │
                                 ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                           SQLite Database                                    │
│                                                                             │
│  ┌─────────────────┐    ┌─────────────────┐    ┌──────────────────────┐    │
│  │   ScrapeRuns    │    │   ScrapeJobs    │    │  ScrapeRunListings   │    │
│  │  (run progress) │    │   (job config)  │    │  (junction table)    │    │
│  │                 │    │                 │    │                      │    │
│  │  - Id           │    │  - Id           │    │  - ScrapeRunId (PK)  │    │
│  │  - InstanceId   │    │  - SearchTerm   │    │  - ListingId (PK)    │    │
│  │  - TriggerType  │    │  - IsActive     │    │  - ScrapeJobId       │    │
│  │  - Status       │◄───│  - LastRunUtc   │◄───│  - Status            │    │
│  │  - CurrentPhase │    │                 │    │  - CreatedUtc        │    │
│  │  - TotalFound   │    │                 │    │  - CompletedUtc      │    │
│  │  - Processed    │    │                 │    │                      │    │
│  └─────────────────┘    └─────────────────┘    └──────────────────────┘    │
│                                │                         │                  │
│                                ▼                         ▼                  │
│                        ┌───────────────────────────────────────┐            │
│                        │              Listings                 │            │
│                        │                                       │            │
│                        │  - ListingId (PK)                     │            │
│                        │  - Title, Price, Currency             │            │
│                        │  - Condition, ShippingCost            │            │
│                        │  - Description                        │            │
│                        │  - Images (JSON array)                │            │
│                        │  - ItemSpecifics (JSON)               │            │
│                        │  - ListingStatus (Active/Sold)        │            │
│                        │  - ScrapeJobId (FK)                   │            │
│                        │  - EndDateUtc, Location               │            │
│                        │  - CreatedUtc, UpdatedUtc             │            │
│                        └───────────────────────────────────────┘            │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Processing Characteristics

| Stage | Component | Parallelism | Notes |
|-------|-----------|-------------|-------|
| **Trigger** | StartScrapeTrigger / NightlyScrapeTrigger | Single | Creates ScrapeRun, starts orchestrator |
| **Search** | JobOrchestrator → ScrapeUrlOrchestrator | Sequential pages | Page-by-page to respect rate limits |
| **Filter** | FilterNewListingsActivity | Single | Bulk DB query |
| **Submit** | SubmitScrapeJobsActivity | **Fire-and-forget** | All jobs submitted at once |
| **Scrape** | AIOWebScraper workers | **Parallel** | Limited by worker pool size |
| **Trigger** | Blob triggers | **Parallel** | Independent per listing |
| **Process** | ListingEtlOrchestrator | **Parallel** | One instance per listing |
| **Track** | UpdateScrapeRunListingActivity | Atomic | Thread-safe progress updates |

---

## Progress Monitoring

### Query Current Run Status

```sql
SELECT
    Id,
    InstanceId,
    TriggerType,
    Status,
    CurrentPhase,
    TotalListingsFound,
    ListingsProcessed,
    StartedUtc,
    CompletedUtc
FROM ScrapeRuns
ORDER BY Id DESC
LIMIT 1;
```

### Query Per-Listing Progress

```sql
SELECT
    ListingId,
    Status,
    CreatedUtc,
    CompletedUtc,
    ROUND((julianday(CompletedUtc) - julianday(CreatedUtc)) * 86400, 1) AS ProcessingSeconds
FROM ScrapeRunListings
WHERE ScrapeRunId = @runId
ORDER BY CompletedUtc DESC;
```

### Progress Summary

```sql
SELECT
    Status,
    COUNT(*) AS Count
FROM ScrapeRunListings
WHERE ScrapeRunId = @runId
GROUP BY Status;
```

---

## Key Design Decisions

1. **Fire-and-forget submission**: JobOrchestrator doesn't wait for scrapes to complete. It inserts junction table entries and submits jobs, then exits.

2. **Blob triggers for event-driven processing**: Each listing is processed independently when its HTML arrives, enabling true parallelism.

3. **Junction table for progress**: `ScrapeRunListings` links listings to runs without polluting the clean `Listings` table.

4. **Simplified blob paths**: `html/{listingId}/listing.html` instead of `html/{jobId}/{listingId}/listing.html` - the jobId is an implementation detail.

5. **Parallel listing + description scraping**: Both are submitted simultaneously since we can construct the description URL without parsing the listing first.

6. **5-minute timeout for partner blobs**: Orchestrator waits for both listing and description, but proceeds with what's available after timeout.

---

## Related Files

- `AIOMarketMaker.Etl/Triggers/StartScrapeTrigger.cs` - Manual HTTP trigger
- `AIOMarketMaker.Etl/Triggers/NightlyScrapeTrigger.cs` - Timer trigger
- `AIOMarketMaker.Etl/Triggers/ListingBlobTrigger.cs` - Blob trigger for listings
- `AIOMarketMaker.Etl/Triggers/DescriptionBlobTrigger.cs` - Blob trigger for descriptions
- `AIOMarketMaker.Etl/Orchestrators/ScrapeOrchestrator.cs` - Main orchestrator
- `AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs` - Per-job orchestrator
- `AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs` - Per-listing processor
- `AIOMarketMaker.Core/Data/Migrations/022_CreateScrapeRunListingsTable.sql` - Junction table
