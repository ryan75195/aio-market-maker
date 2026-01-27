# Event-Driven ETL Pipeline Design

## Overview

Replace the polling-based batch processing pipeline with an event-driven per-listing architecture. Each listing flows through an independent pipeline triggered by blob storage events, enabling real-time processing as scrapes complete.

## Problem Statement

Current pipeline bottlenecks:
- **Batch synchronization**: Must wait for entire batch (50+ listings) before processing any
- **Sequential description fetch**: Listings scraped first, then descriptions (adds ~5s per listing)
- **Polling overhead**: 5-10 second intervals waste time and resources
- **Time to first indexed listing**: 15+ minutes (after full batch completes)

## Architecture

### High-Level Flow

```
Search Results (listing IDs)
       |
       v
Submit to Scraper: 2 URLs per listing
  - Listing page: ebay.com/itm/{id}
  - Description page: itm.ebaydesc.com/itmdesc/{id}?...
       |
       v
Scraper saves to Blob Storage:
  html/{jobId}/{listingId}/listing.html
  html/{jobId}/{listingId}/description.html
       |
       v
Blob Trigger -> Durable Orchestration (per listing)
  - Wait for both files (or 5-min timeout)
  - Parse listing + description
  - Save to database
  - (Future: embeddings, indexing, etc.)
```

### Project Responsibilities

```
AIOMarketMaker.Functions (HTTP API only)
  - POST /api/StartScrapeJob - trigger new job
  - GET /api/GetJobStatus - check progress
  - GET /api/GetListings - query results
  - Lightweight, fast cold starts
  - No blob triggers, no Durable Functions

AIOMarketMaker.Etl (Azure Functions + Durable Functions)
  - Blob triggers (listing.html, description.html)
  - Durable orchestrations (wait for partner, process listing)
  - All ETL processing logic
  - Background worker - event-driven
  - Timer triggers for scheduled jobs (if needed)

AIOMarketMaker.Core (shared - unchanged)
  - Parsers, services, data layer, domain models
```

## Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Event mechanism | Blob Storage triggers | Azurite compatible, zero additional infrastructure |
| Description fetching | Parallel with listing | URL pattern is predictable from listing ID |
| "Both files ready" detection | First-arrival checks for partner | Simple, blob triggers fire independently |
| Timeout handling | Durable Functions external events | Reactive (immediate when partner arrives), efficient |
| Timeout duration | 5 minutes | Long enough for retries, short enough to not block |
| Blob structure | `html/{jobId}/{groupId}/{fileKey}.html` | Organized by listing, easy cleanup |
| Scraper coupling | Generic GroupId + FileKey | Scraper remains eBay-agnostic |
| Partial failure | Process with what we have | Mark incomplete records for later retry |

## Blob Storage Structure

```
html/
  {jobId}/
    {listingId}/
      listing.html
      description.html
```

Example:
```
html/
  abc123/
    306278488042/
      listing.html
      description.html
    187195374314/
      listing.html
      description.html
```

## Scraper Changes (AIOWebScraper)

### Queue Message Extension

```csharp
public record ScrapeQueueMessage
{
    // Existing fields...
    public string? GroupId { get; init; }   // Caller-defined grouping
    public string? FileKey { get; init; }   // Caller-defined file name
}
```

### Blob Path Logic

```csharp
// If GroupId and FileKey provided:
//   html/{jobId}/{groupId}/{fileKey}.html
// Otherwise (backward compatible):
//   html/{jobId}/{safeUrl}.html
```

### Files to Modify

- `ScrapeQueueMessage.cs` - Add GroupId and FileKey fields
- `AzureJobRepository.cs` - Update SaveContentAsync() for new path structure
- `JobItemProcessor.cs` - Pass GroupId/FileKey to save method

## Market Maker Changes

### New URL Builder Method

```csharp
// EbayUrlBuilder.cs
public string BuildDescriptionUrl(string listingId)
{
    return $"https://itm.ebaydesc.com/itmdesc/{listingId}?t=0&category=139971" +
           "&excSoj=1&ver=0&excTrk=1&lsite=3&ittenable=false" +
           "&domain=ebay.com&descgauge=1&cspheader=1&oneClk=2&secureDesc=1";
}
```

### New Request Model

```csharp
public record ScrapeUrlRequest
{
    public required string Url { get; init; }
    public string? GroupId { get; init; }
    public string? FileKey { get; init; }
}
```

### Building Requests

```csharp
public IEnumerable<ScrapeUrlRequest> BuildListingRequests(IEnumerable<string> listingIds)
{
    foreach (var id in listingIds)
    {
        yield return new ScrapeUrlRequest
        {
            Url = _urlBuilder.BuildListingUrl(id),
            GroupId = id,
            FileKey = "listing"
        };

        yield return new ScrapeUrlRequest
        {
            Url = _urlBuilder.BuildDescriptionUrl(id),
            GroupId = id,
            FileKey = "description"
        };
    }
}
```

