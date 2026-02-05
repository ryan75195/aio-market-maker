# Inline ETL Redesign - Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace Azure Functions ETL with ASP.NET API that fetches descriptions inline via scraper cluster.

**Architecture:** Single ASP.NET API replaces both Functions projects (7071 + 7072). ScrapeJobProcessor fetches descriptions inline with `SemaphoreSlim(15)` via `GetPageHtml`. Docker Compose scraper cluster behind nginx.

**Tech Stack:** ASP.NET 8.0 Minimal API, EF Core 8, SQL Server LocalDB, Docker Compose, nginx, NUnit 3.14.0, Moq

**Design doc:** `docs/plans/2026-02-05-inline-etl-redesign.md`

---

## Phase 1: Create ASP.NET API Project

### Task 1: Create the new API project

**Files:**
- Create: `AIOMarketMaker.Api/AIOMarketMaker.Api.csproj`
- Create: `AIOMarketMaker.Api/Program.cs`
- Modify: `AIOMarketMaker.sln` (add project)

**Step 1: Create project from template**

```bash
cd <REPO_ROOT>/AIOMarketMaker
dotnet new web -n AIOMarketMaker.Api -o AIOMarketMaker.Api
dotnet sln AIOMarketMaker.sln add AIOMarketMaker.Api/AIOMarketMaker.Api.csproj
```

**Step 2: Add project references and packages**

```bash
cd AIOMarketMaker.Api
dotnet add reference ../AIOMarketMaker.Core/AIOMarketMaker.Core.csproj
dotnet add reference ../../AIOWebScraper/AIOWebScraper.Storage.Azure/AIOWebScraper.Storage.Azure.csproj
dotnet add package Microsoft.EntityFrameworkCore.SqlServer -v 8.0.11
dotnet add package Azure.Storage.Blobs -v 12.24.0
dotnet add package Azure.Data.Tables
dotnet add package Azure.Storage.Queues
dotnet add package Serilog.AspNetCore
dotnet add package Serilog.Formatting.Compact
```

**Step 3: Write minimal Program.cs with health check**

```csharp
// AIOMarketMaker.Api/Program.cs
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Run();
```

**Step 4: Verify it builds and runs**

```bash
dotnet build AIOMarketMaker.Api/AIOMarketMaker.Api.csproj
dotnet run --project AIOMarketMaker.Api -- --urls "http://localhost:5000"
# In another terminal: curl http://localhost:5000/health
```

Expected: `{"status":"healthy"}`

**Step 5: Commit**

```
feat: create AIOMarketMaker.Api ASP.NET project
```

---

### Task 2: Wire up DI (database, parsers, scraper client)

**Files:**
- Modify: `AIOMarketMaker.Api/Program.cs`
- Reference: `AIOMarketMaker.Etl/Program.cs` (copy DI registrations from here)

**Step 1: Add full DI configuration to Program.cs**

Port the service registrations from `AIOMarketMaker.Etl/Program.cs` (lines 63-198), converting from Azure Functions `HostBuilder` to ASP.NET `WebApplicationBuilder`. Key registrations:

- `EtlDbContext` with SQL Server
- `BlobServiceClient`, `TableServiceClient`
- `IWebscraperClient` / `WebscraperClient` (with HttpClient factory)
- `ISearchParser`, `IListingParser`, `IEbayUrlBuilder`
- `IEmbeddingService`, `ISemanticSearchService`, `IPineconeIndexClient`
- `IListingIndexingService`
- `IScrapeJobProcessor` (scoped)

**Do NOT register:**
- `IQueueService` / `AzureStorageQueueService` (no longer needed)
- `IScrapeRunCounterService` (no longer needed)
- `IListingProcessorService` (no longer needed)
- `QueueServiceClient` (no longer needed for scrape-work queue)

**Step 2: Add `appsettings.json` with local dev config**

