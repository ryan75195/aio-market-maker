# Real-Time Progress - ETL Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Move archived scrape orchestration code to ETL project and wire up for event-driven processing with progress tracking.

**Architecture:** Move existing Durable Functions activities/orchestrators from `_archived/` to ETL, update namespaces, add HTTP trigger for `/scrape/start`, and update `ProcessListingActivity` to increment progress.

**Tech Stack:** .NET 8, Azure Functions v4, Durable Functions, Azure Blob Storage

---

## Phase 1: Move Archived Activities to ETL

### Task 1.1: Move core activities from archived to ETL

**Files:**
- Move: `AIOMarketMaker.Functions/_archived/Activities/GetEnabledJobsActivity.cs` → `AIOMarketMaker.Etl/Activities/`
- Move: `AIOMarketMaker.Functions/_archived/Activities/ParseSearchPageActivity.cs` → `AIOMarketMaker.Etl/Activities/`
- Move: `AIOMarketMaker.Functions/_archived/Activities/FilterNewListingsActivity.cs` → `AIOMarketMaker.Etl/Activities/`
- Move: `AIOMarketMaker.Functions/_archived/Activities/SubmitScrapeJobActivity.cs` → `AIOMarketMaker.Etl/Activities/`
- Move: `AIOMarketMaker.Functions/_archived/Activities/UpdateScrapeRunActivity.cs` → `AIOMarketMaker.Etl/Activities/`
- Move: `AIOMarketMaker.Functions/_archived/Activities/UpdateScrapeRunProgressActivity.cs` → `AIOMarketMaker.Etl/Activities/`

**Step 1: Copy files**

```bash
cp AIOMarketMaker.Functions/_archived/Activities/GetEnabledJobsActivity.cs AIOMarketMaker.Etl/Activities/
cp AIOMarketMaker.Functions/_archived/Activities/ParseSearchPageActivity.cs AIOMarketMaker.Etl/Activities/
cp AIOMarketMaker.Functions/_archived/Activities/FilterNewListingsActivity.cs AIOMarketMaker.Etl/Activities/
cp AIOMarketMaker.Functions/_archived/Activities/SubmitScrapeJobActivity.cs AIOMarketMaker.Etl/Activities/
cp AIOMarketMaker.Functions/_archived/Activities/UpdateScrapeRunActivity.cs AIOMarketMaker.Etl/Activities/
cp AIOMarketMaker.Functions/_archived/Activities/UpdateScrapeRunProgressActivity.cs AIOMarketMaker.Etl/Activities/
```

**Step 2: Update namespaces in each file**

Change `namespace AIOMarketMaker.Functions.Activities;` to `namespace AIOMarketMaker.Etl.Activities;`

**Step 3: Fix any using statements**

Update imports to reference `AIOMarketMaker.Etl` instead of `AIOMarketMaker.Functions` where applicable.

**Step 4: Verify build**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`

**Step 5: Commit**

```bash
git add AIOMarketMaker.Etl/Activities/
git commit -m "feat: move archived activities to ETL project"
```

---

### Task 1.2: Move orchestrators from archived to ETL

**Files:**
- Move: `AIOMarketMaker.Functions/_archived/Orchestrators/ScrapeOrchestrator.cs` → `AIOMarketMaker.Etl/Orchestrators/`
- Move: `AIOMarketMaker.Functions/_archived/Orchestrators/JobOrchestrator.cs` → `AIOMarketMaker.Etl/Orchestrators/`

**Step 1: Copy files**

```bash
cp AIOMarketMaker.Functions/_archived/Orchestrators/ScrapeOrchestrator.cs AIOMarketMaker.Etl/Orchestrators/
cp AIOMarketMaker.Functions/_archived/Orchestrators/JobOrchestrator.cs AIOMarketMaker.Etl/Orchestrators/
```

**Step 2: Update namespaces**

Change `namespace AIOMarketMaker.Functions.Functions.Orchestrators;` to `namespace AIOMarketMaker.Etl.Orchestrators;`

**Step 3: Update activity references**

Change `AIOMarketMaker.Functions.Activities` imports to `AIOMarketMaker.Etl.Activities`

**Step 4: Verify build**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`

