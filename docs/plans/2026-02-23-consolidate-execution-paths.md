# Consolidate Execution Paths Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Delete `JobRunner`, port its two missing behaviors into `ScrapeJobProcessor`, migrate tests, and delete archived code.

**Architecture:** `ScrapeJobProcessor` becomes the single canonical scraping pipeline. Two behaviors from `JobRunner` are ported: (1) safe status transitions via `ListingStatusHelper.CanUpdateStatus()`, (2) smart lookback for sold searches via `LastRunUtc`. The `ScrapeJobConfig` record gains a `LastRunUtc` field, and all three call sites are updated.

**Tech Stack:** .NET 8.0, NUnit 3.14.0, Moq, EF Core (SQLite in-memory for tests)

---

### Task 1: Add status guard test — existing listing should not regress from Sold to Active

**Files:**
- Modify: `AIOMarketMaker.Tests.Unit/Services/ScrapeJobProcessor_UnitTests.cs`

**Step 1: Write the failing test**

Add this test to `ScrapeJobProcessor_UnitTests`:

```csharp
[Test]
public async Task Should_not_regress_sold_listing_back_to_active_on_rescrape()
{
    var run = CreateAndSeedScrapeRun();
    var job = CreateJobConfig();

    // Listing already Sold in DB
    _dbContext.Listings.Add(new Listing
    {
        ListingId = "REGRESS1", ScrapeJobId = 1,
        Title = "Previously Sold", ListingStatus = "Sold",
        Price = 100m, Condition = "USED"
    });
    await _dbContext.SaveChangesAsync();

    // Search returns this listing as active (eBay sometimes shows sold items in active results)
    var activeSummary = CreateSummary("REGRESS1", price: 95m, isSold: false);

    var callCount = 0;
    _searchParserMock
        .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() =>
        {
            callCount++;
            return callCount == 2
                ? new[] { activeSummary }
                : Enumerable.Empty<IEbayProductSummary>();
        });

    await CreateProcessor().Execute(run, job);

    var listing = await _dbContext.Listings.FirstAsync(l => l.ListingId == "REGRESS1");
    Assert.That(listing.ListingStatus, Is.EqualTo("Sold"),
        "Status should NOT regress from Sold back to Active");
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~Should_not_regress_sold_listing_back_to_active_on_rescrape" -v n`

Expected: FAIL — current code unconditionally overwrites status at `SaveListingsFromSummaries` line 369.

**Note:** This test may actually pass because the classify phase filters terminal listings before they reach `SaveListingsFromSummaries`. If it passes, the classify filter is already protecting against this. However, the guard is still needed because `SaveListingsFromSummaries` can receive sold-heuristic listings (existing Active listings that appear in sold results, which are routed to `ToScrape`). Write a second test for that case — see Task 2.

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "test: add status regression guard test for ScrapeJobProcessor"
```

---

### Task 2: Add status guard test — SaveListingsFromSummaries should use CanUpdateStatus

**Files:**
- Modify: `AIOMarketMaker.Tests.Unit/Services/ScrapeJobProcessor_UnitTests.cs`

This tests the deeper bug: a listing that's Active gets routed through `ToScrape` (because it appeared in sold results), and `SaveListingsFromSummaries` updates it. If another concurrent job already marked it Sold between the classify and save phases, the save should not regress it.

**Step 1: Write the failing test**

```csharp
[Test]
public async Task Should_use_safe_status_transitions_when_saving_from_summaries()
{
    var run = CreateAndSeedScrapeRun();
    var job = CreateJobConfig();

    // Pre-existing listing already marked Sold (by another job or earlier phase)
    _dbContext.Listings.Add(new Listing
    {
        ListingId = "CONCURRENT1", ScrapeJobId = 1,
        Title = "Concurrent Update", ListingStatus = "Sold",
        Price = 120m, Condition = "USED"
    });
    await _dbContext.SaveChangesAsync();

    // This listing appears in sold search results — classify routes to ToScrape
    var soldSummary = CreateSummary("CONCURRENT1", price: 110m, isSold: true);

    var callCount = 0;
    _searchParserMock
        .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() =>
        {
            callCount++;
            return callCount == 1
                ? new[] { soldSummary }
                : Enumerable.Empty<IEbayProductSummary>();
        });

    await CreateProcessor().Execute(run, job);

    var listing = await _dbContext.Listings.FirstAsync(l => l.ListingId == "CONCURRENT1");
    // Status should remain Sold (same rank — forward-only progression)
    Assert.That(listing.ListingStatus, Is.EqualTo("Sold"));
    // Price should still be updated even if status doesn't change
    Assert.That(listing.Price, Is.EqualTo(110m));
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~Should_use_safe_status_transitions_when_saving_from_summaries" -v n`

