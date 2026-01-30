# Fix SweepOrchestrator DI Error Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix the dependency injection error preventing SweepOrchestrator from recovering missed blob triggers.

**Architecture:** Move DurableTaskClient-dependent operations (GetInstanceAsync, ScheduleNewOrchestrationInstanceAsync) from activities into the orchestrator itself. Activities can only inject services registered in DI (EtlDbContext, BlobServiceClient), not DurableTaskClient which is only available via [DurableClient] attribute or TaskOrchestrationContext.

**Tech Stack:** Azure Durable Functions, .NET 8.0, Entity Framework Core

---

## Background

**Root Cause:** `FindStalePendingListingsActivity` and `StartMissingOrchestrationActivity` inject `DurableTaskClient` via constructor, but `DurableTaskClient` is only available in:
1. HTTP/Blob triggers via `[DurableClient]` attribute
2. Orchestrators via `TaskOrchestrationContext`

**Error:**
```
Unable to resolve service for type 'Microsoft.DurableTask.Client.DurableTaskClient'
while attempting to activate 'AIOMarketMaker.Etl.Activities.FindStalePendingListingsActivity'
```

**Fix Strategy:**
- `FindStalePendingListingsActivity` → Return listing IDs with blob existence status, orchestrator checks/starts orchestrations
- `StartMissingOrchestrationActivity` → Delete (orchestrator handles directly)

---

### Task 1: Refactor FindStalePendingListingsActivity

Remove DurableTaskClient dependency. Activity returns pending listings + blob existence. Orchestrator handles orchestration checks.

**Files:**
- Modify: `AIOMarketMaker.Etl/Activities/FindStalePendingListingsActivity.cs`
- Modify: `AIOMarketMaker.Etl/Models/SweepModels.cs`

**Step 1: Update SweepModels.cs**

Replace the `FindStalePendingListingsResult` to return simpler data:

```csharp
namespace AIOMarketMaker.Etl.Models;

public record SweepOrchestratorInput(int ScrapeRunId);

public record PendingListingInfo(
    string ListingId,
    bool BlobExists
);

public record FindPendingListingsResult(List<PendingListingInfo> PendingListings);

public record StartOrchestrationInput(int ScrapeRunId, string ListingId);

public record StartSweepInput(int ScrapeRunId, string InstanceId);
```

**Step 2: Rewrite FindStalePendingListingsActivity**

Remove DurableTaskClient, just check blob existence:

```csharp
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

public class FindStalePendingListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<FindStalePendingListingsActivity> _logger;

    public FindStalePendingListingsActivity(
        EtlDbContext dbContext,
        BlobServiceClient blobService,
        ILogger<FindStalePendingListingsActivity> logger)
    {
        _dbContext = dbContext;
        _blobService = blobService;
        _logger = logger;
    }

    [Function(nameof(FindStalePendingListingsActivity))]
    public async Task<FindPendingListingsResult> Run([ActivityTrigger] int scrapeRunId)
    {
        // Get all pending listings for this run
        var pendingListings = await _dbContext.ScrapeRunListings
            .Where(srl => srl.ScrapeRunId == scrapeRunId && srl.Status == "Pending")
            .Select(srl => srl.ListingId)
            .ToListAsync();

        if (pendingListings.Count == 0)
        {
            return new FindPendingListingsResult(new List<PendingListingInfo>());
        }

        var container = _blobService.GetBlobContainerClient("html");
        var results = new List<PendingListingInfo>();

        foreach (var listingId in pendingListings)
        {
            // Check if listing blob exists
            var blobPath = $"{scrapeRunId}/{listingId}/listing.html";
            var blobClient = container.GetBlobClient(blobPath);
            var blobExists = await blobClient.ExistsAsync();

            results.Add(new PendingListingInfo(listingId, blobExists.Value));
        }

        _logger.LogInformation(
            "Found {Total} pending listings for ScrapeRun {ScrapeRunId}, {WithBlob} have blobs",
            results.Count, scrapeRunId, results.Count(r => r.BlobExists));

        return new FindPendingListingsResult(results);
    }
}
```

