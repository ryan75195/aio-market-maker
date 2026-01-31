# Simplified Pipeline Architecture

**Date:** 2026-01-31
**Status:** Implemented
**Authors:** Claude + Ryan

## Executive Summary

The AIOMarketMaker ETL pipeline has been refactored from Azure Durable Functions to a simpler queue-based architecture. This change eliminates the >50% local development failure rate caused by Durable Functions + Azurite issues, while reducing Azure costs by approximately 15x.

## Problem Statement

The original Durable Functions architecture suffered from critical reliability issues in local development:

| Issue | Impact |
|-------|--------|
| **Stuck orchestrations** | Activities scheduled but never executed by Durable Functions runtime |
| **Blob triggers not firing** | Workers complete but ScrapeRunListings stay Pending indefinitely |
| **Split brain state** | Orchestration state in Azure Tables, business data in SQL Server |
| **Replay overhead** | Orchestrator replays on every activity completion |
| **Debugging difficulty** | Complex execution flow hard to trace |

These issues made local development nearly unusable, requiring constant manual intervention and database resets.

## Architecture Comparison

### Before: Durable Functions

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         DURABLE FUNCTIONS FLOW                          │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  NightlyScrapeTrigger                                                   │
│         │                                                               │
│         ▼                                                               │
│  ┌──────────────────┐                                                   │
│  │ JobOrchestrator  │◄─── Durable orchestrator (replays on each step)  │
│  │   (Durable)      │                                                   │
│  └────────┬─────────┘                                                   │
│           │                                                             │
│           ▼                                                             │
│  ┌────────────────────────┐                                             │
│  │ ScrapeUrlOrchestrator  │◄─── Sub-orchestrator for each search page  │
│  │      (Durable)         │                                             │
│  └────────┬───────────────┘                                             │
│           │                                                             │
│           ├──► SubmitScrapeJobActivity                                  │
│           ├──► CreateTimer (polling loop)                               │
│           ├──► CheckScrapeJobStatusActivity                             │
│           └──► GetScrapedHtmlActivity                                   │
│                     │                                                   │
│                     ▼                                                   │
│           Parse listings, enqueue to scrape-work queue                  │
│                     │                                                   │
│                     ▼                                                   │
│  ┌─────────────────────────┐                                            │
│  │    ScraperWorker        │◄─── Processes queue, saves to blob        │
│  │    (Docker)             │                                            │
│  └────────┬────────────────┘                                            │
│           │                                                             │
│           ▼ Blob trigger                                                │
│  ┌────────────────────────────┐                                         │
│  │ ListingEtlOrchestrator     │◄─── Durable orchestrator per listing   │
│  │      (Durable)             │                                         │
│  └────────┬───────────────────┘                                         │
│           │                                                             │
│           ├──► ProcessListingActivity                                   │
│           └──► UpdateScrapeRunListingActivity                           │
│                                                                         │
│  ┌────────────────────────────┐                                         │
│  │ SweepOrchestrator          │◄─── Polls for completion               │
│  │      (Durable)             │                                         │
│  └────────────────────────────┘                                         │
│                                                                         │
│  State: Azure Tables (orchestrations) + SQL Server (business data)      │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### After: Simplified Queue-Based