```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=(localdb)\\MSSQLLocalDB;Database=AIOMarketMaker;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "Scraper": {
    "BaseUrl": "http://localhost:7126"
  },
  "AzureStorage": {
    "ConnectionString": "UseDevelopmentStorage=true"
  },
  "OpenAi": {
    "ApiKey": "{{from local.settings.json or env var}}"
  },
  "Pinecone": {
    "ApiKey": "{{from local.settings.json or env var}}",
    "IndexName": "arbitrage"
  }
}
```

NOTE: Copy API keys from `AIOMarketMaker.Etl/local.settings.json` into the new `appsettings.json` or use environment variables.

**Step 3: Verify build with all DI**

```bash
dotnet build AIOMarketMaker.Api/AIOMarketMaker.Api.csproj
```

**Step 4: Commit**

```
feat: wire up DI for database, parsers, and scraper client
```

---

## Phase 2: Move API Endpoints

### Task 3: Move Jobs CRUD endpoints

**Files:**
- Create: `AIOMarketMaker.Api/Endpoints/JobEndpoints.cs`
- Reference: `AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs:58-277`

**Step 1: Write a failing test**

Create `AIOMarketMaker.Tests/Unit/Endpoints/JobEndpoints_UnitTests.cs`:

```csharp
[TestFixture]
[Category("Unit")]
public class JobEndpoints_UnitTests
{
    [Test]
    public async Task GetJobs_should_return_enabled_jobs()
    {
        // Arrange: in-memory DB with 2 jobs (1 enabled, 1 disabled)
        // Act: call the endpoint handler directly
        // Assert: returns only enabled job
    }
}
```

**Step 2: Implement JobEndpoints**

Convert the 7 job endpoints from Azure Functions HTTP triggers to minimal API extension methods:
- `GET /api/jobs` → `GetJobs`
- `GET /api/jobs/{id}` → `GetJob`
- `POST /api/jobs` → `CreateJob`
- `PUT /api/jobs/{id}` → `UpdateJob`
- `DELETE /api/jobs/{id}` → `DeleteJob`
- `POST /api/jobs/{id}/enable` → `EnableJob`
- `POST /api/jobs/{id}/disable` → `DisableJob`

Pattern:
```csharp
public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs");
        group.MapGet("/", GetJobs);
        group.MapGet("/{id:int}", GetJob);
        // ...
    }

    private static async Task<IResult> GetJobs(EtlDbContext db) { ... }
}
```

**Step 3: Run tests, verify, commit**

```
feat: move jobs CRUD endpoints to ASP.NET API
```

---

### Task 4: Move History endpoints

**Files:**
- Create: `AIOMarketMaker.Api/Endpoints/HistoryEndpoints.cs`
- Reference: `AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs:281-425`

**Step 1: Write failing test for GetHistory**

```csharp
[Test]
public async Task GetHistory_should_return_recent_runs_with_issue_counts()
{
    // Arrange: in-memory DB with ScrapeRun
    // Act: call handler
    // Assert: returns run data with correct fields
}
```

**Step 2: Implement HistoryEndpoints**

- `GET /api/history` → returns last 50 runs with issue counts
- `GET /api/history/{runId}/issues` → returns issues for a run

**IMPORTANT:** The current `GetHistory` queries `ScrapeRunListings` for retrying listing counts. After we drop `ScrapeRunListings`, simplify the issue count to only use `ScrapeRunIssues`. Update the query:

```csharp
// BEFORE (queries ScrapeRunListings - being dropped):
var retryingCounts = await db.ScrapeRunListings
    .Where(l => runIds.Contains(l.ScrapeRunId) && l.ParseAttempts > 0 && ...)

// AFTER (ScrapeRunIssues only):
var issueCounts = await db.ScrapeRunIssues
    .Where(i => runIds.Contains(i.ScrapeRunId))
    .GroupBy(i => i.ScrapeRunId)
    .Select(g => new { ScrapeRunId = g.Key, Count = g.Count() })
    .ToDictionaryAsync(x => x.ScrapeRunId, x => x.Count);
```