## Blob Triggers (AIOMarketMaker.Etl)

### Trigger Implementation

```csharp
[Function("OnListingBlobCreated")]
public async Task RunListing(
    [BlobTrigger("html/{jobId}/{listingId}/listing.html")] string html,
    [DurableClient] IDurableOrchestrationClient client,
    string jobId, string listingId)
{
    var instanceId = $"etl-{jobId}-{listingId}";

    var status = await client.GetStatusAsync(instanceId);
    if (status == null)
    {
        await client.StartNewAsync(
            nameof(ListingEtlOrchestrator),
            instanceId,
            new ListingEtlInput(jobId, listingId, TriggerSource.Listing));
    }
    else
    {
        await client.RaiseEventAsync(instanceId, "listing-ready", true);
    }
}

[Function("OnDescriptionBlobCreated")]
public async Task RunDescription(
    [BlobTrigger("html/{jobId}/{listingId}/description.html")] string html,
    [DurableClient] IDurableOrchestrationClient client,
    string jobId, string listingId)
{
    var instanceId = $"etl-{jobId}-{listingId}";

    var status = await client.GetStatusAsync(instanceId);
    if (status == null)
    {
        await client.StartNewAsync(
            nameof(ListingEtlOrchestrator),
            instanceId,
            new ListingEtlInput(jobId, listingId, TriggerSource.Description));
    }
    else
    {
        await client.RaiseEventAsync(instanceId, "description-ready", true);
    }
}
```

## Durable Orchestration

```csharp
[Function(nameof(ListingEtlOrchestrator))]
public async Task Run([OrchestrationTrigger] TaskOrchestrationContext context)
{
    var input = context.GetInput<ListingEtlInput>();

    // Check what blobs exist
    var state = await context.CallActivityAsync<BlobState>(
        nameof(CheckBlobsActivity), input);

    // Wait for partner if needed
    if (!state.HasBoth)
    {
        var timeout = context.CreateTimer(
            context.CurrentUtcDateTime.AddMinutes(5), CancellationToken.None);
        var partnerEvent = context.WaitForExternalEvent<bool>(
            state.MissingBlob == "listing" ? "listing-ready" : "description-ready");

        var winner = await Task.WhenAny(timeout, partnerEvent);
        if (winner == partnerEvent)
            timeout.Cancel();
    }

    // Process listing (with or without description)
    await context.CallActivityAsync(nameof(ProcessListingActivity), input);
}
```

## Database Changes

### New Column

```sql
ALTER TABLE Listings ADD COLUMN DescriptionStatus TEXT DEFAULT 'pending';
-- Values: 'pending', 'complete', 'missing', 'failed'
```

### Status Values

| Status | Meaning |
|--------|---------|
| `pending` | Not yet processed |
| `complete` | Listing + description both processed |
| `missing` | Description never arrived (timeout) |
| `failed` | Description parsing failed |

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Both blobs arrive | Process immediately, DescriptionStatus = complete |
| Only listing (5 min timeout) | Process without description, DescriptionStatus = missing |
| Listing parse fails | Log error, orchestration fails, Azure retries |
| Description parse fails | Save listing, DescriptionStatus = failed |
| Blob trigger fails | Azure retries automatically (poison queue after 5 failures) |

## Performance Impact

| Metric | Before | After |
|--------|--------|-------|
| Time to first indexed listing | ~15 minutes (after batch) | ~5-10 seconds |
| Description fetch | Sequential (+5s per listing) | Parallel (no added time) |
| Polling overhead | 5-10s intervals | Zero (event-driven) |
| Processing unit | Batch of 50+ | Single listing |

## Local Development

All components work with Azurite:

```bash
# Start Azurite
npx azurite --blobPort 10000 --queuePort 10001 --tablePort 10002

# Start Scraper Worker
cd AIOWebScraper/ScraperWorker && dotnet run -- --dedicated-mode

# Start ETL Functions
cd AIOMarketMaker/AIOMarketMaker.Etl && func start

# Start API Functions (optional)
cd AIOMarketMaker/AIOMarketMaker.Functions && func start
```

## Future Enhancements

1. **Retry missing descriptions** - Timer trigger to re-scrape listings with DescriptionStatus = missing
2. **Embedding generation** - Add step to orchestration pipeline
3. **Pinecone indexing** - Add step to orchestration pipeline
4. **Progress reporting** - Track completed listings per job in real-time

## Implementation Order

1. Scraper: Add GroupId/FileKey to queue message and blob path
2. Market Maker Core: Add BuildDescriptionUrl() and ScrapeUrlRequest
3. Market Maker Etl: Convert to Azure Functions, add blob triggers
4. Market Maker Etl: Add Durable orchestration with external events
5. Market Maker Functions: Strip down to HTTP API only
6. Database: Add DescriptionStatus column migration
7. Integration testing with Azurite
