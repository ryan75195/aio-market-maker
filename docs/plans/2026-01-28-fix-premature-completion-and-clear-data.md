# Fix Premature Completion Race Condition & Clear Data Improvements

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix two bugs (race condition causing premature run completion; ListingsAdded/Skipped counters always 0) and add a "Clear All Data" button to the UI that resets all scrape-related tables.

**Architecture:** The race condition is fixed by setting TotalListingsFound BEFORE submitting scrape jobs (so blob triggers can't auto-complete against TotalListingsFound=0), plus a safety guard requiring TotalListingsFound > 0 in the auto-completion SQL. ListingsAdded/Skipped are tracked atomically in the same SQL UPDATE that increments ListingsProcessed. The clear data feature adds a single "Reset All" button that deletes from Listings, ScrapeRunListings, and ScrapeRuns together.

**Tech Stack:** C# (.NET 8.0), Azure Durable Functions, SQL Server, Electron/Vue.js

---

### Task 1: Fix Race Condition - Set TotalListingsFound Before Submitting Scrape Jobs

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs:153-178`

**Context:** Currently the orchestrator does steps in this order:
1. InsertScrapeRunListings (line 157-159) - creates Pending junction entries
2. SubmitScrapeJobsActivity (line 164-166) - submits URLs to workers
3. UpdateScrapeRunProgressActivity (line 173-178) - sets TotalListingsFound + Phase="Indexing"

With 15 workers, step 2 completes fast, blob triggers fire, and `UpdateScrapeRunListingActivity` auto-completes the run because `TotalListingsFound` is still 0 (step 3 hasn't run). Fix: move step 3 before step 2.

**Step 1: Reorder the steps in JobOrchestrator**

In `JobOrchestrator.cs`, move the `UpdateScrapeRunProgressActivity` call (currently lines 173-178) to BEFORE the `SubmitScrapeJobsActivity` call (currently lines 164-166). The new order after `InsertScrapeRunListingsActivity` should be:

1. Set TotalListingsFound + Phase="Indexing" (moved up)
2. Submit scrape jobs (moved down)
3. Update job timestamp

Replace lines 161-181 with:

```csharp
            logger.LogInformation("Job {JobId}: Inserted {Count} ScrapeRunListings entries", jobId, newListingIds.Count);

            // Step 6: Set TotalListingsFound BEFORE submitting jobs to prevent race condition
            // where blob triggers auto-complete the run against TotalListingsFound=0
            await context.CallActivityAsync(
                nameof(UpdateScrapeRunProgressActivity),
                new UpdateProgressInput(scrapeInstanceId,
                    TotalListingsFound: newListingIds.Count,
                    ListingsProcessed: 0,
                    CurrentPhase: "Indexing"));

            // Step 7: Submit all scrape jobs (fire-and-forget)
            var submitResult = await context.CallActivityAsync<SubmitScrapeJobsResult>(
                nameof(SubmitScrapeJobsActivity),
                new SubmitScrapeJobsInput(newListingIds));

            logger.LogInformation("Job {JobId}: Submitted {Submitted} scrape jobs ({Failed} failed)",
                jobId, submitResult.SubmittedCount, submitResult.FailedCount);

            // Step 8: Update job timestamp
            await context.CallActivityAsync(nameof(UpdateJobTimestampActivity), jobId);
```

**Step 2: Run tests to verify no regressions**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit`
Expected: All existing tests pass (the reorder doesn't change test behavior since the mock context doesn't enforce call order)

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs
git commit -m "fix: set TotalListingsFound before submitting scrape jobs to prevent race condition"
```

---

### Task 2: Add Safety Guard to Auto-Completion SQL

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Activities/UpdateScrapeRunListingActivity.cs:45-52`

**Context:** Even with the reorder fix, add a safety guard so auto-completion can never fire when `TotalListingsFound = 0`. This protects against edge cases (e.g., Durable Functions replay, unexpected ordering).

**Step 1: Add TotalListingsFound > 0 guard to the SQL**

In `UpdateScrapeRunListingActivity.cs`, update the SQL at lines 45-52. Add `AND TotalListingsFound > 0` to both CASE conditions:

```csharp
            await _dbContext.Database.ExecuteSqlRawAsync(@"
                UPDATE ScrapeRuns
                SET ListingsProcessed = ListingsProcessed + 1,
                    Status = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                  AND TotalListingsFound > 0
                                  AND Status = 'Running' THEN 'Completed' ELSE Status END,
                    CompletedUtc = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                        AND TotalListingsFound > 0
                                        AND Status = 'Running' THEN GETUTCDATE() ELSE CompletedUtc END,
                    CurrentPhase = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                        AND TotalListingsFound > 0
                                        AND Status = 'Running' THEN 'Completed' ELSE CurrentPhase END
                WHERE Id = {0}", input.ScrapeRunId);
```

Note: This also sets `CurrentPhase = 'Completed'` on auto-completion, which was previously missing (phase was stuck at "Filtering" or "Indexing" when auto-completion fired).

**Step 2: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit`
Expected: All pass

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/UpdateScrapeRunListingActivity.cs
git commit -m "fix: guard auto-completion against TotalListingsFound=0 and set CurrentPhase on completion"
```

---

### Task 3: Track ListingsAdded/Skipped in Auto-Completion SQL

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Activities/UpdateScrapeRunListingActivity.cs:45-52`
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs:80-129`
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Models/ListingEtlInput.cs:8` (UpdateScrapeRunListingInput)

**Context:** `ListingsAdded` and `ListingsSkipped` are never set on the happy path. The simplest fix: `ProcessListingActivity` already knows if a listing is new (insert) vs existing (update). Return this info so `UpdateScrapeRunListingActivity` can increment the right counter atomically.

**Step 1: Add IsNew field to UpdateScrapeRunListingInput**

In `UpdateScrapeRunListingActivity.cs`, change the record at line 8:

```csharp
public record UpdateScrapeRunListingInput(int ScrapeRunId, string ListingId, string Status, bool IsNewListing = false);
```

**Step 2: Update ProcessListingActivity to return whether listing was new**

In `ProcessListingActivity.cs`, change the return type from `Task` to `Task<bool>` and return whether the listing was inserted (new) vs updated (existing). Change the method signature at line 34:

```csharp
    [Function(nameof(ProcessListingActivity))]
    public async Task<bool> Run([ActivityTrigger] ProcessListingInput input)
```

At line 129, before `SaveChangesAsync`, set a flag:

```csharp
        var isNew = existing == null;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Processed listing {ListingId}: {Action}, descriptionStatus={Status}",
            input.ListingId, isNew ? "added" : "updated", descriptionStatus);

        return isNew;