**Step 5: Commit**

```bash
git add AIOMarketMaker.Etl/Orchestrators/
git commit -m "feat: move archived orchestrators to ETL project"
```

---

### Task 1.3: Move contracts/models from archived

**Files:**
- Check: `AIOMarketMaker.Functions/_archived/Contracts/OrchestratorContracts.cs`
- Move if needed to: `AIOMarketMaker.Etl/Models/`

**Step 1: Check what contracts exist**

Read the contracts file to see what input/output types are needed.

**Step 2: Copy and rename if needed**

```bash
cp AIOMarketMaker.Functions/_archived/Contracts/OrchestratorContracts.cs AIOMarketMaker.Etl/Models/OrchestratorContracts.cs
```

**Step 3: Update namespace**

Change to `namespace AIOMarketMaker.Etl.Models;`

**Step 4: Verify build**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`

**Step 5: Commit**

```bash
git add AIOMarketMaker.Etl/Models/
git commit -m "feat: move orchestrator contracts to ETL project"
```

---

## Phase 2: Add HTTP Trigger for Scrape Start

### Task 2.1: Create StartScrapeTrigger

**Files:**
- Create: `AIOMarketMaker.Etl/Triggers/StartScrapeTrigger.cs`
- Reference: `AIOMarketMaker.Functions/_archived/Functions/ManualScrapeTrigger.cs`

**Step 1: Create the trigger file**

Adapt from archived `ManualScrapeTrigger.cs`:

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using AIOMarketMaker.Etl.Orchestrators;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Etl.Triggers;

public class StartScrapeTrigger
{
    private readonly ILogger<StartScrapeTrigger> _logger;
    private readonly EtlDbContext _dbContext;

    public StartScrapeTrigger(ILogger<StartScrapeTrigger> logger, EtlDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    [Function("StartScrape")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "scrape/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        _logger.LogInformation("Scrape start trigger fired at {Time}", DateTime.UtcNow);

        // Parse optional request body
        StartScrapeRequest? scrapeRequest = null;
        var requestBody = await req.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(requestBody))
        {
            try
            {
                scrapeRequest = JsonSerializer.Deserialize<StartScrapeRequest>(requestBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse request body, using defaults");
            }
        }

        // Create ScrapeRun record
        var scrapeRun = new ScrapeRun
        {
            InstanceId = null, // Will be set after orchestration starts
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            Status = "Running"
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        // Start orchestration with runId
        var orchestratorInput = new ScrapeOrchestratorInput(
            scrapeRun.Id,
            scrapeRequest?.MaxListingsToFetch,
            scrapeRequest?.LookbackDays);

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(ScrapeOrchestrator), orchestratorInput);

        // Update ScrapeRun with instanceId
        scrapeRun.InstanceId = instanceId;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Started orchestration {InstanceId} for run {RunId}", instanceId, scrapeRun.Id);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { runId = scrapeRun.Id, instanceId });
        return response;
    }
}

public record StartScrapeRequest(int? MaxListingsToFetch, int? LookbackDays);
```

**Step 2: Verify build**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`

**Step 3: Commit**

```bash
git add AIOMarketMaker.Etl/Triggers/StartScrapeTrigger.cs
git commit -m "feat: add HTTP trigger for /scrape/start"
```

---

## Phase 3: Wire Up Dependencies

### Task 3.1: Update ETL Program.cs with required services

**Files:**
- Modify: `AIOMarketMaker.Etl/Program.cs`

**Step 1: Add missing service registrations**

The orchestrators and activities need these services:
- `IEbayScraper`
- `IEbayUrlBuilder`
- `ISearchParser`
- `IListingParser`
- `IWebscraperClient`
- `IJobRepository`

Check what's already registered and add missing ones from `AIOMarketMaker.Core.ScraperServiceCollectionExtensions`.

**Step 2: Verify build**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`