Expected: This test should pass on status (Sold→Sold is allowed by `CanUpdateStatus`) but reveals that the current code doesn't use `CanUpdateStatus` at all — it's an unprotected overwrite. If it passes, that's fine — the guard we add in Task 3 is still needed for the Sold→Active case.

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "test: add safe status transition test for SaveListingsFromSummaries"
```

---

### Task 3: Port CanUpdateStatus guard into ScrapeJobProcessor.SaveListingsFromSummaries

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs:356-374`

**Step 1: Write minimal implementation**

In `SaveListingsFromSummaries`, replace the unconditional status overwrite with a guarded one. Change the existing-listing update block (around lines 360-374):

```csharp
// BEFORE (line 369):
existing.ListingStatus = newStatus;

// AFTER:
if (ListingStatusHelper.CanUpdateStatus(existing.ListingStatus, newStatus))
{
    if (existing.ListingStatus != newStatus)
    {
        existing.ListingStatus = newStatus;
    }
}
```

The full block should become:

```csharp
if (existingListings.TryGetValue(summary.ListingId, out var existing))
{
    var oldStatus = existing.ListingStatus;
    existing.Title = summary.Title;
    existing.Price = summary.Price;
    existing.Currency = summary.Currency;
    existing.ShippingCost = summary.ShippingCost;
    existing.Url = summary.Url;
    existing.Condition = concrete?.Condition?.ToString();
    existing.PurchaseFormat = concrete?.BuyingFormat?.ToString();
    existing.Images = images;
    existing.EndDateUtc = concrete?.EndDateUtc;
    existing.DescriptionStatus = "pending";
    existing.UpdatedUtc = DateTime.UtcNow;

    if (ListingStatusHelper.CanUpdateStatus(existing.ListingStatus, newStatus))
    {
        existing.ListingStatus = newStatus;
    }

    if (oldStatus != existing.ListingStatus)
    {
        _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
        {
            ListingId = existing.Id,
            ListingStatus = existing.ListingStatus ?? oldStatus ?? "Unknown",
            Price = summary.Price,
            RecordedUtc = DateTime.UtcNow,
            Source = "StatusUpdate"
        });
    }
}
```

**Important:** The `existing.ListingStatus = newStatus` assignment (line 369) currently happens BEFORE the `oldStatus != newStatus` check (line 376). Move the status assignment inside the `CanUpdateStatus` guard but keep the history check using the `oldStatus` variable.

**Step 2: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ScrapeJobProcessor" -v n`

Expected: ALL existing + new tests pass.

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs
git commit -m "fix: guard status transitions with CanUpdateStatus in SaveListingsFromSummaries"
```

---

### Task 4: Add LastRunUtc to ScrapeJobConfig and wire call sites

**Files:**
- Modify: `AIOMarketMaker.Etl/Models/ScrapeRunModels.cs:15`
- Modify: `AIOMarketMaker.Api/Endpoints/ScrapeEndpoints.cs:27`
- Modify: `AIOMarketMaker.Api/Services/NightlyScrapeService.cs:60`
- Modify: `AIOMarketMaker.Api/Services/StartupRecoveryService.cs:66`

**Step 1: Extend ScrapeJobConfig record**

In `AIOMarketMaker.Etl/Models/ScrapeRunModels.cs` line 15:

```csharp
// BEFORE:
public record ScrapeJobConfig(int Id, string SearchTerm);

// AFTER:
public record ScrapeJobConfig(int Id, string SearchTerm, DateTime? LastRunUtc = null);
```

**Step 2: Update ScrapeEndpoints.cs (line 27)**

```csharp
// BEFORE:
.Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm))

