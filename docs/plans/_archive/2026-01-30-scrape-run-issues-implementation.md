# Scrape Run Issues UI - Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Surface partial failures (like missing descriptions) in the history UI with expandable issue details per run.

**Architecture:** New `ScrapeRunIssues` table stores fetch failures. History API includes issue counts. ListingEtlOrchestrator records issues when blobs are missing after retries. UI shows expandable issue details.

**Tech Stack:** SQL Server, EF Core, Azure Functions (isolated worker), Vue.js/HTML

---

## Task 1: Database Migration

**Files:**
- Create: `AIOMarketMaker.Core/Data/Migrations/SqlServer/027_CreateScrapeRunIssuesTable.sql`

**Step 1: Create the migration file**

```sql
-- Migration: 027_CreateScrapeRunIssuesTable
-- Description: Creates ScrapeRunIssues table to track fetch failures
-- Date: 2026-01-30

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'ScrapeRunIssues')
BEGIN
    CREATE TABLE ScrapeRunIssues (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        ScrapeRunId INT NOT NULL,
        ListingId NVARCHAR(50) NOT NULL,
        IssueType NVARCHAR(50) NOT NULL,
        ErrorMessage NVARCHAR(500),
        CreatedUtc DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_ScrapeRunIssues_ScrapeRuns FOREIGN KEY (ScrapeRunId) REFERENCES ScrapeRuns(Id) ON DELETE CASCADE
    );
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScrapeRunIssues_ScrapeRunId')
BEGIN
    CREATE INDEX IX_ScrapeRunIssues_ScrapeRunId ON ScrapeRunIssues(ScrapeRunId);
END
```

**Step 2: Rebuild Core project to embed migration**

Run: `dotnet build AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 3: Apply migration locally**

Run: `dotnet run --project AIOMarketMaker/AIOMarketMaker.Console -- migrate "Server=(localdb)\MSSQLLocalDB;Database=AIOMarketMaker;Trusted_Connection=True;TrustServerCertificate=True;"`
Expected: "Migrations completed successfully!"

**Step 4: Verify table exists**

Run: `sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'ScrapeRunIssues'" -W`
Expected: Id, ScrapeRunId, ListingId, IssueType, ErrorMessage, CreatedUtc

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Data/Migrations/SqlServer/027_CreateScrapeRunIssuesTable.sql
git commit -m "feat: add ScrapeRunIssues table for tracking fetch failures"
```

---

## Task 2: EF Core Model

**Files:**
- Create: `AIOMarketMaker.Core/Data/Models/ScrapeRunIssue.cs`
- Modify: `AIOMarketMaker.Core/Data/EtlDbContext.cs`

**Step 1: Create the model**

Create `AIOMarketMaker.Core/Data/Models/ScrapeRunIssue.cs`:

```csharp
namespace AIOMarketMaker.Core.Data.Models;

