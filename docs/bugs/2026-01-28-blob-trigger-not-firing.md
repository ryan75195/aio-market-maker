# Bug: Blob Trigger Not Firing - Pipeline Stall After Listing Scrape Completion

**Date:** 2026-01-28
**Status:** Fixed
**Severity:** Critical
**Component:** AIOMarketMaker.Etl Blob Triggers
**Related Runs:** 15018 (PlayStation 5 Console), 15019 (Glasses)
**Fixed By:** Status check before RaiseEventAsync

## Summary

Scrape runs stall permanently in the "Indexing" phase with partial `ListingsProcessed` counts. Workers successfully fetch and store HTML blobs, but the blob triggers (`OnListingBlobCreated`, `OnDescriptionBlobCreated`) never fire to process them. This results in orphaned blobs and incomplete scrape runs that never complete.

## Symptoms

Observed on runs 15018 and 15019:

```
Run 15018 (PlayStation 5 Console):
- Status: Running
- CurrentPhase: Updating Listings
- TotalListingsFound: 6
- ListingsProcessed: 4
- Time Running: 808+ seconds (13+ minutes)

Run 15019 (Glasses):
- Status: Running
- CurrentPhase: Indexing
- TotalListingsFound: 100
- ListingsProcessed: 34
- Time Running: 808+ seconds (13+ minutes)
```

**Expected behavior:**
- Workers fetch listings and save to `html/{listingId}/listing.html`
- Blob trigger fires for each new blob
- `ListingEtlOrchestrator` processes the listing
- `UpdateScrapeRunListingActivity` marks listing complete and increments `ListingsProcessed`
- Run completes when all listings processed

**Actual behavior:**
- Workers successfully save blobs (verified 301 listing blobs, 299 description blobs in Azurite)
- Blob triggers NEVER fire
- Database stuck at 34/100 processed
- Workers complete and go idle
- Runs remain in "Running" state indefinitely

## Root Cause Analysis

### Evidence Gathered

#### 1. Workers ARE Processing Successfully

```
[13:30:11 INF] [29d815b0] SUCCESS: https://www.ebay.co.uk/itm/157428964518 (2228KB)
[13:30:11 INF] MarkJobCompletedAsync: Job 29d815b0 completed with status Success
[13:30:11 INF] [29d815b0] JOB COMPLETED
```

All 5 Docker workers processed URLs and completed around 13:30:35. Response sizes (2.2MB+) indicate real eBay pages, not bot detection.

#### 2. Blobs ARE Being Created

Azurite access logs show successful blob creation:
```
172.17.0.1 - [28/Jan/2026:13:29:46] "PUT /devstoreaccount1/html/116997195126/listing.html" 201
172.17.0.1 - [28/Jan/2026:13:29:46] "PUT /devstoreaccount1/html/127363607075/description.html" 201
172.17.0.1 - [28/Jan/2026:13:30:35] "PUT /devstoreaccount1/html/177452060164/listing.html" 201
```

Verified blob counts via Azurite database:
```
Blobs matching {listingId}/listing.html: 301
Blobs matching {listingId}/description.html: 299
```

Sample blob paths (correct format for trigger):
```
397545255154/listing.html
157627445176/listing.html
236605441152/listing.html
```

#### 3. Blob Triggers NOT Firing

**No blob trigger activity in Azurite logs.** After the initial PUT requests at 13:30:35, the only blob-related request is a failed CLI query at 13:45:17 (403 - authorization failure from my investigation).

The Azure Functions blob trigger should periodically scan the `html` container and process new blobs. These scan requests would appear as GET requests to the container with `comp=list`. None were observed.

#### 4. Queue Is Empty (Workers Completed)

```bash
az storage message peek --queue-name "scrape-work" --num-messages 32
# Returns empty - all messages processed
```

Workers drained the queue by 13:30:35 and went idle.

#### 5. Database Progress Stuck

```sql
SELECT ScrapeRunId, Status, COUNT(*) as Count
FROM ScrapeRunListings
WHERE ScrapeRunId = 15019
GROUP BY ScrapeRunId, Status;

-- Result:
-- ScrapeRunId | Status   | Count
-- 15019       | Complete | 34
-- 15019       | Pending  | 66
```

Only 34 of 100 listings marked complete, despite 301 listing blobs existing.

### Architecture Flow