**Step 3: Run tests, verify, commit**

```
feat: move history endpoints to ASP.NET API
```

---

### Task 5: Move Listings endpoints

**Files:**
- Create: `AIOMarketMaker.Api/Endpoints/ListingEndpoints.cs`
- Reference: `AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs:429-690`

**Step 1: Implement ListingEndpoints**

- `GET /api/listings/active` → `GetActiveListings`
- `GET /api/listings/stats` → `GetListingStats`
- `GET /api/listings/invalid` → `GetInvalidListings`
- `DELETE /api/listings/invalid` → `DeleteInvalidListings`
- `DELETE /api/listings` → `ClearAllListings`
- `DELETE /api/history` → `ClearAllHistory`
- `DELETE /api/data` → `ClearAllData`

These are straightforward DB queries. Port logic directly.

**Step 2: Register all endpoint groups in Program.cs**

```csharp
app.MapJobEndpoints();
app.MapHistoryEndpoints();
app.MapListingEndpoints();
```

**Step 3: Commit**

```
feat: move listing and data management endpoints to ASP.NET API
```

---

## Phase 3: Rewrite ScrapeJobProcessor

### Task 6: Write failing tests for inline description fetching

**Files:**
- Create: `AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_InlineTests.cs`

This is the critical test file. Tests must encode the business requirements, not implementation details.

**Step 1: Write tests**

```csharp
[TestFixture]
[Category("Unit")]
public class ScrapeJobProcessor_InlineTests
{
    // Test 1: Descriptions are fetched inline via GetPageHtml
    [Test]
    public async Task Should_fetch_description_via_GetPageHtml_for_each_new_listing()
    {
        // Arrange: mock IWebscraperClient, 3 new listings
        // Act: processor.Execute(run, job)
        // Assert: GetPageHtml called 3 times with description URLs
    }

    // Test 2: Failed descriptions don't fail the run
    [Test]
    public async Task Should_mark_description_as_missing_when_fetch_fails()
    {
        // Arrange: mock GetPageHtml throws for 1 of 3 listings
        // Act: processor.Execute(run, job)
        // Assert: run.Status == "Completed", 2 listings have descriptions, 1 has "missing"
    }

    // Test 3: Progress is updated during processing
    [Test]
    public async Task Should_update_listings_processed_during_fetch_loop()
    {
        // Arrange: mock GetPageHtml, 10 listings
        // Act: processor.Execute(run, job)
        // Assert: run.ListingsProcessed == 10 after completion
    }

    // Test 4: Run marked complete when all done
    [Test]
    public async Task Should_mark_run_completed_after_all_descriptions_processed()
    {
        // Arrange: mock GetPageHtml succeeds for all
        // Act: processor.Execute(run, job)
        // Assert: run.Status == "Completed", run.CompletedUtc != null
    }

    // Test 5: Run marked complete even when nothing to scrape
    [Test]
    public async Task Should_mark_run_completed_when_no_listings_to_scrape()
    {
        // Arrange: all listings are terminal (Sold/Ended)
        // Act: processor.Execute(run, job)
        // Assert: run.Status == "Completed"
    }

    // Test 6: Run marked failed on unrecoverable error
    [Test]
    public async Task Should_mark_run_failed_when_search_throws()
    {
        // Arrange: mock GetPageHtml throws on search page
        // Act: processor.Execute(run, job)
        // Assert: run.Status == "Failed", run.ErrorMessage contains exception
    }
}
```

**Step 2: Run tests, verify they fail**

```bash
dotnet test AIOMarketMaker.Tests --filter "FullyQualifiedName~ScrapeJobProcessor_InlineTests" -v n
```

Expected: All FAIL (methods not implemented yet)

**Step 3: Commit**

```
test: add failing tests for inline description fetching
```

---