```

**Step 3: Update ListingEtlOrchestrator to pass IsNewListing**

In `ListingEtlOrchestrator.cs`, change line 164 to capture the return value:

```csharp
        var isNewListing = await context.CallActivityAsync<bool>(nameof(ProcessListingActivity), processInput);
```

Then update the `UpdateScrapeRunListingInput` construction at lines 167-171:

```csharp
        var updateInput = new UpdateScrapeRunListingInput(
            lookupResult.ScrapeRunId!.Value,
            input.ListingId,
            "Complete",
            IsNewListing: isNewListing
        );
```

**Step 4: Update the SQL to increment ListingsAdded or ListingsSkipped**

In `UpdateScrapeRunListingActivity.cs`, update the SQL to also increment the appropriate counter. After the `TotalListingsFound > 0` guard is already in place from Task 2:

```csharp
        if (input.Status == "Complete")
        {
            var addedIncrement = input.IsNewListing ? 1 : 0;
            var skippedIncrement = input.IsNewListing ? 0 : 1;

            await _dbContext.Database.ExecuteSqlRawAsync(@"
                UPDATE ScrapeRuns
                SET ListingsProcessed = ListingsProcessed + 1,
                    ListingsAdded = ListingsAdded + {1},
                    ListingsSkipped = ListingsSkipped + {2},
                    Status = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                  AND TotalListingsFound > 0
                                  AND Status = 'Running' THEN 'Completed' ELSE Status END,
                    CompletedUtc = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                        AND TotalListingsFound > 0
                                        AND Status = 'Running' THEN GETUTCDATE() ELSE CompletedUtc END,
                    CurrentPhase = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                        AND TotalListingsFound > 0
                                        AND Status = 'Running' THEN 'Completed' ELSE CurrentPhase END
                WHERE Id = {0}", input.ScrapeRunId, addedIncrement, skippedIncrement);
        }
```

**Step 5: Run tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit`
Expected: All pass. The existing test at `JobOrchestratorTests` doesn't test this path.

**Step 6: Build to check compilation**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeds with no errors

**Step 7: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/UpdateScrapeRunListingActivity.cs \
        AIOMarketMaker/AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs \
        AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs
git commit -m "feat: track ListingsAdded/ListingsSkipped counters during ETL processing"
```

---

### Task 4: Add "Clear All Data" API Endpoint

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs:410-430`

**Context:** Currently "Clear Listings" deletes from `Listings` and "Clear History" deletes from `ScrapeRuns` (which cascades to `ScrapeRunListings`). The user wants a single button that resets everything scrape-related. Add a new endpoint that clears all three tables in the right order.

**Step 1: Add ClearAllData endpoint**

In `ScrapeJobsApi.cs`, add a new endpoint after `ClearAllHistory` (after line 430):

```csharp
    /// <summary>
    /// DELETE /api/data/all - Clear all scrape data (listings, run history, and junction table)
    /// </summary>
    [Function("ClearAllData")]
    public async Task<HttpResponseData> ClearAllData(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "data/all")] HttpRequestData req)
    {
        var listingsCount = await _dbContext.Listings.CountAsync();
        var runsCount = await _dbContext.ScrapeRuns.CountAsync();

        // Delete in correct order: Listings first (no FK deps), then ScrapeRuns (cascades to ScrapeRunListings)
        if (listingsCount > 0)
            await _dbContext.Database.ExecuteSqlRawAsync("DELETE FROM Listings");
        if (runsCount > 0)
            await _dbContext.Database.ExecuteSqlRawAsync("DELETE FROM ScrapeRuns");

        _logger.LogInformation("Cleared all data: {Listings} listings, {Runs} scrape runs (+ cascaded ScrapeRunListings)", listingsCount, runsCount);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { deletedListings = listingsCount, deletedRuns = runsCount });
        return response;
    }
```

**Step 2: Build to verify**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Functions/AIOMarketMaker.Functions.csproj`
Expected: Builds successfully

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Functions/Functions/ScrapeJobsApi.cs
git commit -m "feat: add DELETE /api/data/all endpoint to clear all scrape data at once"
```

---

### Task 5: Add "Reset All Data" Button to Desktop UI

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/index.html:148-155`
- Modify: `AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/app.js:334-351`

**Step 1: Update the Clear Data card in index.html**

Replace the Clear Data card (lines 148-155) with:

```html
            <div class="card">
              <h3>Clear Data</h3>
              <p>Remove scrape data from the database.</p>
              <div class="button-group">
                <button class="btn danger" @click="clearAllData" :disabled="loading">Reset All Data</button>
                <button class="btn danger small" @click="clearListings" :disabled="loading">Listings Only</button>
                <button class="btn danger small" @click="clearHistory" :disabled="loading">History Only</button>
              </div>
            </div>
```

**Step 2: Add clearAllData method in app.js**

Add the new method before `clearListings()` (before line 315):

```javascript
    async clearAllData() {
      if (!confirm('Delete ALL scrape data (listings, run history, and tracking data)? This cannot be undone.')) return;

      this.loading = true;
      try {
        const data = await this.apiCall('/data/all', { method: 'DELETE' });
        const result = this.toCamelCase(data);
        this.showToast(`Cleared ${result.deletedListings} listings and ${result.deletedRuns} history records`, 'success');
        if (this.currentView === 'history') {
          await this.loadHistory();
        } else if (this.currentView === 'opportunities') {
          await this.loadOpportunities();
        }
      } catch (err) {
        this.showToast(`Failed to clear data: ${err.message}`, 'error');
      } finally {
        this.loading = false;
      }
    },
```

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/index.html \
        AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "feat: add Reset All Data button to clear listings, history, and tracking data"
```

---

### Task 6: Verify End-to-End

**Step 1: Rebuild all projects**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: All projects build successfully

**Step 2: Run all unit tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter Category=Unit`
Expected: All pass

**Step 3: Restart local environment and test**

Use `/setup-local-env restart --workers 15` to restart the stack with the fixes, then trigger a scrape and use `/monitor-scrape` to verify:
- TotalListingsFound is set before scrape jobs start processing
- Run does NOT auto-complete prematurely
- ListingsAdded/ListingsSkipped counters increment correctly
- "Reset All Data" button works from UI

**Step 4: Commit any final adjustments**

---

## Summary of Changes

| File | Change | Bug/Feature |
|------|--------|-------------|
| `JobOrchestrator.cs` | Move TotalListingsFound update before SubmitScrapeJobs | Bug 1: Race condition |
| `UpdateScrapeRunListingActivity.cs` | Add `TotalListingsFound > 0` guard + `CurrentPhase` update + `ListingsAdded/Skipped` increment | Bug 1 + Bug 2 |
| `ProcessListingActivity.cs` | Return `bool` (isNew) instead of void | Bug 2: Counter tracking |
| `ListingEtlOrchestrator.cs` | Pass `IsNewListing` to UpdateScrapeRunListingInput | Bug 2: Counter tracking |
| `ScrapeJobsApi.cs` | Add `DELETE /api/data/all` endpoint | Feature: Clear all data |
| `index.html` | Add "Reset All Data" button | Feature: UI button |
| `app.js` | Add `clearAllData()` method | Feature: UI button |