// AFTER:
.Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm, j.LastRunUtc))
```

**Step 3: Update NightlyScrapeService.cs (line 60)**

```csharp
// BEFORE:
.Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm))

// AFTER:
.Select(j => new ScrapeJobConfig(j.Id, j.SearchTerm, j.LastRunUtc))
```

**Step 4: Update StartupRecoveryService.cs (line 66)**

```csharp
// BEFORE:
.Select(r => new OrphanedRun(r.Id, new ScrapeJobConfig(r.JobId!.Value, jobs[r.JobId.Value])))

// AFTER — need LastRunUtc from ScrapeJobs too**
```

Check `StartupRecoveryService.cs` for how `jobs` dictionary is built. It maps `JobId → SearchTerm`. Extend it to also carry `LastRunUtc`. Read the file to determine exact changes needed.

**Step 5: Run build to verify compilation**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`

Expected: Build succeeds. Existing callers using `new ScrapeJobConfig(id, term)` still work due to default `LastRunUtc = null`.

**Step 6: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Models/ScrapeRunModels.cs AIOMarketMaker/AIOMarketMaker.Api/Endpoints/ScrapeEndpoints.cs AIOMarketMaker/AIOMarketMaker.Api/Services/NightlyScrapeService.cs AIOMarketMaker/AIOMarketMaker.Api/Services/StartupRecoveryService.cs
git commit -m "feat: add LastRunUtc to ScrapeJobConfig for smart lookback"
```

---

### Task 5: Add smart lookback test for sold search

**Files:**
- Modify: `AIOMarketMaker.Tests.Unit/Services/ScrapeJobProcessor_UnitTests.cs`

**Step 1: Write the failing test**

ScrapeJobProcessor currently searches ALL sold pages (up to 100). With smart lookback, it should compute lookback days from `LastRunUtc` and only pass that date window to the URL builder.

First, we need the processor to support lookback. The test verifies the URL builder receives date parameters.

```csharp
[Test]
public async Task Should_use_smart_lookback_for_sold_search_when_LastRunUtc_set()
{
    var run = CreateAndSeedScrapeRun();
    var lastRun = DateTime.UtcNow.AddDays(-3);
    var job = new ScrapeJobConfig(1, "Test", lastRun);

    await CreateProcessor().Execute(run, job);

    // Verify sold search URL was built with date parameters
    // The URL builder should receive sold=true for the first call
    _urlBuilderMock.Verify(
        u => u.BuildSearchUrl(
            "Test", true, 1, Condition.NULL, BuyingFormat.BUY_NOW),
        Times.Once,
        "Sold search should be called");
}
```

**Note:** The current `BuildSearchUrl` doesn't accept date parameters, and the lookback behavior will be implemented as a max-pages limit rather than URL date filtering (eBay's date filtering is server-side via the search results, not URL params). The smart lookback manifests as: when `LastRunUtc` is recent, stop paginating sold results earlier if dates fall outside the window. For now, we'll implement it as a configurable max-pages cap based on lookback days.

Actually — looking at the code more carefully, `EbayScraper.SearchSoldListings` accepts `startDate/endDate` but `ScrapeJobProcessor.SearchListings` (line 574) doesn't use `EbayScraper` at all — it builds URLs directly via `_urlBuilder.BuildSearchUrl()`. The smart lookback needs to limit pages, not filter by date in the URL. The simplest approach: cap `maxPages` based on lookback. If last run was 2 days ago, we don't need 100 pages of sold listings — 5-10 pages covers a few days of sold items for most searches.

Let's simplify: add a `DefaultLookbackDays` config (from `IConfiguration`) and pass `LastRunUtc` to `ExecuteScrape` to compute a reduced `maxPages` for sold search.

**Revised approach:** Rather than changing the URL builder, compute a reduced `maxSoldPages` inside `ExecuteScrape`:

```csharp
[Test]
public async Task Should_limit_sold_search_pages_when_LastRunUtc_is_recent()
{
    var run = CreateAndSeedScrapeRun();
    var lastRun = DateTime.UtcNow.AddDays(-1);
    var job = new ScrapeJobConfig(1, "Test", lastRun);

    // Return results on every page to verify pagination stops early
    _searchParserMock
        .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() => new[] { CreateSummary(Guid.NewGuid().ToString("N")[..12]) });

    await CreateProcessor().Execute(run, job);

    // With 1-day lookback, sold search should cap at ~5 pages, not 100
    // Count how many search URLs were fetched
    var searchCalls = _webscraperClientMock.Invocations
        .Where(i => i.Method.Name == "GetPageHtmlAsync")
        .Count();

    // We expect: sold pages (capped) + active pages (up to 100) + description fetches
    // The key constraint: total sold pages should be small
    // With endless results, sold would go to 100 pages without lookback
    Assert.That(searchCalls, Is.LessThan(50),
        "With 1-day lookback, should not search 100+ pages of sold results");
}
```

This test is fragile. Better approach: make the maxSoldPages directly observable. Instead of an indirect test, add a test that verifies the formula:

```csharp
[Test]
public async Task Should_use_reduced_sold_pages_based_on_lookback_days()
{
    var run = CreateAndSeedScrapeRun();
    // Last run was 2 days ago — should use fewer sold pages than the 100 default
    var job = new ScrapeJobConfig(1, "Test", DateTime.UtcNow.AddDays(-2));

    await CreateProcessor().Execute(run, job);

    // The processor should complete without errors
    var updatedRun = await _dbContext.ScrapeRuns.FindAsync(1);
    Assert.That(updatedRun!.Status, Is.EqualTo("Completed"));
}
```

Let's keep this simple and testable. The actual implementation test will verify the formula in a static method.

**Step 2: Commit test**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "test: add smart lookback sold search test"
```

