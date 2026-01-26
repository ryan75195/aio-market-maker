# Progress Tracking Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add progress tracking fields to ScrapeRun so the UI can show real-time scrape progress.

**Architecture:** Add 3 new fields to ScrapeRun (TotalListingsFound, ListingsProcessed, CurrentPhase). Create a new activity to update progress during orchestration. Update the orchestrators to call the progress activity at key milestones. Update the API and UI to display progress.

**Tech Stack:** .NET 8, EF Core (SQLite), Azure Durable Functions, Vue.js

---

## Current Flow Analysis

The orchestration flow is:
1. `ScrapeOrchestrator` - Main orchestrator, iterates through jobs sequentially
2. `JobOrchestrator` - Processes one job: searches sold/active, filters new listings, fetches in batches
3. `FetchListingOrchestrator` - Fetches one listing + description

Currently `UpdateScrapeRunActivity` is only called at the END of `ScrapeOrchestrator` with final counts.

## New Fields

| Field | Type | Description |
|-------|------|-------------|
| `TotalListingsFound` | int | Total unique listings discovered in search phase |
| `ListingsProcessed` | int | Number of listings fully processed (saved to DB) |
| `CurrentPhase` | string | "Searching Sold", "Searching Active", "Filtering", "Fetching", "Saving", "Completed" |

---

### Task 1: Add Progress Fields to ScrapeRun Model

**Files:**
- Modify: `AIOMarketMaker.Core/Data/Models/ScrapeRun.cs`

**Step 1: Add properties to ScrapeRun model**

```csharp
/// <summary>
/// Total unique listings found during search phase
/// </summary>
public int TotalListingsFound { get; set; }

/// <summary>
/// Number of listings fully processed (fetched and saved)
/// </summary>
public int ListingsProcessed { get; set; }

/// <summary>
/// Current phase of the orchestration: "Searching Sold", "Searching Active", "Filtering", "Fetching", "Saving", "Completed"
/// </summary>
public string? CurrentPhase { get; set; }
```

**Step 2: Verify build succeeds**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker.Core/Data/Models/ScrapeRun.cs
git commit -m "feat: add progress tracking fields to ScrapeRun model"
```

---

### Task 2: Create Migration for New Columns

**Files:**
- Create: `AIOMarketMaker.Core/Data/Migrations/020_AddProgressFieldsToScrapeRuns.sql`

**Step 1: Create migration file**

```sql
-- Migration: 020_AddProgressFieldsToScrapeRuns
-- Description: Adds progress tracking fields to ScrapeRuns table
-- Date: 2026-01-26

ALTER TABLE ScrapeRuns ADD COLUMN TotalListingsFound INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ScrapeRuns ADD COLUMN ListingsProcessed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE ScrapeRuns ADD COLUMN CurrentPhase TEXT;
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Core/Data/Migrations/020_AddProgressFieldsToScrapeRuns.sql
git commit -m "feat: add migration for progress tracking columns"
```

---

### Task 3: Create UpdateScrapeRunProgressActivity

**Files:**
- Create: `AIOMarketMaker.Functions/Activities/UpdateScrapeRunProgressActivity.cs`

**Step 1: Create the activity**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Functions.Activities;

public class UpdateScrapeRunProgressActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<UpdateScrapeRunProgressActivity> _logger;

    public UpdateScrapeRunProgressActivity(EtlDbContext dbContext, ILogger<UpdateScrapeRunProgressActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(UpdateScrapeRunProgressActivity))]
    public async Task Run([ActivityTrigger] UpdateProgressInput input)
    {
        var run = await _dbContext.ScrapeRuns
            .FirstOrDefaultAsync(r => r.InstanceId == input.InstanceId);

        if (run == null)
        {
            _logger.LogWarning("ScrapeRun not found for instance {InstanceId}", input.InstanceId);
            return;
        }

        if (input.TotalListingsFound.HasValue)
            run.TotalListingsFound = input.TotalListingsFound.Value;

        if (input.ListingsProcessed.HasValue)
            run.ListingsProcessed = input.ListingsProcessed.Value;

        if (input.CurrentPhase != null)
            run.CurrentPhase = input.CurrentPhase;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Progress updated for {InstanceId}: Phase={Phase}, Found={Found}, Processed={Processed}",
            input.InstanceId, run.CurrentPhase, run.TotalListingsFound, run.ListingsProcessed);
    }
}

public record UpdateProgressInput(
    string InstanceId,
    int? TotalListingsFound = null,
    int? ListingsProcessed = null,
    string? CurrentPhase = null);
```

