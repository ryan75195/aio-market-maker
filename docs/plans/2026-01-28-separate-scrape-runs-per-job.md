# Separate ScrapeRuns Per Job Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Each enabled ScrapeJob gets its own ScrapeRun record, eliminating phase/progress overwrites when multiple jobs run.

**Architecture:** Replace the single `ScrapeOrchestrator` → multiple `JobOrchestrator` pattern with direct `JobOrchestrator` invocations from the trigger. Each job creates its own ScrapeRun with isolated progress tracking. The `ScrapeOrchestrator` becomes optional (for backward compatibility with nightly runs) or removed entirely.

**Tech Stack:** .NET 8, Durable Functions, EF Core, SQLite

---

## Current Architecture (Problem)

```
StartScrapeTrigger
  └─> Creates 1 ScrapeRun
  └─> Starts ScrapeOrchestrator (instanceId = "scrape-run-{runId}")
        └─> Gets all enabled jobs
        └─> For each job:
              └─> JobOrchestrator (shares same scrapeInstanceId)
                    └─> Sets CurrentPhase = "Searching" (OVERWRITES!)
                    └─> Updates TotalListingsFound (OVERWRITES!)
```

## New Architecture (Solution)

```
StartScrapeTrigger
  └─> Gets all enabled jobs
  └─> For each job:
        └─> Creates ScrapeRun (with JobId)
        └─> Starts JobOrchestrator (instanceId = "scrape-run-{runId}")
              └─> Has isolated CurrentPhase, TotalListingsFound
```

---

## Database Changes

### Task 1: Add JobId column to ScrapeRun

**Files:**
- Create: `AIOMarketMaker.Core/Data/Migrations/023_AddJobIdToScrapeRuns.sql`
- Modify: `AIOMarketMaker.Core/Data/Models/ScrapeRun.cs:9-65`

**Step 1: Create migration file**

Create `AIOMarketMaker.Core/Data/Migrations/023_AddJobIdToScrapeRuns.sql`:

```sql
-- Migration: 023_AddJobIdToScrapeRuns
-- Description: Adds JobId column to ScrapeRuns for per-job tracking
-- Date: 2026-01-28

ALTER TABLE ScrapeRuns ADD COLUMN JobId INTEGER NULL;

-- Create index for filtering by job
CREATE INDEX IF NOT EXISTS IX_ScrapeRuns_JobId ON ScrapeRuns (JobId);

-- Add foreign key constraint (SQLite doesn't enforce, but documents intent)
-- FOREIGN KEY (JobId) REFERENCES ScrapeJobs(Id)
```

**Step 2: Update ScrapeRun model**

Add to `ScrapeRun.cs` after line 19 (`TriggerType`):

```csharp
/// <summary>
/// The scrape job this run is for (null for legacy runs that processed all jobs)
/// </summary>
public int? JobId { get; set; }
```

**Step 3: Verify migration applies**

Run: `dotnet run --project AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Migration runs, app starts normally

**Step 4: Commit**

```bash
git add AIOMarketMaker.Core/Data/Migrations/023_AddJobIdToScrapeRuns.sql AIOMarketMaker.Core/Data/Models/ScrapeRun.cs
git commit -m "feat: add JobId column to ScrapeRuns for per-job tracking"
```

---

## Trigger Changes

### Task 2: Modify StartScrapeTrigger to create per-job runs

**Files:**
- Modify: `AIOMarketMaker.Etl/Triggers/StartScrapeTrigger.cs:26-81`
- Test: `AIOMarketMaker.Tests/UnitTests/Triggers/StartScrapeTriggerTests.cs` (new)

**Step 1: Write the failing test**

Create `AIOMarketMaker.Tests/UnitTests/Triggers/StartScrapeTriggerTests.cs`:

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Tests.Utils;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Tests.UnitTests.Triggers;

[TestFixture]
[Category("Unit")]
public class StartScrapeTriggerTests
{
    [Test]
    public async Task Should_create_separate_scrape_run_for_each_enabled_job()
    {
        // Arrange
        using var dbContext = InMemoryDbContextFactory.Create();

        // Add 2 enabled jobs
        dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 1, SearchTerm = "PS5", IsEnabled = true });
        dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 2, SearchTerm = "Xbox", IsEnabled = true });
        dbContext.ScrapeJobs.Add(new ScrapeJob { Id = 3, SearchTerm = "Disabled", IsEnabled = false });
        await dbContext.SaveChangesAsync();

        // Act - simulate what the trigger should do
        var enabledJobs = await dbContext.ScrapeJobs.Where(j => j.IsEnabled).ToListAsync();
        foreach (var job in enabledJobs)
        {
            var run = new ScrapeRun
            {
                JobId = job.Id,
                TriggerType = "Manual",
                StartedUtc = DateTime.UtcNow,
                Status = "Running"
            };
            dbContext.ScrapeRuns.Add(run);
        }
        await dbContext.SaveChangesAsync();

        // Assert
        var runs = await dbContext.ScrapeRuns.ToListAsync();
        Assert.That(runs, Has.Count.EqualTo(2), "Should create 2 runs for 2 enabled jobs");
        Assert.That(runs.Select(r => r.JobId), Is.EquivalentTo(new[] { 1, 2 }));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~StartScrapeTriggerTests"`
