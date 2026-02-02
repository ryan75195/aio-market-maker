# Design: Remove Durable Functions from AIOMarketMaker

**Date:** 2026-01-31
**Status:** Approved
**Author:** Claude + Ryan

## Problem Statement

The current architecture using Durable Functions + Azurite has a >50% failure rate in local development:

1. **Stuck orchestrations** - Activities scheduled but never executed by Durable Functions runtime
2. **Blob triggers not firing** - Workers complete but ScrapeRunListings stay Pending
3. **Split brain state** - Orchestration state in Azure Tables, business data in SQL Server

These issues make local development nearly unusable and require constant manual intervention.

## Decision

Remove Durable Functions entirely. Replace with a simpler queue-based architecture that works identically in local and Azure environments.

## Architecture Overview

### Current Flow (Durable Functions)

```
1. NightlyScrapeTrigger creates ScrapeRun
2. JobOrchestrator (Durable) starts
3. ScrapeUrlOrchestrator (Durable) scrapes search pages
   → SubmitScrapeJobActivity
   → Loop: CreateTimer → CheckScrapeJobStatusActivity
   → GetScrapedHtmlActivity
4. JobOrchestrator parses listings, creates ScrapeRunListings
5. For each listing URL:
   → Enqueue to scrape-work queue
   → Worker scrapes, saves blob
   → Blob trigger fires ListingEtlOrchestrator (Durable)
6. SweepOrchestrator (Durable) monitors completion
```

### New Flow (Queue-Based)

```
1. Trigger creates ScrapeRun (Status = "Searching")

2. Search Phase (synchronous, in-process)
   → Call WebscraperClient.GetPageHtmlAsync() directly
   → Parse listing URLs
   → Insert ScrapeRunListings (Status = "Pending")
   → Update ScrapeRun (Status = "Indexing")
   → Enqueue all listing URLs

3. Workers process queue (unchanged)
   → Scrape listing page
   → Save HTML to blob
   → POST to processing endpoint

4. Processing Endpoint (replaces blob trigger)
   → Read HTML from blob
   → Parse listing data
   → Save to Listings table
   → Update ScrapeRunListings (Status = "Complete")
   → Increment ScrapeRun.ListingsProcessed

5. Completion Check (simple timer, every 30s)
   → Query for runs where ListingsProcessed >= TotalListingsFound
   → Update Status = "Completed"
```

## Components to Change

### Delete (Durable Functions)

- `Orchestrators/JobOrchestrator.cs`
- `Orchestrators/ScrapeUrlOrchestrator.cs`
- `Orchestrators/ListingEtlOrchestrator.cs`
- `Orchestrators/SweepOrchestrator.cs`
- `Activities/SubmitScrapeJobActivity.cs`
- `Activities/CheckScrapeJobStatusActivity.cs`
- `Activities/GetScrapedHtmlActivity.cs`
- Related activity classes

### Modify

- `Triggers/NightlyScrapeTrigger.cs` - Replace orchestrator start with synchronous search
- `Triggers/ManualScrapeTrigger.cs` - Same
- Worker code - Add HTTP callback after blob save

### Add

- `Endpoints/ProcessListingEndpoint.cs` - HTTP endpoint for listing processing
- `Triggers/CompletionCheckTrigger.cs` - Timer that checks for completed runs

### Keep Unchanged

- `WebscraperClient.cs` - Already has `GetPageHtmlAsync()` with synchronous polling
- All parsers (`EbaySearchParser`, `EbayListingParser`, etc.)
- Database schema and EF Core models
- History API and all UI-facing endpoints
- AIOWebScraper (entire project unchanged)

## API Contracts

### Processing Endpoint

```
POST /api/ProcessListing
Content-Type: application/json

{
  "scrapeRunId": 19075,
  "scrapeRunListingId": 123456,
  "blobPath": "19075/listing-abc123.html"
}

Response: 200 OK
{
  "success": true,
  "listingId": 789,
  "status": "added" | "skipped" | "failed"
}
```

### Worker Callback

Workers will POST to this endpoint after saving blob. Message format in queue:

```json
{
  "scrapeRunId": 19075,
  "scrapeRunListingId": 123456,
  "url": "https://www.ebay.co.uk/itm/123456",
  "groupId": "19075",
  "fileKey": "listing-abc123.html"
}
```

## Tradeoffs

### What We Gain

| Benefit | Description |
|---------|-------------|
| Reliability | No Azurite queue/timer bugs, no stuck orchestrations |
| Simplicity | One code path for local and Azure |
| Transparency | All state in SQL Server |
| Speed | No orchestration replay overhead |
| Cost | ~15x cheaper in Azure (fewer executions) |

### What We Lose

| Loss | Mitigation |
|------|------------|
| Automatic retry | Built into worker + endpoint idempotency |
| Orchestration history | ScrapeRunListings provides same visibility |
| Built-in monitoring | History API + App Insights |
| Fan-out/fan-in pattern | Queue + completion check achieves same |

## Failure Handling

### Worker crashes after scraping, before POST

Queue message becomes visible after timeout. Another worker picks it up, re-scrapes, POSTs. Endpoint is idempotent.

### Processing endpoint crashes mid-save

Worker retries POST. Endpoint checks if listing exists before insert.

### Completion check timer misses

Next timer firing (30s later) catches it. No state lost.

## Cost Comparison (1,000 runs/month)

| | Current (Durable) | New (Queue-based) |
|---|-------------------|-------------------|
| Executions | 12.2M | 811K |
| Storage ops | 75M | 4M |
| Monthly cost | ~$5.44 | ~$0.56 |

## Migration Strategy

1. Implement new components alongside existing
2. Add feature flag to switch between architectures
3. Test new architecture locally until stable
4. Deploy to Azure with flag off
5. Enable flag in Azure, monitor
6. Remove old Durable Functions code

## Testing Strategy

- Unit tests for new trigger and endpoint logic
- Integration tests for full flow (search → queue → process → complete)
- E2E tests with real scraping (subset of URLs)
- Load test with 1000+ listings to verify scalability

## Success Criteria

- Local development failure rate < 5%
- Same or faster throughput as current architecture
- All existing UI functionality preserved
- Clean deployment to Azure with no downtime

## Open Questions

None - design approved for implementation.