### Task 7: Rewrite ScrapeJobProcessor with inline fetching

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`

The processor keeps its existing logic for searching and classifying. The change is in `CreateListingsAndEnqueueDescriptions` which becomes `FetchAndProcessDescriptions`.

**Step 1: Add `CreateRun` method**

```csharp
public async Task<ScrapeRun> CreateRun(ScrapeJobConfig job, string triggerType)
{
    var scrapeRun = new ScrapeRun
    {
        JobId = job.Id,
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

**Step 2: Add `Execute` method (replaces `Process`)**

```csharp
public async Task Execute(ScrapeRun run, ScrapeJobConfig job)
{
    try
    {
        await ExecuteScrape(run, job.Id, job.SearchTerm);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Scrape job failed: RunId={RunId}", run.Id);
        run.Status = "Failed";
        run.ErrorMessage = ex.Message;
        run.CompletedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }
}
```

**Step 3: Replace `CreateListingsAndEnqueueDescriptions` with `FetchAndProcessDescriptions`**

Key changes:
- Remove: `_dbContext.ScrapeRunListings.Add(...)` — no more junction table
- Remove: `_webscraperClient.EnqueueScrapeWork(...)` — no more queue
- Add: `FetchAndProcessDescriptions` with `SemaphoreSlim(15)` concurrency
- Add: `ProcessSingleDescription` — calls `GetPageHtml`, parses, saves, indexes
- Add: Progress updates every 10 listings via `Interlocked.Increment`

See design doc Section 2 for full implementation.

**Step 4: Update `ExecuteScrape` to always complete**

```csharp
private async Task ExecuteScrape(ScrapeRun scrapeRun, int jobId, string searchTerm)
{
    const int maxPages = 100;

    var soldSummaries = await SearchSoldListings(scrapeRun, searchTerm, maxPages);
    var activeSummaries = await SearchActiveListings(scrapeRun, searchTerm, maxPages);
    var classified = await ClassifyListings(scrapeRun, activeSummaries, soldSummaries, jobId);

    if (classified.ToUpdateFromSummary.Count > 0)
    {
        await UpdateListingsFromSummary(scrapeRun, classified.ToUpdateFromSummary, classified.ExistingListings);
    }

    if (classified.ToScrape.Count > 0)
    {
        await FetchAndProcessDescriptions(scrapeRun, classified.ToScrape, classified.ExistingListings, jobId);
    }

    // Always mark complete — no more "waiting for callbacks"
    await MarkCompleted(scrapeRun);
}
```

**Step 5: Run tests**

```bash
dotnet test AIOMarketMaker.Tests --filter "FullyQualifiedName~ScrapeJobProcessor_InlineTests" -v n
```

Expected: All PASS

**Step 6: Commit**

```
feat: rewrite ScrapeJobProcessor with inline description fetching
```

---

### Task 8: Update IScrapeJobProcessor interface

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs` (interface at top)

**Step 1: Update interface to expose CreateRun and Execute**

```csharp
public interface IScrapeJobProcessor
{
    Task<ScrapeRun> CreateRun(ScrapeJobConfig job, string triggerType);
    Task Execute(ScrapeRun run, ScrapeJobConfig job);
}
```

Remove the old `Task Process(ScrapeJobMessage message)` method.

**Step 2: Remove `ScrapeJobMessage` dependency from processor**

The processor no longer receives queue messages. It receives `ScrapeRun` and `ScrapeJobConfig` directly.

**Step 3: Remove `IQueueService` dependency from WebscraperClient**

In `WebscraperClient.cs`, remove the `EnqueueScrapeWork` method and the `IQueueService` constructor parameter.

**Step 4: Run all existing passing tests to verify no regressions**

```bash
dotnet test AIOMarketMaker.Tests --filter "Category=Unit" -v n
```

Fix any compilation errors in existing tests that reference removed methods.

**Step 5: Commit**

```
refactor: update IScrapeJobProcessor interface, remove queue dependencies
```

---

## Phase 4: Scrape Trigger Endpoints

### Task 9: Create ScrapeEndpoints with fire-and-forget

**Files:**
- Create: `AIOMarketMaker.Api/Endpoints/ScrapeEndpoints.cs`
- Create: `AIOMarketMaker.Tests/Unit/Endpoints/ScrapeEndpoints_UnitTests.cs`

**Step 1: Write failing test**

```csharp
[Test]
public async Task StartScrape_should_return_202_with_run_ids()
{
    // Arrange: in-memory DB with 1 enabled job, mock processor
    // Act: call StartScrape handler
    // Assert: 202 status, response contains run IDs
}

[Test]
public async Task StartScrape_should_create_run_for_each_enabled_job()
{
    // Arrange: in-memory DB with 2 enabled jobs
    // Act: call StartScrape handler
    // Assert: processor.CreateRun called twice
}
```

**Step 2: Implement ScrapeEndpoints**

```csharp
public static class ScrapeEndpoints
{
    public static void MapScrapeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/scrape/start", StartScrape);
    }

    private static async Task<IResult> StartScrape(
        IScrapeJobProcessor processor,
        EtlDbContext db,
        ILogger<ScrapeEndpoints> logger)
    {
        var jobs = await db.ScrapeJobs.Where(j => j.IsEnabled)
            .Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm))
            .ToListAsync();

        if (!jobs.Any())
        {
            return Results.Ok(new { message = "No enabled jobs" });
        }

        var runs = new List<ScrapeRun>();
        foreach (var job in jobs)
        {
            var run = await processor.CreateRun(job, "Manual");
            runs.Add(run);
        }

        // Fire and forget
        _ = Task.Run(async () =>
        {
            foreach (var (job, run) in jobs.Zip(runs))
            {
                try
                {
                    await processor.Execute(run, job);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Background scrape failed for run {RunId}", run.Id);
                }
            }
        });

        return Results.Accepted(value: new
        {
            runs = runs.Select(r => new { r.Id, r.JobId, r.Status })
        });
    }
}
```

**Step 3: Run tests, commit**

```
feat: add fire-and-forget scrape endpoint
```

---

### Task 10: Create NightlyScrapeService

**Files:**
- Create: `AIOMarketMaker.Api/Services/NightlyScrapeService.cs`
- Create: `AIOMarketMaker.Tests/Unit/Services/NightlyScrapeService_UnitTests.cs`

**Step 1: Write failing test**

```csharp
[Test]
public async Task Should_call_processor_for_each_enabled_job()
{
    // Test the core logic extracted into a testable method
    // Don't test the BackgroundService timing loop
}
```

**Step 2: Implement NightlyScrapeService**

See design doc Section 5 for implementation. Extract the job-processing logic into a testable `RunNightly` method.

**Step 3: Register in Program.cs**

```csharp
builder.Services.AddHostedService<NightlyScrapeService>();
```

**Step 4: Commit**

```
feat: add NightlyScrapeService BackgroundService
```

---

## Phase 5: Database & Cleanup

### Task 11: Database migration — drop ScrapeRunListings

**Files:**
- Create: `AIOMarketMaker.Core/Data/Migrations/SqlServer/034_DropScrapeRunListingsTable.sql`
- Modify: `AIOMarketMaker.Core/Data/EtlDbContext.cs`

**Step 1: Create migration**

```sql
-- Migration: 034_DropScrapeRunListingsTable
-- Description: Drop ScrapeRunListings table - no longer needed with inline ETL
-- Date: 2026-02-05

