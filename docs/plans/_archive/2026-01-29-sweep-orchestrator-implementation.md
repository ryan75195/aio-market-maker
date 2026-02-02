# SweepOrchestrator Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a job-scoped sweep mechanism that detects and recovers from missed blob triggers by starting orphaned ETL orchestrations.

**Architecture:** Sub-orchestration spawned by JobOrchestrator that polls for stale pending listings and starts missing orchestrations. Terminates when all listings processed or timeout.

**Tech Stack:** Azure Durable Functions, Azure Blob Storage, EF Core, DurableTaskClient

---

## Task 1: Add Sweep TriggerSource

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Models/ListingEtlInput.cs:3-7`

**Step 1: Add Sweep to TriggerSource enum**

```csharp
public enum TriggerSource
{
    Listing,
    Description,
    Sweep
}
```

**Step 2: Build to verify**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj
```

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Models/ListingEtlInput.cs
git commit -m "feat: add Sweep trigger source for missed blob recovery"
```

---

## Task 2: Create SweepModels

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.Etl/Models/SweepModels.cs`

**Step 1: Create the models file**

```csharp
namespace AIOMarketMaker.Etl.Models;

public record SweepOrchestratorInput(int ScrapeRunId);

public record StaleListingInfo(
    string ListingId,
    bool BlobExists,
    bool OrchestrationExists
);

public record FindStalePendingListingsResult(List<StaleListingInfo> StaleListings);

public record StartOrchestrationInput(int ScrapeRunId, string ListingId);
```

**Step 2: Build to verify**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj
```

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Models/SweepModels.cs
git commit -m "feat: add sweep orchestrator model classes"
```

---

## Task 3: Create GetPendingCountActivity

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.Etl/Activities/GetPendingCountActivity.cs`

**Step 1: Create the activity**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Activities;

public class GetPendingCountActivity
{
    private readonly EtlDbContext _dbContext;

    public GetPendingCountActivity(EtlDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [Function(nameof(GetPendingCountActivity))]
    public async Task<int> Run([ActivityTrigger] int scrapeRunId)
    {
        return await _dbContext.ScrapeRunListings
            .CountAsync(srl => srl.ScrapeRunId == scrapeRunId && srl.Status == "Pending");
    }
}
```

**Step 2: Build to verify**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj
```

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/GetPendingCountActivity.cs
git commit -m "feat: add GetPendingCountActivity for sweep orchestrator"
```

---

## Task 4: Create FindStalePendingListingsActivity

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.Etl/Activities/FindStalePendingListingsActivity.cs`

**Step 1: Create the activity**

```csharp
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

public class FindStalePendingListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly BlobServiceClient _blobService;
    private readonly DurableTaskClient _durableClient;
    private readonly ILogger<FindStalePendingListingsActivity> _logger;

    public FindStalePendingListingsActivity(
        EtlDbContext dbContext,
        BlobServiceClient blobService,
        DurableTaskClient durableClient,
        ILogger<FindStalePendingListingsActivity> logger)
    {
        _dbContext = dbContext;
        _blobService = blobService;
        _durableClient = durableClient;
        _logger = logger;
    }

    [Function(nameof(FindStalePendingListingsActivity))]
    public async Task<FindStalePendingListingsResult> Run([ActivityTrigger] int scrapeRunId)
    {
        // Get all pending listings for this run
        var pendingListings = await _dbContext.ScrapeRunListings
            .Where(srl => srl.ScrapeRunId == scrapeRunId && srl.Status == "Pending")
            .Select(srl => srl.ListingId)
            .ToListAsync();

        if (pendingListings.Count == 0)
        {
            return new FindStalePendingListingsResult(new List<StaleListingInfo>());
        }

        var container = _blobService.GetBlobContainerClient("html");
        var staleListings = new List<StaleListingInfo>();

        foreach (var listingId in pendingListings)
        {
            // Check if listing blob exists
            var blobPath = $"{scrapeRunId}/{listingId}/listing.html";
            var blobClient = container.GetBlobClient(blobPath);
            var blobExists = await blobClient.ExistsAsync();

            if (!blobExists.Value)
            {
                // Blob doesn't exist yet, not stale - worker hasn't finished
                continue;
            }

            // Blob exists - check if orchestration exists
            var instanceId = $"etl-{scrapeRunId}-{listingId}";
            var instance = await _durableClient.GetInstanceAsync(instanceId);
            var orchestrationExists = instance != null;

            // If blob exists but no orchestration, it's stale
            if (!orchestrationExists)
            {
                _logger.LogInformation(
                    "Found stale listing: ScrapeRun {ScrapeRunId}, Listing {ListingId} - blob exists but no orchestration",
                    scrapeRunId, listingId);

                staleListings.Add(new StaleListingInfo(listingId, true, false));
            }
        }

        return new FindStalePendingListingsResult(staleListings);
    }
}
```

**Step 2: Build to verify**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj
```

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/FindStalePendingListingsActivity.cs
git commit -m "feat: add FindStalePendingListingsActivity for blob trigger recovery"
```

---

## Task 5: Create StartMissingOrchestrationActivity

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.Etl/Activities/StartMissingOrchestrationActivity.cs`

