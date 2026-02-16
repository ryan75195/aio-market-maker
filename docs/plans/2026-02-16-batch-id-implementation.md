# Batch ID Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a `BatchId` column to `ScrapeRun` so runs triggered together can be grouped for UI and operational monitoring.

**Architecture:** Nullable `Guid? BatchId` on existing `ScrapeRun` table. Both trigger points generate a GUID and stamp all runs in the batch. New `/api/history/batches` endpoint computes batch-level status and aggregates from child runs.

**Tech Stack:** .NET 8.0, EF Core, SQL Server, NUnit + Moq

---

### Task 1: SQL Migration — Add BatchId Column

**Files:**
- Create: `AIOMarketMaker.Core/Data/Migrations/SqlServer/038_AddBatchIdToScrapeRuns.sql`

**Step 1: Create the migration file**

```sql
-- Migration: 038_AddBatchIdToScrapeRuns
-- Description: Add BatchId column to ScrapeRuns for grouping runs triggered together
-- Date: 2026-02-16

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRuns') AND name = 'BatchId')
BEGIN
    ALTER TABLE ScrapeRuns ADD BatchId UNIQUEIDENTIFIER NULL;
END

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_ScrapeRuns_BatchId')
BEGIN
    CREATE INDEX IX_ScrapeRuns_BatchId ON ScrapeRuns (BatchId) WHERE BatchId IS NOT NULL;
END
```

**Step 2: Verify embedded resource**

Check that `AIOMarketMaker.Core.csproj` has a glob for `Data/Migrations/SqlServer/*.sql` as `EmbeddedResource`. If not, add it.

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: BUILD SUCCEEDED

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Data/Migrations/SqlServer/038_AddBatchIdToScrapeRuns.sql
git commit -m "feat: add 038 migration for BatchId column on ScrapeRuns"
```

---

### Task 2: Model + EF Config — Add BatchId to ScrapeRun

**Files:**
- Modify: `AIOMarketMaker.Core/Data/Models/ScrapeRun.cs`
- Modify: `AIOMarketMaker.Core/Data/EtlDbContext.cs`

**Step 1: Write the failing test**

File: `AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs`

Add this test to the existing `ScrapeJobProcessor_UnitTests` class:

```csharp
[Test]
public async Task Should_store_batch_id_when_provided_to_CreateRun()
{
    var batchId = Guid.NewGuid();
    var job = CreateJobConfig();

    var run = await CreateProcessor().CreateRun(job, "Manual", batchId);

    var saved = await _dbContext.ScrapeRuns.FindAsync(run.Id);
    Assert.That(saved!.BatchId, Is.EqualTo(batchId));
}