IF EXISTS (SELECT * FROM sys.tables WHERE name = 'ScrapeRunListings')
BEGIN
    DROP TABLE ScrapeRunListings;
END
```

**Step 2: Remove DbSet from EtlDbContext**

In `AIOMarketMaker.Core/Data/EtlDbContext.cs`, remove:
- Line 24: `public DbSet<ScrapeRunListing> ScrapeRunListings { get; set; }`
- Lines 104-126: `ScrapeRunListing` entity configuration in `OnModelCreating`

**Step 3: Rebuild Core to embed migration**

```bash
dotnet build AIOMarketMaker.Core/AIOMarketMaker.Core.csproj
```

**Step 4: Commit**

```
feat: add migration 034 to drop ScrapeRunListings table
```

---

### Task 12: Delete removed files

**Files to delete:**

```
# ETL triggers and services (replaced by ASP.NET endpoints)
AIOMarketMaker.Etl/Triggers/ScrapeJobQueueTrigger.cs
AIOMarketMaker.Etl/Triggers/CompletionCheckTrigger.cs
AIOMarketMaker.Etl/Triggers/ScrapeTrigger.cs
AIOMarketMaker.Etl/Endpoints/ProcessListingEndpoint.cs
AIOMarketMaker.Etl/Services/ListingProcessorService.cs
AIOMarketMaker.Etl/Services/ScrapeRunCounterService.cs
AIOMarketMaker.Etl/Services/ScrapeRunService.cs