**Step 1: Create the activity**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Orchestrators;

namespace AIOMarketMaker.Etl.Activities;

public class StartMissingOrchestrationActivity
{
    private readonly DurableTaskClient _durableClient;
    private readonly ILogger<StartMissingOrchestrationActivity> _logger;

    public StartMissingOrchestrationActivity(
        DurableTaskClient durableClient,
        ILogger<StartMissingOrchestrationActivity> logger)
    {
        _durableClient = durableClient;
        _logger = logger;
    }

    [Function(nameof(StartMissingOrchestrationActivity))]
    public async Task<bool> Run([ActivityTrigger] StartOrchestrationInput input)
    {
        var instanceId = $"etl-{input.ScrapeRunId}-{input.ListingId}";

        // Check if already exists (race condition protection)
        var existing = await _durableClient.GetInstanceAsync(instanceId);
        if (existing != null)
        {
            _logger.LogInformation(
                "Orchestration {InstanceId} already exists with status {Status}, skipping",
                instanceId, existing.RuntimeStatus);
            return false;
        }

        // Start the ETL orchestration
        var etlInput = new ListingEtlInput(input.ScrapeRunId, input.ListingId, TriggerSource.Sweep);

        await _durableClient.ScheduleNewOrchestrationInstanceAsync(
            nameof(ListingEtlOrchestrator),
            etlInput,
            new StartOrchestrationOptions { InstanceId = instanceId });

        _logger.LogInformation(
            "Started missing orchestration {InstanceId} via sweep",
            instanceId);

        return true;
    }
}
```

**Step 2: Build to verify**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj
```

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/StartMissingOrchestrationActivity.cs
git commit -m "feat: add StartMissingOrchestrationActivity for sweep recovery"
```

---

## Task 6: Create SweepOrchestrator

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/SweepOrchestrator.cs`

**Step 1: Create the orchestrator**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Orchestrators;

public class SweepOrchestrator
{
    private const int PollIntervalSeconds = 60;
    private const int MaxIterations = 30; // 30 minutes max

    [Function(nameof(SweepOrchestrator))]
    public async Task Run([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<SweepOrchestrator>();
        var input = context.GetInput<SweepOrchestratorInput>()!;

        logger.LogInformation(
            "Starting sweep orchestrator for ScrapeRun {ScrapeRunId}",
            input.ScrapeRunId);

        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            // Wait before checking (except on first iteration)
            if (iteration > 0)
            {
                var nextCheck = context.CurrentUtcDateTime.AddSeconds(PollIntervalSeconds);
                await context.CreateTimer(nextCheck, CancellationToken.None);
            }

            // Check how many pending listings remain
            var pendingCount = await context.CallActivityAsync<int>(
                nameof(GetPendingCountActivity),
                input.ScrapeRunId);

            if (pendingCount == 0)
            {
                logger.LogInformation(
                    "Sweep complete for ScrapeRun {ScrapeRunId} - no pending listings remain",
                    input.ScrapeRunId);
                return;
            }

            logger.LogInformation(
                "Sweep iteration {Iteration} for ScrapeRun {ScrapeRunId}: {PendingCount} pending listings",
                iteration + 1, input.ScrapeRunId, pendingCount);

            // Find stale listings (blob exists but no orchestration)
            var result = await context.CallActivityAsync<FindStalePendingListingsResult>(
                nameof(FindStalePendingListingsActivity),
                input.ScrapeRunId);

            if (result.StaleListings.Count > 0)
            {
                logger.LogInformation(
                    "Found {Count} stale listings for ScrapeRun {ScrapeRunId}, starting orchestrations",
                    result.StaleListings.Count, input.ScrapeRunId);

                // Start missing orchestrations
                foreach (var staleListing in result.StaleListings)
                {
                    await context.CallActivityAsync<bool>(
                        nameof(StartMissingOrchestrationActivity),
                        new StartOrchestrationInput(input.ScrapeRunId, staleListing.ListingId));
                }
            }
        }

        logger.LogWarning(
            "Sweep reached max iterations ({MaxIterations}) for ScrapeRun {ScrapeRunId}, exiting",
            MaxIterations, input.ScrapeRunId);
    }
}
```

**Step 2: Build to verify**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj
```

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/SweepOrchestrator.cs
git commit -m "feat: add SweepOrchestrator for blob trigger recovery"
```

---

## Task 7: Integrate SweepOrchestrator into JobOrchestrator

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs`

