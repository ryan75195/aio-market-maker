# Fix Indexing Sequencing Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Index listings to Pinecone when descriptions are processed, move RefreshComparables to run at completion.

**Architecture:** Add IListingIndexingService to ListingProcessorService for per-listing indexing. Move RefreshComparables from ScrapeJobProcessor to ScrapeRunCounterService completion path. Add scrapeJobId to Increment interface.

**Tech Stack:** .NET 8.0, NUnit, Moq, Pinecone SDK, OpenAI embeddings

---

### Task 1: Update IScrapeRunCounterService interface to include scrapeJobId

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeRunCounterService.cs`

**Step 1: Update the interface**

Change `IScrapeRunCounterService.Increment` signature:
```csharp
Task Increment(int scrapeRunId, int scrapeJobId, string status, string? listingStatus = null);
```

**Step 2: Update SqlScrapeRunCounterService.Increment signature**

Add `int scrapeJobId` parameter (not used yet, will be used in Task 3).

**Step 3: Update EfCoreScrapeRunCounterService.Increment signature**

Add `int scrapeJobId` parameter (not used yet, will be used in Task 3).

**Step 4: Update all callers of Increment**

Search for `_counterService.Increment` and `counterService.Increment` and add the `scrapeJobId` argument. The caller is `ListingProcessorService.Process()` which already has `request.ScrapeJobId`.

**Step 5: Build and run tests**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit`
Expected: Build succeeds, tests pass (mocks will need updating too).

**Step 6: Commit**

```bash
git add -A && git commit -m "refactor: add scrapeJobId to IScrapeRunCounterService.Increment"
```

---

### Task 2: Add IListingIndexingService to ListingProcessorService

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ListingProcessorService.cs`
- Modify: `AIOMarketMaker.Tests/Unit/Services/ListingProcessorService_UnitTests.cs`
- Modify: `AIOMarketMaker.Tests/Unit/Endpoints/ProcessListingEndpoint_UnitTests.cs`

**Step 1: Write failing tests**

In `ListingProcessorService_UnitTests.cs`:
- Add `Mock<IListingIndexingService> _indexingServiceMock` field
- Update `CreateService()` to pass the mock
- Add test: `Should_index_listing_when_description_complete` - verify `Index(listing, true)` called after successful parse
- Add test: `Should_not_index_when_description_missing` - verify `Index` never called when blob not found
- Add test: `Should_not_index_when_description_parse_fails` - verify `Index` never called on parse exception

**Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ListingProcessorService_UnitTests"`
Expected: FAIL - constructor mismatch

**Step 3: Add IListingIndexingService dependency to ListingProcessorService**

Add `IListingIndexingService` to constructor. After `SaveChangesAsync` on the success path (where `DescriptionStatus == "complete"`), call:
```csharp
await _indexingService.Index(listing, isNew: true);
```

Only index when `listing.DescriptionStatus == "complete"`. Do NOT wrap in try/catch — let failures propagate.

**Step 4: Update ProcessListingEndpoint_UnitTests.cs**

Add indexing service mock to `CreateEndpoint()` helper.

**Step 5: Run tests to verify they pass**

Run: `dotnet test --filter Category=Unit`
Expected: All pass

**Step 6: Commit**

```bash
git add -A && git commit -m "feat: index listings to Pinecone when description is processed"
```

---

### Task 3: Move RefreshComparables to completion path

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeRunCounterService.cs`
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`
- Modify: `AIOMarketMaker.Etl/Triggers/CompletionCheckTrigger.cs`

**Step 1: Add IComparablesRefreshService to ScrapeRunCounterService implementations**

Both `SqlScrapeRunCounterService` and `EfCoreScrapeRunCounterService` get `IComparablesRefreshService` as a constructor dependency.

**Step 2: Call RefreshComparables in EfCoreScrapeRunCounterService completion path**

When completion is detected (the `if` block that sets Status = "Completed"), BEFORE setting Completed:
```csharp
scrapeRun.CurrentPhase = "Refreshing comparables";
await _dbContext.SaveChangesAsync();

var activeListings = await _dbContext.Listings
    .Where(l => l.ScrapeJobId == scrapeJobId && l.ListingStatus == "Active")
    .ToListAsync();
await _comparablesRefreshService.Refresh(activeListings);

scrapeRun.Status = "Completed";
scrapeRun.CurrentPhase = "Completed";
scrapeRun.CompletedUtc = DateTime.UtcNow;
```

**Step 3: Call RefreshComparables in SqlScrapeRunCounterService completion path**

After the completion SQL UPDATE succeeds (completedRows > 0), call refresh:
```csharp
if (completedRows > 0)
{
    // Need to load the run to get JobId for the refresh
    var activeListings = await _dbContext.Listings
        .Where(l => l.ScrapeJobId == scrapeJobId && l.ListingStatus == "Active")
        .ToListAsync();
    await _comparablesRefreshService.Refresh(activeListings);
}
```

**Step 4: Remove RefreshComparables from ScrapeJobProcessor.ExecuteScrape**

Remove the `await RefreshComparables(scrapeRun, jobId);` call and the `RefreshComparables` private method entirely (if no longer needed).

**Step 5: Update CompletionCheckTrigger to also refresh**

Add `IComparablesRefreshService` dependency. When marking runs as completed, also refresh comparables for each run's job.

**Step 6: Build and run tests**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Run: `dotnet test --filter Category=Unit`
Expected: All pass

**Step 7: Commit**

```bash
git add -A && git commit -m "feat: move RefreshComparables to completion path"
```

---

### Task 4: Register IListingIndexingService in ETL Program.cs

**Files:**
- Modify: `AIOMarketMaker.Etl/Program.cs`

**Step 1: Register IListingIndexingService**

Pinecone is required. After the existing Pinecone registration, add:
```csharp
services.AddSingleton<IListingIndexingService, ListingIndexingService>();
```

This requires `IEmbeddingService` to also be registered (it should already be from the embedding stage work).

**Step 2: Verify EmbeddingService is registered**

Check that `IEmbeddingService` is registered. If not, add it.

**Step 3: Build and verify**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeds

**Step 4: Commit**

```bash
git add -A && git commit -m "chore: register IListingIndexingService in ETL DI"
```

---

### Task 5: Update existing tests for new dependencies

**Files:**
- Modify: `AIOMarketMaker.Tests/Unit/Services/ListingProcessorService_UnitTests.cs`
- Modify: `AIOMarketMaker.Tests/Unit/Endpoints/ProcessListingEndpoint_UnitTests.cs`

**Step 1: Verify all tests pass**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit`

**Step 2: Fix any remaining test compilation issues**

Update any mocks or constructors that changed.

**Step 3: Commit if any fixes needed**

```bash
git add -A && git commit -m "test: fix tests for indexing sequencing changes"
```