**Step 3: Build to verify no compilation errors**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeded (with warnings about old types)

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/FindStalePendingListingsActivity.cs AIOMarketMaker/AIOMarketMaker.Etl/Models/SweepModels.cs
git commit -m "fix: remove DurableTaskClient dependency from FindStalePendingListingsActivity"
```

---

### Task 2: Delete StartMissingOrchestrationActivity

The orchestrator will handle starting orchestrations directly using TaskOrchestrationContext.

**Files:**
- Delete: `AIOMarketMaker.Etl/Activities/StartMissingOrchestrationActivity.cs`

**Step 1: Delete the file**

```bash
rm AIOMarketMaker/AIOMarketMaker.Etl/Activities/StartMissingOrchestrationActivity.cs
```

**Step 2: Build to verify**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build fails (SweepOrchestrator references deleted activity)

**Step 3: Commit**

```bash
git add -A
git commit -m "refactor: remove StartMissingOrchestrationActivity (orchestrator handles directly)"
```

---

### Task 3: Refactor SweepOrchestrator

Use TaskOrchestrationContext to check/start orchestrations directly.

**Files:**
- Modify: `AIOMarketMaker.Etl/Orchestrators/SweepOrchestrator.cs`

**Step 1: Rewrite SweepOrchestrator**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Orchestrators;

public class SweepOrchestrator
{
    private const int InitialDelaySeconds = 90;  // Wait for JobOrchestrator to submit work
    private const int PollIntervalSeconds = 60;
    private const int MaxIterations = 30; // 30 minutes max

    [Function(nameof(SweepOrchestrator))]
    public async Task Run([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<SweepOrchestrator>();
        var input = context.GetInput<SweepOrchestratorInput>()!;

        logger.LogInformation(
            "Starting sweep orchestrator for ScrapeRun {ScrapeRunId}, waiting {InitialDelay}s before first check",
            input.ScrapeRunId, InitialDelaySeconds);

        // Initial delay to let JobOrchestrator submit work and blob triggers fire
        var initialWait = context.CurrentUtcDateTime.AddSeconds(InitialDelaySeconds);
        await context.CreateTimer(initialWait, CancellationToken.None);

        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            // Wait before checking (except on first iteration after initial delay)
            if (iteration > 0)
            {
                var nextCheck = context.CurrentUtcDateTime.AddSeconds(PollIntervalSeconds);
                await context.CreateTimer(nextCheck, CancellationToken.None);
            }

            // Check run status first
            var runStatus = await context.CallActivityAsync<ScrapeRunStatusResult>(
                nameof(GetScrapeRunStatusActivity),
                input.ScrapeRunId);

            // If run is already completed or failed, we're done
            if (runStatus.Status is "Completed" or "Failed" or "NotFound")
            {
                logger.LogInformation(
                    "Sweep exiting for ScrapeRun {ScrapeRunId} - run status is {Status}",
                    input.ScrapeRunId, runStatus.Status);
                return;
            }

            // Check how many pending listings remain in the junction table
            var pendingCount = await context.CallActivityAsync<int>(
                nameof(GetPendingCountActivity),
                input.ScrapeRunId);

            // Only exit when pendingCount == 0 if we're past the Indexing phase start
            var isInIndexingOrLater = runStatus.CurrentPhase is "Indexing" or "Completed";

            if (pendingCount == 0 && isInIndexingOrLater)
            {
                logger.LogInformation(
                    "Sweep complete for ScrapeRun {ScrapeRunId} - no pending listings remain (phase: {Phase})",
                    input.ScrapeRunId, runStatus.CurrentPhase);
                return;
            }

            if (pendingCount == 0)
            {
                // Not in Indexing phase yet - keep waiting
                logger.LogInformation(
                    "Sweep iteration {Iteration} for ScrapeRun {ScrapeRunId}: waiting for Indexing phase (current: {Phase})",
                    iteration + 1, input.ScrapeRunId, runStatus.CurrentPhase);
                continue;
            }

            logger.LogInformation(
                "Sweep iteration {Iteration} for ScrapeRun {ScrapeRunId}: {PendingCount} pending listings",
                iteration + 1, input.ScrapeRunId, pendingCount);

            // Find pending listings and their blob status
            var result = await context.CallActivityAsync<FindPendingListingsResult>(
                nameof(FindStalePendingListingsActivity),
                input.ScrapeRunId);

            // Find listings where blob exists (potential missed triggers)
            var listingsWithBlobs = result.PendingListings
                .Where(p => p.BlobExists)
                .ToList();

            if (listingsWithBlobs.Count > 0)
            {
                logger.LogInformation(
                    "Found {Count} pending listings with blobs for ScrapeRun {ScrapeRunId}, checking orchestrations",
                    listingsWithBlobs.Count, input.ScrapeRunId);

                int startedCount = 0;
                foreach (var pending in listingsWithBlobs)
                {
                    var instanceId = $"etl-{input.ScrapeRunId}-{pending.ListingId}";

                    // Use sub-orchestrator to start if not exists (orchestrator can call sub-orchestrators)
                    var started = await context.CallSubOrchestratorAsync<bool>(
                        nameof(StartOrchestrationIfNotExistsOrchestrator),
                        new StartOrchestrationInput(input.ScrapeRunId, pending.ListingId));

                    if (started)
                    {
                        startedCount++;
                    }
                }

                logger.LogInformation(
                    "Sweep started {StartedCount} missing orchestrations for ScrapeRun {ScrapeRunId}",
                    startedCount, input.ScrapeRunId);
            }
        }

        logger.LogWarning(
            "Sweep reached max iterations ({MaxIterations}) for ScrapeRun {ScrapeRunId}, exiting",
            MaxIterations, input.ScrapeRunId);
    }
}
```