```
[Worker] → SaveContentAsync() → [Azurite Blob: html/{listingId}/listing.html]
                                           ↓
                                   [Blob Trigger: OnListingBlobCreated]  ← NOT FIRING
                                           ↓
                                   [ListingEtlOrchestrator]
                                           ↓
                                   [UpdateScrapeRunListingActivity]
                                           ↓
                                   [Database: ScrapeRunListings.Status = 'Complete']
```

The chain breaks at the blob trigger. Blobs are written but never processed.

### Infrastructure Configuration

**Azurite runs in Docker container:**
```bash
docker ps
# azurite    mcr.microsoft.com/azure-storage/azurite   0.0.0.0:10000-10002->10000-10002/tcp
```

**Workers connect via Docker networking:**
```
tableStorageConnectionString=...TableEndpoint=http://host.docker.internal:10002/devstoreaccount1;
blobStorageKey=...BlobEndpoint=http://host.docker.internal:10000/devstoreaccount1;
queueStorageConnectionString=...QueueEndpoint=http://host.docker.internal:10001/devstoreaccount1;
```

**Functions host uses standard development storage:**
```json
// local.settings.json
{
  "blobStorageConnectionString": "UseDevelopmentStorage=true"
}
```

`UseDevelopmentStorage=true` resolves to `http://127.0.0.1:10000/devstoreaccount1`, which reaches the same Azurite container via Docker port mapping.

### Trigger Binding Configuration

```csharp
// ListingBlobTrigger.cs
[Function("OnListingBlobCreated")]
public async Task Run(
    [BlobTrigger("html/{listingId}/listing.html",
     Connection = "blobStorageConnectionString")] string html,
    [DurableClient] DurableTaskClient client,
    string listingId)
```

The trigger watches `html/{listingId}/listing.html` which matches the blob path format used by workers.

### Probable Root Causes

1. **Blob trigger scanner not running or failing silently**
   - Azure Functions uses a storage-based blob receipt system to track processed blobs
   - The scanner may have failed to initialize or encountered an error
   - No error logs visible in Azurite

2. **Azurite blob change detection incompatibility**
   - Azurite emulates Azure Storage but may not fully support the blob trigger polling mechanism
   - Event Grid (used in production) is not available locally

3. **Connection string mismatch**
   - Functions host may be connecting to a different storage endpoint
   - `UseDevelopmentStorage=true` resolution may differ from `host.docker.internal`

4. **Blob receipts preventing reprocessing**
   - If blobs were previously processed (from an earlier run), they may be marked as "already processed"
   - The `azure-webjobs-hosts` container stores blob receipts

## Environment Details

- **Azurite:** Running in Docker container, ports 10000-10002 mapped
- **Workers:** 5 Docker containers (`scraper-queue-worker` through `scraper-queue-worker-5`)
- **Functions Host:** `func.exe` v4.1045.200 running on port 7071
- **Database:** SQL Server LocalDB `(localdb)\MSSQLLocalDB`, database `AIOMarketMaker`

### Containers in Azurite

```
azure-webjobs-secrets
testhubname-applease
azure-webjobs-hosts
testhubname-leases
html                    ← Target container for triggers
testhubname-largemessages
```

## Affected Files

- `AIOMarketMaker.Etl/Triggers/ListingBlobTrigger.cs` - Trigger that should fire
- `AIOMarketMaker.Etl/Triggers/DescriptionBlobTrigger.cs` - Companion trigger
- `AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs` - Orchestrator that should be started
- `AIOMarketMaker.Etl/Activities/UpdateScrapeRunListingActivity.cs` - Activity that updates progress
- `AIOMarketMaker.Etl/local.settings.json` - Connection string configuration

## Reproduction Steps

1. Start Azurite in Docker:
   ```bash
   docker run -p 10000:10000 -p 10001:10001 -p 10002:10002 \
     mcr.microsoft.com/azure-storage/azurite
   ```

2. Start 5 scraper workers:
   ```bash
   docker run -e "blobStorageKey=..." -e "queueStorageConnectionString=..." \
     scraper-worker:latest
   ```

3. Start Functions host:
   ```bash
   cd AIOMarketMaker/AIOMarketMaker.Etl && func start
   ```

4. Trigger a scrape via UI or API

5. Observe:
   - Workers process URLs and save blobs (check Azurite logs for PUT 201)
   - Scrape run progress stalls (check `GET /api/history`)
   - Blob trigger never fires (no orchestration logs)