Expected: PASS (this is a behavior test, not testing existing code)

**Step 3: Rewrite StartScrapeTrigger**

Replace `StartScrapeTrigger.cs` Run method (lines 26-81):

```csharp
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

    // Get all enabled jobs
    var enabledJobs = await _dbContext.ScrapeJobs
        .Where(j => j.IsEnabled)
        .Select(j => new { j.Id, j.SearchTerm })
        .ToListAsync();

    if (enabledJobs.Count == 0)
    {
        _logger.LogInformation("No enabled jobs found");
        var noJobsResponse = req.CreateResponse(HttpStatusCode.OK);
        await noJobsResponse.WriteAsJsonAsync(new { message = "No enabled jobs", runs = Array.Empty<object>() });
        return noJobsResponse;
    }

    // Create a ScrapeRun for each enabled job
    var startedRuns = new List<object>();
    foreach (var job in enabledJobs)
    {
        var scrapeRun = new ScrapeRun
        {
            JobId = job.Id,
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            Status = "Running"
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        // Start orchestration for this job
        var instanceId = $"scrape-run-{scrapeRun.Id}";
        var orchestratorInput = new JobOrchestratorInput(
            job.Id,
            instanceId,
            scrapeRequest?.MaxListingsToFetch,
            scrapeRequest?.LookbackDays);

        await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(JobOrchestrator), orchestratorInput,
            new StartOrchestrationOptions { InstanceId = instanceId });

        scrapeRun.InstanceId = instanceId;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Started JobOrchestrator {InstanceId} for job {JobId}: {SearchTerm}",
            instanceId, job.Id, job.SearchTerm);

        startedRuns.Add(new { runId = scrapeRun.Id, instanceId, jobId = job.Id, searchTerm = job.SearchTerm });
    }

    var response = req.CreateResponse(HttpStatusCode.Accepted);
    await response.WriteAsJsonAsync(new { runs = startedRuns });
    return response;
}
```

**Step 4: Add required using**

Add at top of file if not present:
```csharp
using AIOMarketMaker.Etl.Orchestrators;
using Microsoft.EntityFrameworkCore;
```

**Step 5: Run tests**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit"`
Expected: All pass

**Step 6: Commit**

```bash
git add AIOMarketMaker.Etl/Triggers/StartScrapeTrigger.cs AIOMarketMaker.Tests/UnitTests/Triggers/StartScrapeTriggerTests.cs
git commit -m "feat: start separate orchestration per job in StartScrapeTrigger"
```

---

## JobOrchestrator Changes

### Task 3: Make JobOrchestrator work standalone (not as sub-orchestrator)

**Files:**
- Modify: `AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs:16-177`

**Step 1: Verify current JobOrchestrator signature**

Current: Takes `JobOrchestratorInput` which includes `ScrapeInstanceId`
This already works! The `ScrapeInstanceId` IS the orchestration's `context.InstanceId`.

No changes needed to JobOrchestrator itself - it already uses `scrapeInstanceId` correctly.

**Step 2: Commit (no-op, document decision)**