public class ScrapeRunIssue
{
    public int Id { get; set; }
    public int ScrapeRunId { get; set; }
    public string ListingId { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedUtc { get; set; }

    public ScrapeRun? ScrapeRun { get; set; }
}
```

**Step 2: Add DbSet to EtlDbContext**

In `AIOMarketMaker.Core/Data/EtlDbContext.cs`, add after line 24 (after ScrapeRunListings):

```csharp
public DbSet<ScrapeRunIssue> ScrapeRunIssues { get; set; } = null!;
```

**Step 3: Add entity configuration**

In `EtlDbContext.cs`, add inside `OnModelCreating` after the ScrapeRunListing configuration (around line 123):

```csharp
modelBuilder.Entity<ScrapeRunIssue>(entity =>
{
    entity.ToTable("ScrapeRunIssues");
    entity.HasKey(e => e.Id);

    entity.Property(e => e.ListingId).IsRequired().HasMaxLength(50);
    entity.Property(e => e.IssueType).IsRequired().HasMaxLength(50);
    entity.Property(e => e.ErrorMessage).HasMaxLength(500);
    entity.Property(e => e.CreatedUtc).HasDefaultValueSql(dateDefaultSql);

    entity.HasIndex(e => e.ScrapeRunId);

    entity.HasOne(e => e.ScrapeRun)
        .WithMany()
        .HasForeignKey(e => e.ScrapeRunId)
        .OnDelete(DeleteBehavior.Cascade);
});
```

**Step 4: Build and verify**

Run: `dotnet build AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add AIOMarketMaker.Core/Data/Models/ScrapeRunIssue.cs AIOMarketMaker.Core/Data/EtlDbContext.cs
git commit -m "feat: add ScrapeRunIssue entity to EF Core"
```

---

## Task 3: RecordIssueActivity

**Files:**
- Create: `AIOMarketMaker.Etl/Activities/RecordIssueActivity.cs`
- Create: `AIOMarketMaker.Etl/Models/RecordIssueInput.cs`

**Step 1: Create the input model**

Create `AIOMarketMaker.Etl/Models/RecordIssueInput.cs`:

```csharp
namespace AIOMarketMaker.Etl.Models;

public record RecordIssueInput(
    int ScrapeRunId,
    string ListingId,
    string IssueType,
    string? ErrorMessage
);
```

**Step 2: Create the activity**

Create `AIOMarketMaker.Etl/Activities/RecordIssueActivity.cs`:

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

public class RecordIssueActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<RecordIssueActivity> _logger;

    public RecordIssueActivity(EtlDbContext dbContext, ILogger<RecordIssueActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(RecordIssueActivity))]
    public async Task Run([ActivityTrigger] RecordIssueInput input)
    {
        var issue = new ScrapeRunIssue
        {
            ScrapeRunId = input.ScrapeRunId,
            ListingId = input.ListingId,
            IssueType = input.IssueType,
            ErrorMessage = input.ErrorMessage,
            CreatedUtc = DateTime.UtcNow
        };

        _dbContext.ScrapeRunIssues.Add(issue);
        await _dbContext.SaveChangesAsync();

        _logger.LogWarning(
            "Recorded issue for ScrapeRun {ScrapeRunId}, Listing {ListingId}: {IssueType} - {ErrorMessage}",
            input.ScrapeRunId, input.ListingId, input.IssueType, input.ErrorMessage);
    }
}
```

**Step 3: Build and verify**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker.Etl/Activities/RecordIssueActivity.cs AIOMarketMaker.Etl/Models/RecordIssueInput.cs
git commit -m "feat: add RecordIssueActivity for tracking fetch failures"
```

---

## Task 4: Integrate with ListingEtlOrchestrator

**Files:**
- Modify: `AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs`

**Step 1: Add issue recording when description is missing**

In `ListingEtlOrchestrator.cs`, find the block around line 140-146 that handles missing description after retry:

```csharp
// If only description missing, proceed without it (description is optional)
if (!state.HasDescription)
{
    logger.LogWarning(
        "Description blob still missing for {ListingId} after retry, proceeding without it",
        input.ListingId);
}
```

Replace it with:

```csharp
// If only description missing, proceed without it but record the issue
if (!state.HasDescription)
{
    logger.LogWarning(
        "Description blob still missing for {ListingId} after retry, proceeding without it",
        input.ListingId);

    await context.CallActivityAsync(
        nameof(RecordIssueActivity),
        new RecordIssueInput(
            input.ScrapeRunId,
            input.ListingId,
            "DESCRIPTION_FETCH_FAILED",
            "Description blob not found after timeout and retry"));
}
```

**Step 2: Add using statement**

At the top of `ListingEtlOrchestrator.cs`, ensure this using exists:

```csharp
using AIOMarketMaker.Etl.Models;
```

**Step 3: Build and verify**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs
git commit -m "feat: record DESCRIPTION_FETCH_FAILED issues in orchestrator"
```

---

## Task 5: History API - Add Issue Count

**Files:**
- Modify: `AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs`

**Step 1: Update GetHistory to include issue count**

In `ScrapeJobsApi.cs`, find the `GetHistory` function (around line 248-281). Replace the entire method with:

```csharp
/// <summary>
/// GET /api/history - List scrape run history
/// </summary>
[Function("GetHistory")]
public async Task<HttpResponseData> GetHistory(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "history")] HttpRequestData req)
{
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
            r.ListingsFailed,
            r.TotalListingsFound,
            r.ListingsProcessed,
            r.CurrentPhase,
            r.ErrorMessage,
            IssueCount = _dbContext.ScrapeRunIssues.Count(i => i.ScrapeRunId == r.Id)
        })
        .ToListAsync();

    var response = req.CreateResponse(HttpStatusCode.OK);
    await response.WriteAsJsonAsync(runs);
    return response;
}
```

**Step 2: Build and verify**

Run: `dotnet build AIOMarketMaker.Functions/AIOMarketMaker.Functions.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs
git commit -m "feat: add issueCount to history API response"
```

---

## Task 6: History API - Get Issues Endpoint

**Files:**
- Modify: `AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs`

**Step 1: Add GetHistoryIssues endpoint**

In `ScrapeJobsApi.cs`, add after the `GetHistory` method (around line 281):

```csharp
/// <summary>
/// GET /api/history/{runId}/issues - Get issues for a specific scrape run
/// </summary>
[Function("GetHistoryIssues")]
public async Task<HttpResponseData> GetHistoryIssues(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "history/{runId:int}/issues")] HttpRequestData req,
    int runId)
{
    var issues = await _dbContext.ScrapeRunIssues
        .Where(i => i.ScrapeRunId == runId)
        .OrderBy(i => i.CreatedUtc)
        .Select(i => new
        {
            i.ListingId,
            i.IssueType,
            i.ErrorMessage,
            i.CreatedUtc
        })
        .ToListAsync();

    var response = req.CreateResponse(HttpStatusCode.OK);
    await response.WriteAsJsonAsync(issues);
    return response;
}
```

**Step 2: Build and verify**

Run: `dotnet build AIOMarketMaker.Functions/AIOMarketMaker.Functions.csproj`
Expected: Build succeeded

**Step 3: Test locally**

Start the Functions API and test:
```bash
curl http://localhost:7071/api/history/18050/issues
```
Expected: `[]` (empty array, no issues recorded yet)

**Step 4: Commit**

```bash
git add AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs
git commit -m "feat: add GET /api/history/{runId}/issues endpoint"
```

---

## Task 7: Desktop UI - Expandable Issues

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/index.html`
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`
- Modify: `AIOMarketMaker.Desktop/electron/src/styles.css`

**Step 1: Update history table HTML**

In `index.html`, replace the history table rows (lines 181-201) with:

```html
<template v-for="run in history" :key="run.id">
  <tr :class="{ 'error-row': run.status === 'Failed', 'has-issues': run.issueCount > 0 }" @click="run.issueCount > 0 && toggleIssues(run)">
    <td>{{ formatDate(run.startedUtc) }}</td>
    <td>{{ run.jobSearchTerm || 'All Jobs' }}</td>
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
      <span v-else-if="run.status === 'Completed' && run.totalListingsFound > 0">{{ run.totalListingsFound }} processed</span>
      <span v-else>-</span>
    </td>
    <td class="number">{{ run.listingsAdded }}</td>
    <td class="number issues-cell">
      <span v-if="run.issueCount > 0" class="issue-indicator" :class="{ expanded: run.showIssues }">
        <span class="warning-icon">⚠️</span>
        <span class="issue-count">{{ run.issueCount }}</span>
        <span class="expand-icon">{{ run.showIssues ? '▲' : '▼' }}</span>
      </span>
      <span v-else>{{ run.listingsFailed || 0 }}</span>
    </td>
  </tr>
  <tr v-if="run.showIssues && run.issues" class="issues-row">
    <td colspan="8">
      <div class="issues-panel">
        <table class="issues-table">
          <thead>
            <tr>
              <th>Listing ID</th>
              <th>Issue</th>
              <th>Error</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="issue in run.issues" :key="issue.listingId">
              <td><a :href="'https://www.ebay.co.uk/itm/' + issue.listingId" target="_blank">{{ issue.listingId }}</a></td>
              <td>{{ formatIssueType(issue.issueType) }}</td>
              <td>{{ issue.errorMessage }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </td>
  </tr>
</template>
```

**Step 2: Add JavaScript methods**

In `app.js`, add these methods inside the `methods` object:

```javascript
async toggleIssues(run) {
  if (run.showIssues) {
    run.showIssues = false;
    return;
  }

  if (!run.issues) {
    try {
      const issues = await this.apiCall(`/api/history/${run.id}/issues`);
      run.issues = issues;
    } catch (err) {
      this.showToast('Failed to load issues', 'error');
      return;
    }
  }

  run.showIssues = true;
},

formatIssueType(issueType) {
  const types = {
    'LISTING_FETCH_FAILED': 'Listing fetch failed',
    'DESCRIPTION_FETCH_FAILED': 'Description fetch failed'
  };
  return types[issueType] || issueType;
},
```

**Step 3: Add CSS styles**

In `styles.css`, add at the end:

```css
/* Issue indicators */
.has-issues {
  cursor: pointer;
}

.has-issues:hover {
  background-color: rgba(255, 193, 7, 0.1);
}

.issues-cell {
  white-space: nowrap;
}

.issue-indicator {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  background: rgba(255, 193, 7, 0.2);
  border-radius: 4px;
  cursor: pointer;
}

.issue-indicator:hover {
  background: rgba(255, 193, 7, 0.3);
}

.warning-icon {
  font-size: 12px;
}

.issue-count {
  font-weight: 600;
  color: #f59e0b;
}

.expand-icon {
  font-size: 10px;
  color: #666;
}

.issues-row td {
  padding: 0 !important;
  background: #1a1a2e;
}

.issues-panel {
  padding: 12px 16px;
  border-top: 1px solid #333;
}

.issues-table {
  width: 100%;
  font-size: 13px;
}

.issues-table th {
  text-align: left;
  padding: 6px 12px;
  color: #888;
  font-weight: 500;
}

.issues-table td {
  padding: 6px 12px;
}

.issues-table a {
  color: #60a5fa;
  text-decoration: none;
}

.issues-table a:hover {
  text-decoration: underline;
}
```

**Step 4: Test locally**

Start the desktop app and verify:
1. History shows runs with issue counts
2. Clicking a row with issues expands to show details
3. Listing IDs link to eBay

**Step 5: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/index.html AIOMarketMaker.Desktop/electron/src/app.js AIOMarketMaker.Desktop/electron/src/styles.css
git commit -m "feat: add expandable issues view to history UI"
```

---

## Verification

After all tasks complete:

1. **Run migrations**: `dotnet run --project AIOMarketMaker/AIOMarketMaker.Console -- migrate "..."`
2. **Start environment**: `/setup-local-env restart --workers 5`
3. **Trigger a scrape** that will have description fetch failures
4. **Check history UI** - should show warning icon and issue count
5. **Click to expand** - should show listing IDs with issue details

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit`
Expected: All tests pass