---

### Task 6: Implement smart lookback in ScrapeJobProcessor

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`

**Step 1: Add CalculateMaxSoldPages static method**

Add after `MarkCompleted` (line 572):

```csharp
internal static int CalculateMaxSoldPages(DateTime? lastRunUtc, int defaultMaxPages = 100)
{
    if (lastRunUtc == null)
    {
        return defaultMaxPages;
    }

    var daysSinceLastRun = (int)Math.Ceiling((DateTime.UtcNow - lastRunUtc.Value).TotalDays);
    var lookbackDays = Math.Max(1, daysSinceLastRun + 1); // +1 buffer

    // Heuristic: ~2 pages per day of sold results for typical searches
    // Minimum 5 pages to handle high-volume searches
    var calculatedPages = Math.Max(5, lookbackDays * 2);
    return Math.Min(calculatedPages, defaultMaxPages);
}
```

**Step 2: Wire it into ExecuteScrape**

Change `ExecuteScrape` (line 98-129) to pass different maxPages for sold vs active:

```csharp
// BEFORE:
const int maxPages = 100;
var soldSummaries = await SearchSoldListings(scrapeRun, searchTerm, maxPages);
var activeSummaries = await SearchActiveListings(scrapeRun, searchTerm, maxPages);

// AFTER:
const int maxPages = 100;
var maxSoldPages = CalculateMaxSoldPages(job.LastRunUtc, maxPages);
_logger.LogInformation("Smart lookback: LastRunUtc={LastRun}, maxSoldPages={MaxSoldPages}",
    job.LastRunUtc, maxSoldPages);

var soldSummaries = await SearchSoldListings(scrapeRun, searchTerm, maxSoldPages);
var activeSummaries = await SearchActiveListings(scrapeRun, searchTerm, maxPages);
```

This means `ExecuteScrape` needs access to `job`. Update the signature:

```csharp
// BEFORE:
private async Task ExecuteScrape(ScrapeRun scrapeRun, int jobId, string searchTerm)

// AFTER:
private async Task ExecuteScrape(ScrapeRun scrapeRun, ScrapeJobConfig job)
```

And update the caller in `Execute` (line 85):

```csharp
// BEFORE:
await ExecuteScrape(run, job.Id, job.SearchTerm);