## Investigation Commands

### Check Azurite blob database
```powershell
$json = docker exec azurite cat /data/__azurite_db_blob__.json
$listingBlobs = [regex]::Matches($json, '"containerName":"html","name":"(\d+/listing\.html)"')
Write-Output "Listing blobs: $($listingBlobs.Count)"
```

### Check Azurite access logs
```bash
docker logs azurite --tail 100 2>&1 | grep html
```

### Check worker activity
```bash
docker logs scraper-queue-worker --tail 20
```

### Check database state
```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker \
  -Q "SELECT ScrapeRunId, Status, COUNT(*) FROM ScrapeRunListings
      WHERE ScrapeRunId IN (15018, 15019)
      GROUP BY ScrapeRunId, Status" -W
```

## Proposed Fixes

### Option 1: Switch to Event-Based Processing

Replace blob triggers with direct activity calls from the worker pipeline:

```csharp
// After saving blob in worker
await _orchestrationClient.StartOrchestrationAsync(
    "ListingEtlOrchestrator",
    new ListingEtlInput(listingId, TriggerSource.Worker));
```

**Pros:** Eliminates blob trigger dependency, immediate processing
**Cons:** Requires API changes, tighter coupling

### Option 2: Use Queue Trigger Instead of Blob Trigger

After saving blob, enqueue a message to a processing queue:

```csharp
// Worker saves blob then enqueues processing message
await _jobRepository.SaveContentAsync(jobId, url, content, groupId, fileKey, ct);
await _processingQueue.EnqueueAsync(new ProcessListingMessage(listingId), ct);
```

**Pros:** Queue triggers are more reliable in Azurite
**Cons:** Requires new queue and code changes

### Option 3: Debug and Fix Blob Trigger Configuration

1. Enable verbose logging for blob triggers in `host.json`:
   ```json
   {
     "logging": {
       "logLevel": {
         "Microsoft.Azure.WebJobs.Extensions.Storage": "Debug"
       }
     }
   }
   ```

2. Clear blob receipts in `azure-webjobs-hosts` container

3. Verify `blobStorageConnectionString` resolves correctly

**Pros:** Maintains current architecture
**Cons:** May be Azurite limitation, not fixable locally

### Option 4: Hybrid - Use Host Azurite Instead of Docker

Run Azurite directly on host instead of in Docker:

```bash
npx azurite --blobPort 10000 --queuePort 10001 --tablePort 10002
```

**Pros:** Simpler networking, may resolve connectivity issues
**Cons:** Requires reconfiguring all connection strings

## Workarounds

### Manual Processing

Query orphaned blobs and manually trigger processing:

```csharp
// Get all listing IDs with blobs but not processed
var orphanedListings = await _blobContainer.GetBlobsAsync()
    .Where(b => b.Name.EndsWith("/listing.html"))
    .Select(b => b.Name.Split('/')[0])
    .Except(processedListingIds)
    .ToListAsync();

foreach (var listingId in orphanedListings)
{
    await _orchestrationClient.StartOrchestrationAsync(
        "ListingEtlOrchestrator",
        new ListingEtlInput(listingId, TriggerSource.Manual));
}
```

## Test Cases Needed

1. Verify blob trigger fires when blob created (requires working trigger)
2. Test with host Azurite vs Docker Azurite
3. Test blob trigger with various path formats
4. Verify blob receipts are being created/updated
5. Test queue-based alternative processing

## Related Issues

- 2026-01-28-premature-scrape-run-completion.md - Related completion logic issue
- Queue worker reliability - Workers completed but triggers didn't fire

## Appendix: Full Debugging Session Log

### Timeline

| Time | Event |
|------|-------|
| 12:34:29 | Functions host started |
| 12:34:46 | Azurite Docker container started |
| 13:17:00 | Scrape runs 15018, 15019 started |
| 13:17:00-13:21:00 | Searching phase (fetching search pages) |
| 13:21:00-13:30:35 | Workers fetching listing pages, saving blobs |
| 13:30:35 | Last blob saved, workers go idle |
| 13:30:35+ | No blob trigger activity, runs stalled |
| 13:37:22 | Investigation begins |
| 13:45:17 | CLI blob list query fails (403) |

### Blob Statistics (as of 13:37)