**Step 3: Commit**

```bash
git add AIOMarketMaker.Etl/Program.cs
git commit -m "feat: wire up scraper services in ETL DI"
```

---

## Phase 4: Update ProcessListingActivity for Progress Tracking

### Task 4.1: Add progress increment to ProcessListingActivity

**Files:**
- Modify: `AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs`

**Step 1: Add progress increment after SaveChangesAsync**

Add this after `await _dbContext.SaveChangesAsync();`:

```csharp
// Increment progress and check completion atomically
// Note: JobId is the ScrapeRun.Id as a string
if (int.TryParse(input.JobId, out var scrapeRunId))
{
    await _dbContext.Database.ExecuteSqlRawAsync(@"
        UPDATE ScrapeRuns
        SET ListingsProcessed = ListingsProcessed + 1,
            Status = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                          AND Status = 'Running' THEN 'Completed' ELSE Status END,
            CompletedUtc = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                AND Status = 'Running' THEN datetime('now') ELSE CompletedUtc END
        WHERE Id = {0}", scrapeRunId);
}
```

**Step 2: Add using statement**

Add `using Microsoft.EntityFrameworkCore;` if not present.

**Step 3: Verify build**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`

**Step 4: Commit**

```bash
git add AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs
git commit -m "feat: add progress increment to ProcessListingActivity"
```

---

## Phase 5: Update Orchestrators for New Architecture

### Task 5.1: Update ScrapeOrchestrator to use ScrapeRun.Id as jobId

**Files:**
- Modify: `AIOMarketMaker.Etl/Orchestrators/ScrapeOrchestrator.cs`

**Step 1: Review and update orchestrator**

Ensure the orchestrator:
1. Receives `ScrapeOrchestratorInput` with `ScrapeRunId`
2. Uses `ScrapeRunId.ToString()` as the scraper jobId when submitting URLs
3. Updates `TotalListingsFound` after filtering
4. Submits URLs with `GroupId=listingId`, `FileKey="listing"/"description"`

**Step 2: Verify build**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`

**Step 3: Commit**

```bash
git add AIOMarketMaker.Etl/Orchestrators/
git commit -m "feat: update ScrapeOrchestrator for event-driven architecture"
```

---

## Phase 6: Verification

### Task 6.1: Build and run locally

**Step 1: Build entire solution**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.sln
```

**Step 2: Start Azurite**

```bash
npx azurite --blobPort 10000 --queuePort 10001 --tablePort 10002
```

**Step 3: Start ETL Functions**

```bash
cd AIOMarketMaker/AIOMarketMaker.Etl && func start --port 7072
```

**Step 4: Verify endpoints registered**

Check func output shows:
- `StartScrape: [POST] http://localhost:7072/api/scrape/start`
- `ScrapeOrchestrator: orchestrationTrigger`
- `OnListingBlobCreated: blobTrigger`
- `OnDescriptionBlobCreated: blobTrigger`

**Step 5: Commit any final fixes**

```bash
git add -A
git commit -m "fix: resolve build issues for ETL scrape orchestration"
```

---

## Summary of Changes

| Task | Description |
|------|-------------|
| 1.1 | Move 6 activities from `_archived/` to ETL |
| 1.2 | Move 2 orchestrators from `_archived/` to ETL |
| 1.3 | Move contracts/models from `_archived/` to ETL |
| 2.1 | Create `StartScrapeTrigger.cs` for `/api/scrape/start` |
| 3.1 | Wire up scraper services in ETL DI |
| 4.1 | Add progress increment to `ProcessListingActivity` |
| 5.1 | Update orchestrators for new architecture |
| 6.1 | Build and verify locally |