# Tests for removed components
AIOMarketMaker.Tests/Unit/Triggers/ScrapeJobQueueTrigger_UnitTests.cs
AIOMarketMaker.Tests/Unit/Triggers/CompletionCheckTrigger_UnitTests.cs
AIOMarketMaker.Tests/Unit/Triggers/ScrapeTrigger_SequentialTests.cs
AIOMarketMaker.Tests/Unit/Triggers/ScrapeTrigger_UnitTests.cs
AIOMarketMaker.Tests/Unit/Endpoints/ProcessListingEndpoint_UnitTests.cs
AIOMarketMaker.Tests/Unit/Services/ScrapeRunCounterService_UnitTests.cs
AIOMarketMaker.Tests/Unit/Services/ListingProcessorService_UnitTests.cs
AIOMarketMaker.Tests/Unit/Services/ScrapeRunService_UnitTests.cs

# Worker callback in AIOWebScraper
AIOWebScraper/ScraperWorker/Services/HttpProcessingCallback.cs
```

**Step 1: Delete files**

```bash
git rm <each file above>
```

**Step 2: Fix any remaining compilation errors**

```bash
dotnet build AIOMarketMaker.sln
```

**Step 3: Commit**

```
refactor: delete queue-based ETL components replaced by inline processing
```

---

### Task 13: Clean up WebscraperClient

**Files:**
- Modify: `AIOMarketMaker.Core/Services/WebscraperClient.cs`

**Step 1: Remove `EnqueueScrapeWork` method** (lines 245-273)

**Step 2: Remove `IQueueService` from constructor and interface**

Check `IWebscraperClient` interface — remove `EnqueueScrapeWork` from it.

**Step 3: Remove `ScrapeWorkItem` record if only used for enqueue**

**Step 4: Run tests**

```bash
dotnet test AIOMarketMaker.Tests --filter "Category=Unit" -v n
```

**Step 5: Commit**

```
refactor: remove EnqueueScrapeWork from WebscraperClient
```

---

## Phase 6: Docker Compose Scraper Cluster

### Task 14: Create Docker Compose configuration

**Files:**
- Create: `docker-compose.scraper.yml` (at repo root)
- Create: `nginx.conf` (at repo root)

**Step 1: Create nginx.conf**

```nginx
upstream scrapers {
    server scraper:7126;
}
server {
    listen 7126;
    location / {
        proxy_pass http://scrapers;
        proxy_read_timeout 120s;
        proxy_connect_timeout 10s;
    }
}
```

**Step 2: Create docker-compose.scraper.yml**

```yaml
services:
  scraper:
    build:
      context: ./AIOWebScraper/ScraperWorker
    command: ["--dedicated-mode"]
    deploy:
      replicas: 5
    expose:
      - "7126"

  scraper-lb:
    image: nginx:alpine
    ports:
      - "7126:7126"
    volumes:
      - ./nginx.conf:/etc/nginx/nginx.conf:ro
    depends_on:
      - scraper