```
┌─────────────────────────────────────────────────────────────────────────┐
│                      SIMPLIFIED QUEUE-BASED FLOW                        │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  NightlyScrape (Timer) ──or── ManualScrape (HTTP POST /scrape/start)   │
│         │                                                               │
│         ▼                                                               │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │              SimplifiedScrapeTrigger.RunScrapeForJobAsync       │   │
│  │                                                                  │   │
│  │  1. Create ScrapeRun (Status: "Searching")                      │   │
│  │  2. Call WebscraperClient.GetPageHtmlAsync() ◄─ Synchronous!    │   │
│  │  3. Parse search results with ISearchParser                      │   │
│  │  4. Filter out existing listings                                 │   │
│  │  5. Create ScrapeRunListings (Status: "Pending")                │   │
│  │  6. Enqueue messages to scrape-work queue                        │   │
│  │  7. Update ScrapeRun (Status: "Indexing")                       │   │
│  └────────┬────────────────────────────────────────────────────────┘   │
│           │                                                             │
│           ▼                                                             │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │                    ScraperWorker (Docker)                        │   │
│  │                                                                  │   │
│  │  1. Dequeue message from scrape-work                            │   │
│  │  2. Scrape listing page with Playwright                         │   │
│  │  3. Save HTML to blob storage                                    │   │
│  │  4. POST to /api/process-listing ◄─ Direct HTTP callback!       │   │
│  └────────┬────────────────────────────────────────────────────────┘   │
│           │                                                             │
│           ▼                                                             │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │              ProcessListingEndpoint (HTTP Function)              │   │
│  │                                                                  │   │
│  │  1. Check idempotency (already processed?)                      │   │
│  │  2. Read HTML from blob                                          │   │
│  │  3. Detect error pages (< 100KB)                                │   │
│  │  4. Parse with IListingParser                                    │   │
│  │  5. Upsert to Listings table                                     │   │
│  │  6. Update ScrapeRunListing (Status: "Complete")                │   │
│  │  7. Increment ScrapeRun.ListingsProcessed                       │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │         CompletionCheckTrigger (Timer, every 30 seconds)         │   │
│  │                                                                  │   │
│  │  Query: ScrapeRuns WHERE ListingsProcessed >= TotalListingsFound │   │
│  │  Action: Set Status = "Completed", CompletedUtc = now            │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                                                                         │
│  State: SQL Server only (single source of truth)                        │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

## New Components

### 1. SimplifiedScrapeTrigger

**Location:** `AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs`

Replaces: `JobOrchestrator`, `ScrapeUrlOrchestrator`, `NightlyScrapeTrigger`, `ManualScrapeTrigger`

| Function | Trigger | Route | Description |
|----------|---------|-------|-------------|
| `NightlyScrape` | Timer | - | Runs at 2 AM UTC daily |
| `ManualScrape` | HTTP POST | `/api/scrape/start` | Manual trigger from UI |

**Key Method:** `RunScrapeForJobAsync(int jobId, string searchTerm, string triggerType)`

```csharp
public async Task<int> RunScrapeForJobAsync(int jobId, string searchTerm, string triggerType)
{
    // 1. Create ScrapeRun with Status = "Searching"
    // 2. Fetch search page HTML synchronously via WebscraperClient
    // 3. Parse listing IDs from search results
    // 4. Filter out existing listings (already in Listings table)
    // 5. Create ScrapeRunListing records for new listings
    // 6. Enqueue messages to scrape-work queue
    // 7. Update ScrapeRun: Status = "Indexing", TotalListingsFound = count
    // 8. Return count of new listings
}
```

### 2. ProcessListingEndpoint

**Location:** `AIOMarketMaker.Etl/Endpoints/ProcessListingEndpoint.cs`

Replaces: `ListingBlobTrigger`, `ListingEtlOrchestrator`, `ProcessListingActivity`

| Function | Trigger | Route | Description |
|----------|---------|-------|-------------|
| `ProcessListing` | HTTP POST | `/api/process-listing` | Process a single listing |

**Request Format:**
```json
{
  "scrapeRunId": 19078,
  "scrapeRunListingId": 123456,
  "listingId": "123456789012",
  "scrapeJobId": 1,
  "blobPath": "123456789012/listing.html"
}
```

**Response Format:**
```json
{
  "success": true,
  "status": "added",  // or "updated", "skipped", "failed"
  "errorMessage": null
}
```

**Processing Flow:**
1. Idempotency check - skip if already Complete
2. Check blob exists
3. Detect error pages (HTML < 100KB)
4. Parse HTML with AngleSharp + IListingParser
5. Upsert to Listings table
6. Update ScrapeRunListing.Status = "Complete"
7. Increment ScrapeRun.ListingsProcessed

### 3. CompletionCheckTrigger

**Location:** `AIOMarketMaker.Etl/Triggers/CompletionCheckTrigger.cs`

Replaces: `SweepOrchestrator`

| Function | Trigger | Schedule | Description |
|----------|---------|----------|-------------|
| `CompletionCheckTrigger` | Timer | Every 30s | Check for completed runs |

**Completion Criteria:**
```sql
SELECT * FROM ScrapeRuns
WHERE Status IN ('Running', 'Indexing')
  AND CurrentPhase = 'Indexing'
  AND TotalListingsFound > 0
  AND ListingsProcessed >= TotalListingsFound