**Step 2: Build (will fail - missing sub-orchestrator)**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Error - StartOrchestrationIfNotExistsOrchestrator not found

**Step 3: Commit partial progress**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/SweepOrchestrator.cs
git commit -m "refactor: update SweepOrchestrator to use sub-orchestrator pattern"
```

---

### Task 4: Create StartOrchestrationIfNotExistsOrchestrator

Sub-orchestrator that checks if ListingEtlOrchestrator exists, starts it if not.

**Files:**
- Create: `AIOMarketMaker.Etl/Orchestrators/StartOrchestrationIfNotExistsOrchestrator.cs`

**Step 1: Create the sub-orchestrator**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Orchestrators;

/// <summary>
/// Sub-orchestrator that starts ListingEtlOrchestrator if it doesn't exist.
/// Used by SweepOrchestrator to recover missed blob triggers.
/// </summary>
public class StartOrchestrationIfNotExistsOrchestrator
{
    [Function(nameof(StartOrchestrationIfNotExistsOrchestrator))]
    public async Task<bool> Run([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<StartOrchestrationIfNotExistsOrchestrator>();
        var input = context.GetInput<StartOrchestrationInput>()!;

        var instanceId = $"etl-{input.ScrapeRunId}-{input.ListingId}";

        // Check if orchestration already exists
        // Note: We can't call GetInstanceAsync from orchestrator, but we CAN start with
        // the same instanceId - Durable Functions will reject duplicate starts
        try
        {
            var etlInput = new ListingEtlInput(input.ScrapeRunId, input.ListingId, TriggerSource.Sweep);

            // ScheduleNewOrchestrationInstanceAsync will throw if instance already exists
            // with the same instanceId (when using StartOrchestrationOptions)
            await context.CallSubOrchestratorAsync(
                nameof(ListingEtlOrchestrator),
                etlInput,
                new SubOrchestrationOptions { InstanceId = instanceId });

            logger.LogInformation(
                "Started missing orchestration {InstanceId} via sweep",
                instanceId);

            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("already exists") ||
                                   ex.Message.Contains("conflict") ||
                                   ex.Message.Contains("duplicate"))
        {
            logger.LogInformation(
                "Orchestration {InstanceId} already exists, skipping",
                instanceId);
            return false;
        }
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/StartOrchestrationIfNotExistsOrchestrator.cs
git commit -m "feat: add StartOrchestrationIfNotExistsOrchestrator sub-orchestrator"
```

---

### Task 5: Clean up unused code

Remove StartMissingOrchestrationActivity reference from StartSweepOrchestratorActivity if present, and remove old StaleListingInfo model.

**Files:**
- Modify: `AIOMarketMaker.Etl/Models/SweepModels.cs` (remove StaleListingInfo, FindStalePendingListingsResult)

**Step 1: Verify SweepModels.cs is clean**

Should only contain:
```csharp
namespace AIOMarketMaker.Etl.Models;

public record SweepOrchestratorInput(int ScrapeRunId);

public record PendingListingInfo(
    string ListingId,
    bool BlobExists
);

public record FindPendingListingsResult(List<PendingListingInfo> PendingListings);

public record StartOrchestrationInput(int ScrapeRunId, string ListingId);

public record StartSweepInput(int ScrapeRunId, string InstanceId);
```

**Step 2: Build full solution**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add -A
git commit -m "chore: clean up unused sweep models"
```

---

### Task 6: Manual Integration Test

Test the fix by starting a scrape and verifying SweepOrchestrator runs successfully.

**Step 1: Restart ETL Functions**

```bash
# Stop existing func processes
pkill -f "func start"

# Restart ETL
cd <REPO_ROOT>/AIOMarketMaker.Etl && func start --port 7072
```

**Step 2: Start a scrape**

```bash
curl -s http://localhost:7072/api/scrape/start -X POST -H "Content-Type: application/json" | jq '.'
```

**Step 3: Verify SweepOrchestrator is running (not failed)**

```bash
curl -s "http://localhost:7072/runtime/webhooks/durabletask/instances?instanceIdPrefix=sweep" | jq '.[] | {instanceId, runtimeStatus, output}'
```

Expected: `runtimeStatus: "Running"` (not "Failed")

**Step 4: Check for DI errors in logs**

If SweepOrchestrator is running without DI errors, the fix is successful.

---

## Verification Checklist

- [ ] `FindStalePendingListingsActivity` no longer injects `DurableTaskClient`
- [ ] `StartMissingOrchestrationActivity` is deleted
- [ ] `SweepOrchestrator` uses sub-orchestrator pattern
- [ ] `StartOrchestrationIfNotExistsOrchestrator` handles duplicate detection
- [ ] Solution builds without errors
- [ ] SweepOrchestrator runs without DI failures