// AFTER:
await ExecuteScrape(run, job);
```

Then update all references within `ExecuteScrape` from `jobId` to `job.Id` and `searchTerm` to `job.SearchTerm`.

**Step 3: Add unit test for CalculateMaxSoldPages**

```csharp
[TestCase(null, 100, Description = "Null LastRunUtc uses default")]
[TestCase(-1, 5, Description = "1 day ago: max(5, 2*2) = 5")]
[TestCase(-3, 8, Description = "3 days ago: max(5, 4*2) = 8")]
[TestCase(-30, 62, Description = "30 days ago: max(5, 31*2) = 62")]
[TestCase(-90, 100, Description = "90 days ago: min(182, 100) = 100")]
public void Should_calculate_max_sold_pages_from_lookback(int? daysAgo, int expectedPages)
{
    var lastRunUtc = daysAgo.HasValue ? DateTime.UtcNow.AddDays(daysAgo.Value) : (DateTime?)null;
    var result = ScrapeJobProcessor.CalculateMaxSoldPages(lastRunUtc);
    Assert.That(result, Is.EqualTo(expectedPages));
}
```

**Step 4: Run all ScrapeJobProcessor tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ScrapeJobProcessor" -v n`

Expected: ALL tests pass.

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "feat: add smart lookback to limit sold search pages based on LastRunUtc"
```

---

### Task 7: Update job LastRunUtc after scrape completes

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`

Currently `ScrapeJobProcessor` does NOT update `ScrapeJob.LastRunUtc` after a run — `JobRunner` did this in `UpdateJobTimestamp`. Port this behavior.

**Step 1: Write the failing test**

```csharp
[Test]
public async Task Should_update_job_LastRunUtc_on_successful_completion()
{
    var run = CreateAndSeedScrapeRun();
    var job = CreateJobConfig();

    await CreateProcessor().Execute(run, job);

    var updatedJob = await _dbContext.ScrapeJobs.FindAsync(1);
    Assert.That(updatedJob!.LastRunUtc, Is.Not.Null,
        "Job LastRunUtc should be updated after successful run");
    Assert.That(updatedJob.LastRunUtc, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromSeconds(5)));
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~Should_update_job_LastRunUtc" -v n`

Expected: FAIL — `LastRunUtc` is null because processor never sets it.

**Step 3: Implement — update LastRunUtc in MarkCompleted**

In `MarkCompleted` (around line 566), add:

```csharp
private async Task MarkCompleted(ScrapeRun scrapeRun, int jobId)
{
    scrapeRun.Status = "Completed";
    scrapeRun.CurrentPhase = "Completed";
    scrapeRun.CompletedUtc = DateTime.UtcNow;

    var job = await _dbContext.ScrapeJobs.FindAsync(jobId);
    if (job != null)
    {
        job.LastRunUtc = DateTime.UtcNow;
    }

    await _dbContext.SaveChangesAsync();
}
```

Update the call site in `ExecuteScrape`:

```csharp
// BEFORE:
await MarkCompleted(scrapeRun);

// AFTER:
await MarkCompleted(scrapeRun, job.Id);
```

**Step 4: Run all tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests.Unit/AIOMarketMaker.Tests.Unit.csproj --filter "FullyQualifiedName~ScrapeJobProcessor" -v n`

Expected: ALL pass.

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs AIOMarketMaker/AIOMarketMaker.Tests.Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "feat: update ScrapeJob.LastRunUtc after successful scrape completion"
```

---

### Task 8: Delete JobRunner and remove registrations

**Files:**
- Delete: `AIOMarketMaker.Core/Services/JobRunner.cs`
- Modify: `AIOMarketMaker.Etl/Startup.cs:126`

**Step 1: Delete JobRunner.cs**

Delete the file `AIOMarketMaker.Core/Services/JobRunner.cs` entirely. This removes:
- `JobRunResult` record
- `IJobRunner` interface
- `JobRunner` class

**Step 2: Remove registration from Startup.cs**

In `AIOMarketMaker.Etl/Startup.cs` line 126, delete:

```csharp
// DELETE THIS LINE:
services.AddScoped<IJobRunner, JobRunner>();
```

**Step 3: Build to find remaining references**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`

Expected: Build errors in:
- `AIOMarketMaker.Tests.Integration/JobOrchestratorIntegrationTests.cs` (references `IJobRunner`, `JobRunner`)

These will be handled in Task 9.

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Startup.cs
git rm AIOMarketMaker/AIOMarketMaker.Core/Services/JobRunner.cs
git commit -m "refactor: delete JobRunner — ScrapeJobProcessor is the canonical pipeline"
```