[Test]
public async Task Should_allow_null_batch_id_in_CreateRun()
{
    var job = CreateJobConfig();

    var run = await CreateProcessor().CreateRun(job, "Manual");

    var saved = await _dbContext.ScrapeRuns.FindAsync(run.Id);
    Assert.That(saved!.BatchId, Is.Null);
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~Should_store_batch_id_when_provided_to_CreateRun|FullyQualifiedName~Should_allow_null_batch_id_in_CreateRun" -v n`
Expected: FAIL — `CreateRun` does not accept a `batchId` parameter

**Step 3: Add BatchId property to ScrapeRun model**

File: `AIOMarketMaker.Core/Data/Models/ScrapeRun.cs`

Add after the `InstanceId` property (around line 14):

```csharp
/// <summary>
/// Groups runs that were triggered together (e.g., all jobs from a single manual/nightly trigger).
/// Null for legacy runs created before batch tracking.
/// </summary>
public Guid? BatchId { get; set; }
```

**Step 4: Add EF configuration for the index**

File: `AIOMarketMaker.Core/Data/EtlDbContext.cs`

Inside the `modelBuilder.Entity<ScrapeRun>(entity => { ... })` block (after line 100), add:

```csharp
entity.HasIndex(e => e.BatchId)
    .HasFilter("[BatchId] IS NOT NULL");
```

Note: The `HasFilter` uses SQL Server syntax. For SQLite (used in tests), EF will ignore the filter — this is fine since SQLite doesn't support filtered indexes.

**Step 5: Update CreateRun signature and implementation**

File: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`

Update the interface (line 17):

```csharp
Task<ScrapeRun> CreateRun(ScrapeJobConfig job, string triggerType, Guid? batchId = null);
```

Update the implementation (line 61):

```csharp
public async Task<ScrapeRun> CreateRun(ScrapeJobConfig job, string triggerType, Guid? batchId = null)
{
    var scrapeRun = new ScrapeRun
    {
        JobId = job.Id,
        BatchId = batchId,
        Status = "Queued",
        CurrentPhase = "Queued",
        TriggerType = triggerType,
        StartedUtc = DateTime.UtcNow,
        InstanceId = Guid.NewGuid().ToString()
    };
    _dbContext.ScrapeRuns.Add(scrapeRun);
    await _dbContext.SaveChangesAsync();
    return scrapeRun;
}
```

**Step 6: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~Should_store_batch_id_when_provided_to_CreateRun|FullyQualifiedName~Should_allow_null_batch_id_in_CreateRun" -v n`
Expected: PASS

Also verify no existing tests broke:

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeJobProcessor_UnitTests" -v n`
Expected: All PASS

**Step 7: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Data/Models/ScrapeRun.cs \
        AIOMarketMaker/AIOMarketMaker.Core/Data/EtlDbContext.cs \
        AIOMarketMaker/AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs \
        AIOMarketMaker/AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "feat: add BatchId to ScrapeRun model and CreateRun"
```

---

### Task 3: Wire BatchId Into Trigger Points

**Files:**
- Modify: `AIOMarketMaker.Api/Endpoints/ScrapeEndpoints.cs`
- Modify: `AIOMarketMaker.Api/Services/NightlyScrapeService.cs`

**Step 1: Update ScrapeEndpoints.StartScrape**

File: `AIOMarketMaker.Api/Endpoints/ScrapeEndpoints.cs`

Update the `StartScrapeResponse` record (line 8):

```csharp
public record StartScrapeResponse(Guid BatchId, IEnumerable<ScrapeRunInfo> Runs);
```

In `StartScrape` method, generate a batchId before the loop (insert after line 33, before the `runIds` loop):

```csharp
var batchId = Guid.NewGuid();
```

Update the `CreateRun` call (line 39):

```csharp
var run = await processor.CreateRun(job, "Manual", batchId);
```

Update the response (line 73):

```csharp
var runInfos = runIds.Zip(jobs, (id, job) => new ScrapeRunInfo(id, job.Id, "Queued"));
return Results.Accepted(value: new StartScrapeResponse(batchId, runInfos));
```

**Step 2: Update NightlyScrapeService.RunNightly**

File: `AIOMarketMaker.Api/Services/NightlyScrapeService.cs`

In `RunNightly` method, generate a batchId before the job loop (insert after `_logger.LogInformation("Found {Count} enabled jobs..."` on line 63):

```csharp
var batchId = Guid.NewGuid();
```

Update the `CreateRun` call (line 71):

```csharp
var run = await processor.CreateRun(job, "Nightly", batchId);
```

**Step 3: Build to verify compilation**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: BUILD SUCCEEDED

**Step 4: Run all existing tests to verify nothing broke**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit" -v n`
Expected: All PASS

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Api/Endpoints/ScrapeEndpoints.cs \
        AIOMarketMaker/AIOMarketMaker.Api/Services/NightlyScrapeService.cs
git commit -m "feat: generate BatchId in StartScrape and RunNightly"
```

---

### Task 4: Batch History Endpoint

**Files:**
- Create: `AIOMarketMaker.Api/Endpoints/BatchHistoryEndpoints.cs`
- Create: `AIOMarketMaker.Tests/Unit/Endpoints/BatchHistoryEndpoints_UnitTests.cs`
- Modify: `AIOMarketMaker.Api/Program.cs` (register the new endpoint group)

**Step 1: Write the failing tests**

Create file: `AIOMarketMaker.Tests/Unit/Endpoints/BatchHistoryEndpoints_UnitTests.cs`

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Tests.Utils;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.Unit.Endpoints;

[TestFixture]
[Category("Unit")]
public class BatchHistoryEndpoints_UnitTests
{
    private EtlDbContext _dbContext = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();

        _dbContext.ScrapeJobs.AddRange(
            new ScrapeJob { Id = 1, SearchTerm = "Shark Navigator" },
            new ScrapeJob { Id = 2, SearchTerm = "Nike Dunk Low" },
            new ScrapeJob { Id = 3, SearchTerm = "PS5 Console" });
        _dbContext.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public void Should_derive_Completed_when_all_runs_completed()
    {
        var result = BatchStatusDeriver.Derive(new[] { "Completed", "Completed", "Completed" });
        Assert.That(result, Is.EqualTo("Completed"));
    }

    [Test]
    public void Should_derive_Failed_when_all_runs_failed()
    {
        var result = BatchStatusDeriver.Derive(new[] { "Failed", "Failed" });
        Assert.That(result, Is.EqualTo("Failed"));
    }

    [Test]
    public void Should_derive_PartialFailure_when_mix_of_completed_and_failed()
    {
        var result = BatchStatusDeriver.Derive(new[] { "Completed", "Failed", "Completed" });
        Assert.That(result, Is.EqualTo("PartialFailure"));
    }

    [Test]
    public void Should_derive_Running_when_any_run_is_active()
    {
        var result = BatchStatusDeriver.Derive(new[] { "Completed", "Searching", "Queued" });
        Assert.That(result, Is.EqualTo("Running"));
    }

    [Test]
    public void Should_derive_Queued_when_all_queued()
    {
        var result = BatchStatusDeriver.Derive(new[] { "Queued", "Queued" });
        Assert.That(result, Is.EqualTo("Queued"));
    }

    [TestCase("Running")]
    [TestCase("Searching")]
    [TestCase("Indexing")]
    [TestCase("Processing")]
    public void Should_derive_Running_for_active_status(string activeStatus)
    {
        var result = BatchStatusDeriver.Derive(new[] { activeStatus, "Completed" });
        Assert.That(result, Is.EqualTo("Running"));
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~BatchHistoryEndpoints_UnitTests" -v n`
Expected: FAIL — `BatchStatusDeriver` does not exist

**Step 3: Implement the endpoint and status deriver**

Create file: `AIOMarketMaker.Api/Endpoints/BatchHistoryEndpoints.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Api.Endpoints;

public record BatchRunResponse(
    int Id, int? JobId, string? JobSearchTerm,
    string? Status, string? CurrentPhase,
    int TotalListingsFound, int ListingsProcessed,
    int ListingsAddedActive, int ListingsAddedSold,
    int ListingsUpdated, int ListingsSkipped,
    int ListingsFailed, int ListingsFilteredPreQueue,
    int IssueCount);

public record BatchResponse(
    Guid BatchId, string? TriggerType,
    DateTime StartedUtc, DateTime? CompletedUtc,
    string Status, int RunCount,
    int TotalListingsFound, int TotalListingsProcessed,
    IEnumerable<BatchRunResponse> Runs);

public static class BatchStatusDeriver
{
    private static readonly HashSet<string> ActiveStatuses = new()
        { "Running", "Searching", "Indexing", "Processing" };

    public static string Derive(IEnumerable<string> runStatuses)
    {
        var statuses = runStatuses.ToList();

        if (statuses.Any(s => ActiveStatuses.Contains(s)))
        {
            return "Running";
        }

        if (statuses.All(s => s == "Queued"))
        {
            return "Queued";
        }

        if (statuses.Any(s => s == "Queued"))
        {
            return "Running";
        }

        if (statuses.All(s => s == "Completed"))
        {
            return "Completed";
        }

        if (statuses.All(s => s == "Failed"))
        {
            return "Failed";
        }

        return "PartialFailure";
    }
}

public static class BatchHistoryEndpoints
{
    public static void MapBatchHistoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/history");
        group.MapGet("/batches", GetBatches);
    }

    private static async Task<IResult> GetBatches(EtlDbContext db, int page = 1, int pageSize = 20)
    {
        if (page < 1) { page = 1; }
        if (pageSize < 1) { pageSize = 20; }
        if (pageSize > 100) { pageSize = 100; }

        // Load all runs that have a BatchId, grouped by batch
        var batchIds = await db.ScrapeRuns
            .Where(r => r.BatchId != null)
            .Select(r => r.BatchId!.Value)
            .Distinct()
            .OrderByDescending(b => db.ScrapeRuns
                .Where(r => r.BatchId == b)
                .Min(r => r.StartedUtc))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var totalCount = await db.ScrapeRuns
            .Where(r => r.BatchId != null)
            .Select(r => r.BatchId!.Value)
            .Distinct()
            .CountAsync();

        var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        // Load all runs for these batches in one query
        var runs = await db.ScrapeRuns
            .Where(r => r.BatchId != null && batchIds.Contains(r.BatchId.Value))
            .ToListAsync();

        // Load job names
        var jobIds = runs.Where(r => r.JobId.HasValue).Select(r => r.JobId!.Value).Distinct().ToList();
        var jobNames = await db.ScrapeJobs
            .Where(j => jobIds.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id, j => j.SearchTerm);

        // Load issue counts
        var runIds = runs.Select(r => r.Id).ToList();
        var issueCounts = await db.ScrapeRunIssues
            .Where(i => runIds.Contains(i.ScrapeRunId))
            .GroupBy(i => i.ScrapeRunId)
            .Select(g => new { RunId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RunId, x => x.Count);

        // Build response
        var batches = batchIds.Select(batchId =>
        {
            var batchRuns = runs.Where(r => r.BatchId == batchId).OrderBy(r => r.JobId).ToList();
            var statuses = batchRuns.Select(r => r.Status).ToList();

            return new BatchResponse(
                BatchId: batchId,
                TriggerType: batchRuns.FirstOrDefault()?.TriggerType,
                StartedUtc: batchRuns.Min(r => r.StartedUtc),
                CompletedUtc: batchRuns.All(r => r.CompletedUtc.HasValue)
                    ? batchRuns.Max(r => r.CompletedUtc)
                    : null,
                Status: BatchStatusDeriver.Derive(statuses),
                RunCount: batchRuns.Count,
                TotalListingsFound: batchRuns.Sum(r => r.TotalListingsFound),
                TotalListingsProcessed: batchRuns.Sum(r => r.ListingsProcessed),
                Runs: batchRuns.Select(r => new BatchRunResponse(
                    r.Id, r.JobId,
                    r.JobId.HasValue ? jobNames.GetValueOrDefault(r.JobId.Value) : null,
                    r.Status, r.CurrentPhase,
                    r.TotalListingsFound, r.ListingsProcessed,
                    r.ListingsAddedActive, r.ListingsAddedSold,
                    r.ListingsUpdated, r.ListingsSkipped,
                    r.ListingsFailed, r.ListingsFilteredPreQueue,
                    issueCounts.GetValueOrDefault(r.Id, 0))));
        });

        return Results.Ok(new { items = batches, totalCount, totalPages, page, pageSize });
    }
}
```

**Step 4: Register the endpoint in Program.cs**

File: `AIOMarketMaker.Api/Program.cs`

Find where other endpoints are mapped (e.g., `app.MapHistoryEndpoints()`) and add:

```csharp
app.MapBatchHistoryEndpoints();
```

**Step 5: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~BatchHistoryEndpoints_UnitTests" -v n`
Expected: All PASS

**Step 6: Run all unit tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit" -v n`
Expected: All PASS

**Step 7: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Api/Endpoints/BatchHistoryEndpoints.cs \
        AIOMarketMaker/AIOMarketMaker.Tests/Unit/Endpoints/BatchHistoryEndpoints_UnitTests.cs \
        AIOMarketMaker/AIOMarketMaker.Api/Program.cs
git commit -m "feat: add /api/history/batches endpoint with batch status derivation"
```

---

### Task 5: Add BatchId to Existing History Endpoint Response

**Files:**
- Modify: `AIOMarketMaker.Api/Endpoints/HistoryEndpoints.cs`

The existing `/api/history` endpoint should include `BatchId` in its response so the UI can link individual runs to their batch.

**Step 1: Update the response records**

File: `AIOMarketMaker.Api/Endpoints/HistoryEndpoints.cs`

Update `HistoryRunResponse` (line 6) to include `BatchId`:

```csharp
public record HistoryRunResponse(
    int Id, string? InstanceId, Guid? BatchId, int? JobId, string? JobSearchTerm,
    string? TriggerType, DateTime? StartedUtc, DateTime? CompletedUtc,
    string? Status, int ListingsAddedActive, int ListingsAddedSold,
    int ListingsUpdated, int ListingsSkipped, int ListingsFailed,
    int ListingsFilteredPreQueue, int TotalListingsFound,
    int ListingsProcessed, string? CurrentPhase, string? ErrorMessage,
    int IssueCount);
```

Update `HistoryRunProjection` (line 21) similarly:

```csharp
public record HistoryRunProjection(
    int Id, string? InstanceId, Guid? BatchId, int? JobId, string? JobSearchTerm,
    string? TriggerType, DateTime? StartedUtc, DateTime? CompletedUtc,
    string? Status, int ListingsAddedActive, int ListingsAddedSold,
    int ListingsUpdated, int ListingsSkipped, int ListingsFailed,
    int ListingsFilteredPreQueue, int TotalListingsFound,
    int ListingsProcessed, string? CurrentPhase, string? ErrorMessage);
```

Update the LINQ query in `GetHistory` (line 57) to include `r.BatchId`:

```csharp
.Select(r => new HistoryRunProjection(
    r.Id, r.InstanceId, r.BatchId, r.JobId,
    // ... rest stays the same
```

Update the response mapping (line 78) to pass `BatchId` through:

```csharp
var runsWithIssues = runs.Select(r => new HistoryRunResponse(
    r.Id, r.InstanceId, r.BatchId, r.JobId, r.JobSearchTerm,
    // ... rest stays the same
```

**Step 2: Build and run existing tests**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln && dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit" -v n`
Expected: BUILD SUCCEEDED, all PASS

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Api/Endpoints/HistoryEndpoints.cs
git commit -m "feat: include BatchId in existing history endpoint response"
```

---

## Summary of Changes

| File | Action | What |
|------|--------|------|
| `Core/Data/Migrations/SqlServer/038_AddBatchIdToScrapeRuns.sql` | Create | SQL migration |
| `Core/Data/Models/ScrapeRun.cs` | Modify | Add `BatchId` property |
| `Core/Data/EtlDbContext.cs` | Modify | Add filtered index config |
| `Etl/Services/ScrapeJobProcessor.cs` | Modify | Add `batchId` param to `CreateRun` |
| `Api/Endpoints/ScrapeEndpoints.cs` | Modify | Generate + pass batchId, update response |
| `Api/Services/NightlyScrapeService.cs` | Modify | Generate + pass batchId |
| `Api/Endpoints/BatchHistoryEndpoints.cs` | Create | New batch grouping endpoint |
| `Api/Endpoints/HistoryEndpoints.cs` | Modify | Add BatchId to response |
| `Api/Program.cs` | Modify | Register batch endpoint |
| `Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs` | Modify | 2 new tests |
| `Tests/Unit/Endpoints/BatchHistoryEndpoints_UnitTests.cs` | Create | 7 status derivation tests |