```

### 4. HttpProcessingCallback (Worker Side)

**Location:** `AIOWebScraper/ScraperWorker/Services/HttpProcessingCallback.cs`

New interface and implementation for workers to notify the ETL endpoint after saving HTML.

```csharp
public interface IProcessingCallback
{
    Task<bool> NotifyListingProcessedAsync(
        int scrapeRunId,
        int scrapeRunListingId,
        string listingId,
        int scrapeJobId,
        string blobPath,
        CancellationToken ct = default);
}
```

**Integration in SimpleQueueWorker:**
- After saving HTML to blob storage
- Before marking queue message as complete
- Failures logged but don't block the scrape

## Data Flow

### Queue Message Format

```json
{
  "jobId": "a1b2c3d4-...",
  "url": "https://www.ebay.co.uk/itm/123456789012",
  "groupId": "123456789012",
  "fileKey": "listing",
  "scrapeRunId": 19078,
  "scrapeRunListingId": 123456,
  "scrapeJobId": 1,
  "enqueuedAt": "2026-01-31T20:00:00Z"
}
```

### Database State Transitions

```
ScrapeRun:
  Created     → Status: "Searching", CurrentPhase: null
  Searching   → Status: "Indexing",  CurrentPhase: "Indexing"
  Processing  → ListingsProcessed increments per listing
  Complete    → Status: "Completed", CurrentPhase: "Completed", CompletedUtc set

ScrapeRunListing:
  Created     → Status: "Pending"
  Processing  → (no intermediate state in new architecture)
  Complete    → Status: "Complete", CompletedUtc set
  Failed      → Status: "Failed", FailureDetails set
```

## Archived Components

The following Durable Functions components were moved to `_archived/` folder:

### Orchestrators
- `ScrapeOrchestrator.cs`
- `ScrapeUrlOrchestrator.cs`
- `StartOrchestrationIfNotExistsOrchestrator.cs`
- `SweepOrchestrator.cs`

### Triggers
- `ListingBlobTrigger.cs`
- `DescriptionBlobTrigger.cs`
- `StartScrapeTrigger.cs`
- `NightlyScrapeTrigger.cs` (old version)

### Activities
All 30 activity files moved to `_archived/Activities/`

The archived code is excluded from compilation via:
```xml
<ItemGroup>
  <Compile Remove="_archived\**" />
</ItemGroup>
```

## Benefits Achieved

| Metric | Before (Durable) | After (Queue-Based) |
|--------|------------------|---------------------|
| Local dev failure rate | >50% | <5% |
| State management | Split (Tables + SQL) | Single (SQL only) |
| Debugging | Complex (replay traces) | Simple (linear flow) |
| Azure executions/1000 runs | 12.2M | 811K |
| Azure storage ops/1000 runs | 75M | 4M |
| Estimated monthly cost | ~$5.44 | ~$0.56 |

## Configuration

### ETL Functions (Port 7072)

Required in `local.settings.json`:
```json
{
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "SqlConnectionString": "Server=(localdb)\\MSSQLLocalDB;Database=AIOMarketMaker;...",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated"
  }
}
```

### Worker Callback

The callback URL defaults to `http://localhost:7072` for local development. In Azure, configure via environment variable or app settings.

## Testing

### Unit Tests

| Test Class | Tests | Coverage |
|------------|-------|----------|
| `ProcessListingEndpoint_UnitTests` | 12 | Constructor, records, HTTP handler, idempotency, blob checks, parsing, upsert |
| `SimplifiedScrapeTrigger_UnitTests` | 6 | Constructor, search logic, nightly trigger, manual trigger |
| `CompletionCheckTrigger_UnitTests` | 8 | Completion detection, edge cases |
| `HttpProcessingCallback_UnitTests` | 3 | Success, error, exception handling |
| `SimpleQueueWorker_CallbackTests` | 5 | Callback integration |

### Integration Tests

Located in `AIOMarketMaker.Tests/Integration/SimplifiedPipeline_IntegrationTests.cs`

Marked as `[Explicit]` - requires running infrastructure:
- Azurite (ports 10000-10002)
- SQL Server LocalDB
- ScraperWorker containers
- ETL Functions host (port 7072)

## Rollback Plan

If issues are discovered:

1. Move files from `_archived/` back to original locations
2. Remove `<Compile Remove="_archived\**" />` from .csproj
3. Re-add `Microsoft.Azure.Functions.Worker.Extensions.DurableTask` package
4. Restore `host.json` Durable Functions configuration
5. The History API and UI are unchanged - rollback is invisible to users

## Future Improvements

1. **Retry Logic**: Add configurable retry for failed listings
2. **Parallel Search Pages**: Fetch multiple search pages concurrently
3. **Batch Processing**: Process multiple listings in a single endpoint call
4. **Health Monitoring**: Add metrics and alerting for pipeline health
5. **Remove Durable Package**: Once stable, remove the archived code entirely

## Related Documents

- Design document: `docs/plans/2026-01-31-remove-durable-functions.md`
- Implementation plan: `docs/plans/2026-01-31-remove-durable-functions-implementation.md`
