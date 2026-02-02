# SweepOrchestrator Design

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a job-scoped sweep mechanism that detects and recovers from missed blob triggers by starting orphaned ETL orchestrations.

**Architecture:** Sub-orchestration spawned by JobOrchestrator that polls for stale pending listings and starts missing orchestrations.

**Tech Stack:** Azure Durable Functions, Azure Blob Storage, EF Core

---

## Problem Statement

Blob triggers in Azurite (and occasionally in production) can miss events. When a worker uploads HTML but the blob trigger doesn't fire, the listing remains "Pending" forever, causing the ScrapeRun to stall.

## Solution Overview

Add a `SweepOrchestrator` that:
1. Starts when a ScrapeRun enters the Indexing phase
2. Polls every 60 seconds for "stale" pending listings
3. Starts missing ETL orchestrations for listings where blob exists but orchestration doesn't
4. Terminates when all listings are processed or max duration reached

## Components

### SweepOrchestrator

**Instance ID:** `sweep-{scrapeRunId}`

**Input:** `SweepOrchestratorInput(int ScrapeRunId)`

**Logic:**
```
const int PollIntervalSeconds = 60;
const int MaxIterations = 30;  // 30 min max

for (int i = 0; i < MaxIterations; i++)
{
    // Find stale listings (blob exists, no orchestration)
    var staleListings = await CallActivityAsync<List<StaleListingInfo>>(
        nameof(FindStalePendingListingsActivity), input.ScrapeRunId);

    // Start missing orchestrations
    foreach (var listing in staleListings.Where(l => l.BlobExists && !l.OrchestrationExists))
    {
        await CallActivityAsync(nameof(StartMissingOrchestrationActivity),
            new StartOrchestrationInput(input.ScrapeRunId, listing.ListingId));
    }

    // Check if all done
    var pendingCount = await CallActivityAsync<int>(
        nameof(GetPendingCountActivity), input.ScrapeRunId);

    if (pendingCount == 0)
    {
        logger.LogInformation("Sweep complete - no pending listings remain");
        return;
    }

    // Wait before next iteration
    await CreateTimer(CurrentUtcDateTime.AddSeconds(PollIntervalSeconds), CancellationToken.None);
}

logger.LogWarning("Sweep reached max iterations, exiting");
```

### FindStalePendingListingsActivity

**Input:** `int scrapeRunId`

**Output:** `List<StaleListingInfo>`

**Logic:**
1. Query `ScrapeRunListings WHERE ScrapeRunId = @id AND Status = 'Pending'`
2. For each pending listing:
   - Check if blob exists at `html/{scrapeRunId}/{listingId}/listing.html`
   - Check if orchestration `etl-{scrapeRunId}-{listingId}` exists (via DurableTaskClient)
3. Return list with blob/orchestration status for each

### StartMissingOrchestrationActivity

**Input:** `StartOrchestrationInput(int ScrapeRunId, string ListingId)`

**Output:** `bool Started`

**Logic:**
1. Try to schedule orchestration with ID `etl-{scrapeRunId}-{listingId}`
2. Input: `ListingEtlInput(ScrapeRunId, ListingId, TriggerSource.Sweep)`
3. Return true if started, false if already exists

### GetPendingCountActivity

**Input:** `int scrapeRunId`

**Output:** `int` (count of pending listings)

**Logic:** `SELECT COUNT(*) FROM ScrapeRunListings WHERE ScrapeRunId = @id AND Status = 'Pending'`

## Files to Create

- `AIOMarketMaker.Etl/Orchestrators/SweepOrchestrator.cs`
- `AIOMarketMaker.Etl/Activities/FindStalePendingListingsActivity.cs`
- `AIOMarketMaker.Etl/Activities/StartMissingOrchestrationActivity.cs`
- `AIOMarketMaker.Etl/Activities/GetPendingCountActivity.cs`
- `AIOMarketMaker.Etl/Models/SweepModels.cs`

## Files to Modify

- `AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs` - spawn SweepOrchestrator after SubmitScrapeJobsActivity
- `AIOMarketMaker.Etl/Models/ListingEtlInput.cs` - add `Sweep` to TriggerSource enum

## Integration

In `JobOrchestrator.cs`, after submitting scrape jobs:

```csharp
// Start sweep orchestrator to handle missed blob triggers
await context.CallSubOrchestratorAsync(
    nameof(SweepOrchestrator),
    new SweepOrchestratorInput(scrapeRunId),
    new TaskOptions { InstanceId = $"sweep-{scrapeRunId}" });
```

Note: Using `CallSubOrchestratorAsync` means JobOrchestrator waits for sweep to complete. If we want fire-and-forget, use `ScheduleNewOrchestrationInstanceAsync` instead.

**Decision:** Use `ScheduleNewOrchestrationInstanceAsync` (fire-and-forget) so JobOrchestrator doesn't block.

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| Blob trigger fires normally | Sweep finds orchestration exists, skips |
| Blob trigger misses | Sweep starts orchestration after ~60s |
| All listings complete quickly | Sweep finds 0 pending, exits immediately |
| Duplicate SweepOrchestrator | Rejected by instance ID collision |
| ScrapeRun fails/cancels | Sweep times out after 30 min |
| Listing fails in ETL | Status changes to Failed, sweep ignores |

## Testing

1. **Unit tests:** Mock activities, verify sweep logic
2. **Integration test:** Start job, artificially skip blob trigger, verify sweep recovers
3. **E2E test:** Run duplicate jobs, verify both complete (original bug scenario)