```
Total blobs in html container: 635
- Listing blobs ({id}/listing.html): 301
- Description blobs ({id}/description.html): 299
- Search result blobs ({jobId}/{url}.html): 35
```

### Database State (as of 13:37)

```
ScrapeRun 15018: Status=Running, Phase=Updating Listings, Found=6, Processed=4
ScrapeRun 15019: Status=Running, Phase=Indexing, Found=100, Processed=34
ScrapeRunListings 15019: 34 Complete, 66 Pending
```

## Root Cause (Confirmed)

**The bug report's initial analysis was incorrect.** The blob triggers WERE firing - they were detecting new blobs and attempting to process them. The actual root cause was:

### Actual Issue: RaiseEventAsync on Completed Orchestrations

The `ListingBlobTrigger` and `DescriptionBlobTrigger` called `client.RaiseEventAsync()` on orchestration instances that had already completed. This throws a `Grpc.Core.RpcException` in the isolated worker model, causing the trigger to fail repeatedly until the message was moved to the poison queue.

**Evidence:**
1. **Blob receipts were being created** - 224 listing receipts existed (triggers fired successfully for those)
2. **Poison queue contained failed triggers** - Messages for listings like `147106416420` which was already marked Complete in run 15017
3. **Work queue had retrying items** - Messages with `dequeueCount > 0` indicating retry attempts

### Code Flow That Failed

```csharp
// ListingBlobTrigger.cs (before fix)
var existingInstance = await client.GetInstanceAsync(instanceId);
if (existingInstance == null)
{
    // Start new orchestration - OK
}
else
{
    // BUG: existingInstance might be Completed/Failed/Terminated
    await client.RaiseEventAsync(instanceId, "listing-ready", true);  // THROWS!
}
```

When `existingInstance.RuntimeStatus` was `Completed`, `Failed`, or `Terminated`, the `RaiseEventAsync` call threw an exception, poisoning the blob trigger message.

### Why Some Listings Worked (34/100)

The first triggers for each listing started new orchestrations (branch 1). These completed successfully. When description blobs arrived later and tried to raise events on the now-completed orchestrations, they failed.

## Fix Applied

Modified both `ListingBlobTrigger.cs` and `DescriptionBlobTrigger.cs` to check the orchestration's runtime status before calling `RaiseEventAsync`:

```csharp
var existingInstance = await client.GetInstanceAsync(instanceId);
if (existingInstance == null)
{
    // Start new orchestration
    await client.ScheduleNewOrchestrationInstanceAsync(...);
}
else if (existingInstance.RuntimeStatus == OrchestrationRuntimeStatus.Running ||
         existingInstance.RuntimeStatus == OrchestrationRuntimeStatus.Pending ||
         existingInstance.RuntimeStatus == OrchestrationRuntimeStatus.Suspended)
{
    // Only raise event if orchestration is still active
    await client.RaiseEventAsync(instanceId, "listing-ready", true);
}
else
{
    // Orchestration already completed - skip gracefully
    _logger.LogInformation(
        "Orchestration {InstanceId} already completed with status {Status}, skipping event",
        instanceId, existingInstance.RuntimeStatus);
}
```

## Recovery Steps

To process the orphaned blobs from runs 15018/15019:

1. **Restart Functions host** to pick up the fix
2. **Clear poison queue** messages (they will never succeed as orchestrations completed):
   ```bash
   az storage message clear --queue-name "webjobs-blobtrigger-poison" \
     --connection-string "DefaultEndpointsProtocol=http;..."
   ```
3. **Clear blob receipts** for the orphaned listings to allow re-trigger:
   ```bash
   # Delete receipts for listings that have Pending status in ScrapeRunListings
   az storage blob delete-batch --source "azure-webjobs-hosts" \
     --pattern "blobreceipts/*/OnListingBlobCreated/*" \
     --connection-string "..."
   ```
4. **Alternatively**, create a manual recovery function that:
   - Queries ScrapeRunListings for Pending status
   - Checks if blob exists for each
   - Directly starts ListingEtlOrchestrator for each

## Lessons Learned

1. **Durable Functions gRPC exceptions** - The isolated worker model throws exceptions for operations on completed/non-existent orchestrations, unlike the in-process model which fails silently
2. **Always check RuntimeStatus** before raising events on existing orchestrations
3. **Poison queues are a symptom** - When triggers go to poison, investigate the trigger code, not just the infrastructure