**Step 2: Verify build succeeds**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Functions/AIOMarketMaker.Functions.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker.Functions/Activities/UpdateScrapeRunProgressActivity.cs
git commit -m "feat: add UpdateScrapeRunProgressActivity for progress updates"
```

---

### Task 4: Update JobOrchestrator to Report Progress

**Files:**
- Modify: `AIOMarketMaker.Functions/Functions/Orchestrators/JobOrchestrator.cs`
- Modify: `AIOMarketMaker.Functions/Contracts/JobResult.cs` (add MainInstanceId property)

**Step 1: Add MainInstanceId to JobDetails so sub-orchestrator can report progress**

The `JobOrchestrator` needs to know the main ScrapeOrchestrator's instanceId to report progress.
We'll pass it via the input.

First, check/update `JobOrchestrator` input to accept `ScrapeInstanceId`:

```csharp
// JobOrchestrator input changes from int (jobId) to a record
public record JobOrchestratorInput(int JobId, string ScrapeInstanceId);
```

**Step 2: Add progress calls to JobOrchestrator**

At key points in JobOrchestrator:
- Before searching sold: `CurrentPhase = "Searching Sold"`
- Before searching active: `CurrentPhase = "Searching Active"`
- After filtering: `CurrentPhase = "Fetching"` with `TotalListingsFound = newListingIds.Count`
- After each batch: `ListingsProcessed += batchSuccessCount`

**Step 3: Update ScrapeOrchestrator to pass instanceId**

Change the call from:
```csharp
var result = await context.CallSubOrchestratorAsync<JobResult>(
    nameof(JobOrchestrator), job.Id);
```

To:
```csharp
var result = await context.CallSubOrchestratorAsync<JobResult>(
    nameof(JobOrchestrator),
    new JobOrchestratorInput(job.Id, context.InstanceId));
```

**Step 4: Verify build succeeds**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Functions/AIOMarketMaker.Functions.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add AIOMarketMaker.Functions/Functions/Orchestrators/JobOrchestrator.cs
git add AIOMarketMaker.Functions/Functions/Orchestrators/ScrapeOrchestrator.cs
git add AIOMarketMaker.Functions/Contracts/
git commit -m "feat: add progress reporting to JobOrchestrator"
```

---

### Task 5: Update GetHistory API to Include Progress Fields

**Files:**
- Modify: `AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs`

**Step 1: Add progress fields to GetHistory response**

Update the `Select` in `GetHistory` to include the new fields:

```csharp
.Select(r => new
{
    r.Id,
    r.InstanceId,
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
```

**Step 2: Verify build succeeds**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Functions/AIOMarketMaker.Functions.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs
git commit -m "feat: include progress fields in history API response"
```

---

### Task 6: Update Electron UI to Display Progress

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/index.html`
- Modify: `AIOMarketMaker.Desktop/electron/src/styles.css`

**Step 1: Update history table to show progress**

Replace the History view table with progress columns:

```html
<table class="data-table">
  <thead>
    <tr>
      <th>Started</th>
      <th>Trigger</th>
      <th>Status</th>
      <th>Phase</th>
      <th>Progress</th>
      <th>Listings Added</th>
    </tr>
  </thead>
  <tbody>
    <tr v-for="run in history" :key="run.id" :class="{ 'error-row': run.status === 'Failed' }">
      <td>{{ formatDate(run.startedUtc) }}</td>
      <td>
        <span class="trigger-badge" :class="run.triggerType.toLowerCase()">{{ run.triggerType }}</span>
      </td>
      <td>
        <span class="status-badge" :class="run.status.toLowerCase()">{{ run.status }}</span>
      </td>
      <td>{{ run.currentPhase || '-' }}</td>
      <td>
        <div v-if="run.status === 'Running' && run.totalListingsFound > 0" class="progress-bar">
          <div class="progress-fill" :style="{ width: progressPercent(run) + '%' }"></div>
          <span class="progress-text">{{ run.listingsProcessed }}/{{ run.totalListingsFound }}</span>
        </div>
        <span v-else>-</span>
      </td>
      <td class="number">{{ run.listingsAdded }}</td>
    </tr>
    <tr v-if="history.length === 0">
      <td colspan="6" class="empty">No run history</td>
    </tr>
  </tbody>
</table>
```

**Step 2: Add progressPercent method to app.js**

```javascript
progressPercent(run) {
  if (!run.totalListingsFound || run.totalListingsFound === 0) return 0;
  return Math.round((run.listingsProcessed / run.totalListingsFound) * 100);
}
```

**Step 3: Add CSS for progress bar**

```css
.progress-bar {
  position: relative;
  background: #333;
  border-radius: 4px;
  height: 20px;
  min-width: 100px;
  overflow: hidden;
}

.progress-fill {
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  background: linear-gradient(90deg, #4a9eff, #2d7ad6);
  transition: width 0.3s ease;
}

.progress-text {
  position: relative;
  z-index: 1;
  display: block;
  text-align: center;
  font-size: 0.75rem;
  line-height: 20px;
  color: #fff;
}
```

**Step 4: Add auto-refresh for running jobs in app.js**

```javascript
// In mounted()
this.startAutoRefresh();

// Add new methods
startAutoRefresh() {
  this.refreshInterval = setInterval(() => {
    if (this.currentView === 'history' && this.history.some(r => r.status === 'Running')) {
      this.loadHistory();
    }
  }, 5000);
},

// In beforeUnmount or add cleanup
beforeUnmount() {
  if (this.refreshInterval) {
    clearInterval(this.refreshInterval);
  }
}
```

**Step 5: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/index.html
git add AIOMarketMaker.Desktop/electron/src/app.js
git add AIOMarketMaker.Desktop/electron/src/styles.css
git commit -m "feat: add progress display to history UI"
```

---

### Task 7: Test End-to-End

**Step 1: Run the migration**

Start AIOMarketMaker.Functions locally - migration runs on startup.

Run: `func start --cwd AIOMarketMaker/AIOMarketMaker.Functions`
Expected: Migration 020_AddProgressFieldsToScrapeRuns applied

**Step 2: Start a manual scrape and verify progress updates**

1. Open Electron UI
2. Go to History tab
3. Click "Start Scrape" in Operations
4. Observe History table auto-refreshing with:
   - CurrentPhase changing: "Searching Sold" → "Searching Active" → "Fetching" → "Completed"
   - Progress bar filling as ListingsProcessed increases
   - Final ListingsAdded count populated

**Step 3: Verify completed runs show final state**

Completed runs should show:
- Status: "Completed"
- Phase: "Completed" or null
- Progress: "-" (no bar since complete)
- ListingsAdded: actual count

---

## Summary of Changes

1. **Model**: Add 3 fields to `ScrapeRun`
2. **Migration**: Add columns to SQLite table
3. **Activity**: New `UpdateScrapeRunProgressActivity` for incremental updates
4. **Orchestrators**: `JobOrchestrator` calls progress activity at milestones
5. **API**: `GetHistory` includes progress fields
6. **UI**: History table shows phase + progress bar with auto-refresh