```

**Step 3: Test locally**

```bash
docker compose -f docker-compose.scraper.yml up -d
curl http://localhost:7126/health  # or whatever health endpoint ScraperWorker exposes
```

**Step 4: Commit**

```
feat: add Docker Compose scraper cluster with nginx LB
```

---

## Phase 7: Integration Testing

### Task 15: Write integration test for full inline pipeline

**Files:**
- Create: `AIOMarketMaker.Tests/Integration/InlinePipeline_IntegrationTests.cs`

**Step 1: Write integration test**

```csharp
[TestFixture]
[Category("Integration")]
[Explicit]
public class InlinePipeline_IntegrationTests
{
    [Test]
    public async Task Full_flow_should_search_fetch_descriptions_and_complete()
    {
        // Arrange: real DB, mock scraper that returns test HTML
        // Act: processor.Execute(run, job)
        // Assert:
        //   - ScrapeRun.Status == "Completed"
        //   - Listings created with descriptions
        //   - No ScrapeRunListings created (table dropped)
        //   - ListingsProcessed matches count
    }

    [Test]
    public async Task Should_complete_even_when_some_descriptions_fail()
    {
        // Arrange: mock scraper that fails for 2 of 10 listings
        // Act: processor.Execute(run, job)
        // Assert: Status == "Completed", 8 with descriptions, 2 with "missing"
    }
}
```

**Step 2: Run and verify**

```bash
dotnet test AIOMarketMaker.Tests --filter "FullyQualifiedName~InlinePipeline" -v n
```

**Step 3: Commit**

```
test: add integration tests for inline pipeline
```

---

### Task 16: Run full test suite and fix regressions

**Step 1: Run all unit tests**

```bash
dotnet test AIOMarketMaker.Tests --filter "Category=Unit" -v n
```

**Step 2: Fix any failing tests**

Tests that reference deleted components need to be removed or updated.

**Step 3: Verify build of entire solution**

```bash
dotnet build AIOMarketMaker.sln
```

**Step 4: Commit any fixes**

```
fix: resolve test regressions from ETL redesign
```

---

## Phase 8: Final Verification

### Task 17: End-to-end smoke test

**Step 1: Start scraper**

```bash
cd AIOWebScraper/ScraperWorker && dotnet run -- --dedicated-mode
```

**Step 2: Start new API**

```bash
cd AIOMarketMaker/AIOMarketMaker.Api && dotnet run
```

**Step 3: Trigger scrape**

```bash
curl -X POST http://localhost:5000/api/scrape/start
```

Expected: `202 Accepted` with run IDs

**Step 4: Monitor via history**

```bash
watch -n 5 'curl -s http://localhost:5000/api/history | jq ".[0] | {Status, CurrentPhase, ListingsProcessed, TotalListingsFound}"'
```

Expected: Progress from Searching → Classifying → Indexing (N/M) → Completed

**Step 5: Verify completion**

```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "SELECT TOP 1 Id, Status, CurrentPhase, TotalListingsFound, ListingsProcessed FROM ScrapeRuns ORDER BY Id DESC" -W
```

Expected: Status=Completed, ListingsProcessed = TotalListingsFound - ListingsFilteredPreQueue

---

## Summary

| Phase | Tasks | What Gets Done |
|-------|-------|----------------|
| 1 | 1-2 | New ASP.NET API project with DI |
| 2 | 3-5 | All 17 API endpoints moved from Functions |
| 3 | 6-8 | ScrapeJobProcessor rewritten with inline fetching |
| 4 | 9-10 | Fire-and-forget trigger + nightly BackgroundService |
| 5 | 11-13 | DB migration, file deletion, WebscraperClient cleanup |
| 6 | 14 | Docker Compose scraper cluster |
| 7 | 15-16 | Integration tests + regression fixes |
| 8 | 17 | End-to-end smoke test |

**Estimated time:** 3-5 focused sessions

**Risk mitigation:** Each phase is independently committable. If something goes wrong, the old Azure Functions projects still exist and can be run.
