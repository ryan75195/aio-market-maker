# Bug: Premature ScrapeRun Completion with Incorrect Phase

**Date:** 2026-01-28
**Status:** Fixed
**Severity:** High
**Component:** AIOMarketMaker.Etl Orchestrators
**Fixed:** 2026-01-28 - Added phase check to prevent premature completion

## Summary

ScrapeRun records are being marked as "Completed" while still on the "Searching" phase, with only a fraction of listings processed. This results in incomplete data extraction and misleading status in the UI.

## Symptoms

Observed on run 13013:
```json
{
  "Id": 13013,
  "Status": "Completed",
  "CurrentPhase": "Searching",
  "TotalListingsFound": 1047,
  "ListingsProcessed": 21
}
```

**Expected behavior:**
- Status should be "Completed" only after all phases complete
- CurrentPhase should progress: Searching → Filtering → Indexing → Completed
- ListingsProcessed should equal TotalListingsFound when Status is "Completed"

**Actual behavior:**
- Status is "Completed" while CurrentPhase is still "Searching"
- Only 21 of 1047 listings were processed
- Run completed in ~2.5 minutes despite incomplete processing

## Root Cause Analysis

### Phase Progression Flow (JobOrchestrator.cs)

```
Line 47:  phase = "Searching"
Line 111: phase = "Filtering"
Line 162: phase = "Indexing", TotalListingsFound = newListingIds.Count
Line 104/133: phase = "Completed" (early exit paths)
```

### Completion Logic (UpdateScrapeRunActivity.cs)

```csharp
// Line 41-46
if (run.TotalListingsFound == 0 || run.ListingsProcessed >= run.TotalListingsFound)
{
    run.CompletedUtc = DateTime.UtcNow;
    run.Status = "Completed";
}
```

### Potential Race Conditions

1. **ScrapeOrchestrator completes before phases progress**
   - ScrapeOrchestrator calls UpdateScrapeRunActivity after JobOrchestrator returns
   - If TotalListingsFound is still 0 at this point, condition `run.TotalListingsFound == 0` triggers
   - Status set to "Completed" prematurely

2. **Order of operations issue**
   - UpdateScrapeRunProgressActivity (sets TotalListingsFound) may not have committed
   - UpdateScrapeRunActivity (checks TotalListingsFound) runs with stale value
   - EF Core DbContext isolation between activities may not be sufficient

3. **Phase never advancing past "Searching"**
   - If TotalListingsFound = 1047 but CurrentPhase = "Searching", the progress update at line 162 never executed
   - Suggests exception or early return between line 47 and line 162

## Inconsistent State Explanation

The observed state (TotalListingsFound = 1047, CurrentPhase = "Searching") is logically inconsistent:
- TotalListingsFound is only set at line 162 (with phase = "Indexing")
- Or at line 266 in DetectAndUpdateSoldListingsAsync (with phase = "Updating Listings")
- Neither path results in CurrentPhase = "Searching"

This suggests one of:
1. TotalListingsFound was updated but CurrentPhase update failed
2. Multiple concurrent updates with race condition
3. A code path that sets TotalListingsFound without setting CurrentPhase

## Affected Files

- `AIOMarketMaker.Etl/Orchestrators/ScrapeOrchestrator.cs` - Lines 88-97
- `AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs` - Lines 47, 111, 159-162
- `AIOMarketMaker.Etl/Activities/UpdateScrapeRunActivity.cs` - Lines 41-46
- `AIOMarketMaker.Etl/Activities/UpdateScrapeRunProgressActivity.cs` - Lines 32-39
- `AIOMarketMaker.Etl/Activities/UpdateScrapeRunListingActivity.cs` - Lines 45-52

## Additional Issue: Queue Worker Not Processing

During debugging, discovered the queue worker was not running or not processing messages:
- `scrape-work` queue had old messages from 02:55:44 with DequeueCount = 0
- New scrape runs were stuck on "Searching" indefinitely
- Queue worker process was not found in running processes

## Reproduction Steps

1. Start local environment (Azurite, API, ETL, WebScraper)
2. Trigger a scrape run via `POST http://localhost:7072/api/scrape/start`
3. Monitor run status via `GET http://localhost:7071/api/history`
4. Observe run completing with inconsistent state

## Proposed Fixes

### Option 1: Don't mark complete if phases incomplete
```csharp
// UpdateScrapeRunActivity.cs - add phase check
else if (run.CurrentPhase == "Completed" &&
         (run.TotalListingsFound == 0 || run.ListingsProcessed >= run.TotalListingsFound))
{
    run.CompletedUtc = DateTime.UtcNow;
    run.Status = "Completed";
}
```

### Option 2: Let blob triggers handle completion
Remove completion logic from UpdateScrapeRunActivity entirely. Let UpdateScrapeRunListingActivity's SQL handle it when all listings are processed.

### Option 3: Add atomic phase/count updates
Use database transactions to ensure phase and count updates are atomic.

## Test Cases Needed

1. Verify phase progression: Searching → Filtering → Indexing → Completed
2. Verify Status = "Completed" only when CurrentPhase = "Completed"
3. Verify ListingsProcessed = TotalListingsFound when complete
4. Test with various listing counts (0, 1, 100, 1000+)
5. Test concurrent updates to ScrapeRun record

## Resolution

**Fix Applied:** Option 1 - Phase check before marking complete

Changed `UpdateScrapeRunActivity.cs` line 41 from:
```csharp
else if (run.TotalListingsFound == 0 || run.ListingsProcessed >= run.TotalListingsFound)
```

To:
```csharp
else if (run.CurrentPhase == "Completed" &&
         (run.TotalListingsFound == 0 || run.ListingsProcessed >= run.TotalListingsFound))
```

This ensures `Status = "Completed"` is only set when `CurrentPhase` explicitly indicates completion, preventing race conditions where multiple jobs overwrite each other's progress.

**Tests Added:** `UpdateScrapeRunActivityTests.cs` with 6 test cases covering:
- Should NOT complete when phase is not "Completed" (even if TotalListingsFound == 0)
- Should complete when phase IS "Completed" and TotalListingsFound == 0
- Should NOT complete when phase is "Indexing" with pending listings
- Should complete when phase is "Completed" and all listings processed
- Should mark failed regardless of phase
- Should not throw when run not found

## Related Issues

- Queue worker reliability/monitoring
- Need for better observability into orchestration state