```bash
git commit --allow-empty -m "docs: JobOrchestrator already supports standalone operation"
```

---

## Update History API

### Task 4: Include JobId and SearchTerm in history response

**Files:**
- Modify: `AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs:249-275`

**Step 1: Update GetHistory to include job info**

Replace lines 253-271 in `ScrapeJobsApi.cs`:

```csharp
var runs = await _dbContext.ScrapeRuns
    .OrderByDescending(r => r.StartedUtc)
    .Take(50)
    .Select(r => new
    {
        r.Id,
        r.InstanceId,
        r.JobId,
        JobSearchTerm = r.JobId != null
            ? _dbContext.ScrapeJobs.Where(j => j.Id == r.JobId).Select(j => j.SearchTerm).FirstOrDefault()
            : null,
        r.TriggerType,
        r.StartedUtc,
        r.CompletedUtc,
        r.Status,
        r.ListingsAdded,
        r.ListingsSkipped,
        r.TotalListingsFound,
        r.ListingsProcessed,
        r.CurrentPhase,
        r.ErrorMessage
    })
    .ToListAsync();
```

**Step 2: Build and verify**

Run: `dotnet build AIOMarketMaker.Functions/AIOMarketMaker.Functions.csproj`
Expected: Build succeeds

**Step 3: Commit**

```bash
git add AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs
git commit -m "feat: include JobId and SearchTerm in history API response"
```

---

## Update NightlyScrapeTrigger

### Task 5: Update nightly trigger to create per-job runs

**Files:**
- Modify: `AIOMarketMaker.Etl/Triggers/NightlyScrapeTrigger.cs`

**Step 1: Read current implementation**

Check current implementation and apply same pattern as StartScrapeTrigger.

**Step 2: Update to match StartScrapeTrigger pattern**

Apply the same per-job orchestration pattern used in Task 2.

**Step 3: Commit**

```bash
git add AIOMarketMaker.Etl/Triggers/NightlyScrapeTrigger.cs
git commit -m "feat: nightly trigger creates separate run per job"
```

---

## Cleanup (Optional)

### Task 6: Remove or deprecate ScrapeOrchestrator

**Files:**
- Archive: `AIOMarketMaker.Etl/Orchestrators/ScrapeOrchestrator.cs`

**Step 1: Move to _archived folder**

```bash
mkdir -p AIOMarketMaker.Etl/Orchestrators/_archived
mv AIOMarketMaker.Etl/Orchestrators/ScrapeOrchestrator.cs AIOMarketMaker.Etl/Orchestrators/_archived/
```

**Step 2: Verify build**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeds (ScrapeOrchestrator no longer referenced)

**Step 3: Commit**

```bash
git add -A
git commit -m "refactor: archive ScrapeOrchestrator, now using direct JobOrchestrator invocations"
```

---

## Integration Test

### Task 7: Manual integration test

**Steps:**
1. Start local environment: `/setup-local-env start`
2. Verify 2 jobs are enabled: `curl http://localhost:7071/api/jobs`
3. Trigger scrape: `curl -X POST http://localhost:7071/api/scrape/start`
4. Verify response shows 2 separate runs with different jobIds
5. Monitor history: `curl http://localhost:7071/api/history`
6. Verify each run has independent phase/progress tracking
7. Wait for completion, verify no phase overwrites

**Expected Output:**
```json
{
  "runs": [
    { "runId": 1, "instanceId": "scrape-run-1", "jobId": 1, "searchTerm": "PlayStation 5 Console" },
    { "runId": 2, "instanceId": "scrape-run-2", "jobId": 2, "searchTerm": "Glasses" }
  ]
}
```

---

## Summary

| Task | Description | Files Changed |
|------|-------------|---------------|
| 1 | Add JobId to ScrapeRun | Migration + Model |
| 2 | StartScrapeTrigger per-job | Trigger + Test |
| 3 | Verify JobOrchestrator | No changes needed |
| 4 | History API with job info | API |
| 5 | NightlyScrapeTrigger per-job | Trigger |
| 6 | Archive ScrapeOrchestrator | Cleanup |
| 7 | Integration test | Manual verification |

**Estimated effort:** 6 tasks, ~30-45 minutes with TDD