**Step 1: Add using statement at top**

Add to the existing usings:
```csharp
using Microsoft.DurableTask.Client;
```

**Step 2: After SubmitScrapeJobsActivity (around line 178), spawn the sweep orchestrator**

Find this block (around line 173-178):
```csharp
// Step 7: Submit all scrape jobs (fire-and-forget)
var submitResult = await context.CallActivityAsync<SubmitScrapeJobsResult>(
    nameof(SubmitScrapeJobsActivity),
    new SubmitScrapeJobsInput(scrapeRunId, newListingIds));

logger.LogInformation("Job {JobId}: Submitted {Submitted} scrape jobs ({Failed} failed)",
    jobId, submitResult.SubmittedCount, submitResult.FailedCount);
```

Add after it:
```csharp
// Step 7b: Start sweep orchestrator to handle missed blob triggers
var sweepInstanceId = $"sweep-{scrapeRunId}";
await context.CallActivityAsync(
    nameof(StartSweepOrchestratorActivity),
    new StartSweepInput(scrapeRunId, sweepInstanceId));

logger.LogInformation("Job {JobId}: Started sweep orchestrator {SweepInstanceId}",
    jobId, sweepInstanceId);
```

**Step 3: Build to verify**

This will fail because StartSweepOrchestratorActivity doesn't exist yet. That's Task 8.

---

## Task 8: Create StartSweepOrchestratorActivity

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.Etl/Activities/StartSweepOrchestratorActivity.cs`
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Models/SweepModels.cs` (add input record)

**Step 1: Add StartSweepInput to SweepModels.cs**

Add to the end of the file:
```csharp
public record StartSweepInput(int ScrapeRunId, string InstanceId);
```

**Step 2: Create the activity**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Orchestrators;

namespace AIOMarketMaker.Etl.Activities;

public class StartSweepOrchestratorActivity
{
    private readonly DurableTaskClient _durableClient;
    private readonly ILogger<StartSweepOrchestratorActivity> _logger;

    public StartSweepOrchestratorActivity(
        DurableTaskClient durableClient,
        ILogger<StartSweepOrchestratorActivity> logger)
    {
        _durableClient = durableClient;
        _logger = logger;
    }

    [Function(nameof(StartSweepOrchestratorActivity))]
    public async Task Run([ActivityTrigger] StartSweepInput input)
    {
        // Check if sweep already running for this run (idempotency)
        var existing = await _durableClient.GetInstanceAsync(input.InstanceId);
        if (existing != null &&
            (existing.RuntimeStatus == OrchestrationRuntimeStatus.Running ||
             existing.RuntimeStatus == OrchestrationRuntimeStatus.Pending))
        {
            _logger.LogInformation(
                "Sweep orchestrator {InstanceId} already running, skipping",
                input.InstanceId);
            return;
        }

        // Start the sweep orchestrator
        await _durableClient.ScheduleNewOrchestrationInstanceAsync(
            nameof(SweepOrchestrator),
            new SweepOrchestratorInput(input.ScrapeRunId),
            new StartOrchestrationOptions { InstanceId = input.InstanceId });

        _logger.LogInformation(
            "Started sweep orchestrator {InstanceId} for ScrapeRun {ScrapeRunId}",
            input.InstanceId, input.ScrapeRunId);
    }
}
```

**Step 3: Build to verify**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj
```

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/StartSweepOrchestratorActivity.cs
git add AIOMarketMaker/AIOMarketMaker.Etl/Models/SweepModels.cs
git add AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs
git commit -m "feat: integrate SweepOrchestrator into JobOrchestrator"
```

---

## Task 9: Build and Test

**Step 1: Build entire solution**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.sln
```

**Step 2: Run unit tests**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit"
```

**Step 3: Manual integration test**

1. Restart ETL with the new code:
   ```bash
   # Kill existing func processes
   pkill -f "func start"

   # Restart ETL
   cd AIOMarketMaker/AIOMarketMaker.Etl && func start --port 7072 > /dev/null 2>&1 &
   ```

2. Trigger a scrape job via the UI

3. Monitor with `/monitor-scrape` - verify:
   - Sweep orchestrator starts after job submission
   - Any missed blob triggers are recovered
   - All listings complete

**Step 4: Final commit (if any fixes needed)**

```bash
git add -A
git commit -m "fix: address integration issues in sweep orchestrator"
```