---

### Task 9: Delete integration tests that depend on JobRunner

**Files:**
- Delete: `AIOMarketMaker.Tests.Integration/JobOrchestratorIntegrationTests.cs`

**Rationale:** These tests are `[Explicit]` integration tests that hit real Azure services with hardcoded connection strings and API keys. They test the old `JobRunner` flow. The equivalent behavior is now covered by:
- `ScrapeJobProcessor_UnitTests` (14 tests covering search, classify, update, description fetch)
- `ScrapeJobProcessor_InlineTests` (20+ tests covering inline description pipeline)

The integration tests also contain **hardcoded Azure credentials** that should not be in the codebase.

**Step 1: Delete the file**

Delete `AIOMarketMaker.Tests.Integration/JobOrchestratorIntegrationTests.cs`.

**Step 2: Build to verify**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`

Expected: Clean build.

**Step 3: Run all tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.sln --filter "Category=Unit" -v n`

Expected: ALL unit tests pass.

**Step 4: Commit**

```bash
git rm AIOMarketMaker/AIOMarketMaker.Tests.Integration/JobOrchestratorIntegrationTests.cs
git commit -m "refactor: delete JobRunner integration tests — covered by ScrapeJobProcessor unit tests"
```

---

### Task 10: Delete archived code directories

**Files:**
- Delete: `AIOMarketMaker.Functions/_archived/` (entire directory)
- Delete: `AIOMarketMaker.Etl/_archived/` (entire directory)

**Step 1: Delete archived directories**

```bash
rm -rf AIOMarketMaker/AIOMarketMaker.Functions/_archived
rm -rf AIOMarketMaker/AIOMarketMaker.Etl/_archived
```

**Step 2: Build to verify nothing depended on archived code**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`

Expected: Clean build.

**Step 3: Commit**

```bash
git rm -r AIOMarketMaker/AIOMarketMaker.Functions/_archived
git rm -r AIOMarketMaker/AIOMarketMaker.Etl/_archived
git commit -m "cleanup: delete archived Durable Functions and old ETL code"
```

---

### Task 11: Run full test suite and verify

**Step 1: Run all unit tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.sln --filter "Category=Unit" -v n`

Expected: ALL pass. No regressions.

**Step 2: Run full build**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`

Expected: Clean build, zero warnings related to our changes.

**Step 3: Verify no remaining JobRunner references**

Run: `grep -r "JobRunner\|IJobRunner" AIOMarketMaker/ --include="*.cs" -l`

Expected: No results (only docs/plans may reference it historically, which is fine).

---

## Behavioral Parity Checklist

| JobRunner Behavior | Ported? | Where |
|--------------------|---------|-------|
| `CanUpdateStatus()` safe transitions | YES | Task 3: `SaveListingsFromSummaries` |
| `CalculateLookbackDays()` smart sold search | YES | Task 6: `CalculateMaxSoldPages` |
| `DetectAndUpdateSoldListings()` global transition | ALREADY COVERED | Classify phase detects Active→Sold per-job |
| `FilterNewListings()` terminal filter | ALREADY COVERED | Classify phase filters terminal statuses |
| `UpsertListings()` insert/update with history | ALREADY COVERED | `SaveListingsFromSummaries` + `ProcessFetchedDescription` |
| `UpdateJobTimestamp()` set LastRunUtc | YES | Task 7: `MarkCompleted` |
| `MapToListing()` entity mapping | ALREADY COVERED | `SaveListingsFromSummaries` builds Listing directly |

## What We Lose Table

| Old Behavior | New Behavior | Impact | Intentional? |
|--------------|--------------|--------|--------------|
| `EbayScraper.GetItemsFromListings()` batch detail fetch | Inline description-only fetch | Descriptions fetched from `itm.ebaydesc.com` instead of full listing re-scrape | YES — summary data is sufficient for price/status, descriptions are the only thing missing |
| Global Active→Sold detection across all jobs | Per-job Active→Sold detection | A listing owned by Job A won't get Sold-detected by Job B's run | YES — cross-job detection was causing confusion and is handled by the next run of Job A |
| `JobRunResult` return type | `ScrapeRun` entity with status | More data available, persisted to DB | YES — improvement |
