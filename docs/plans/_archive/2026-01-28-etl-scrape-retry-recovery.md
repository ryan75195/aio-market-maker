# ETL Scrape Retry Recovery Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** When the ETL orchestrator detects a missing listing or description blob after timeout, re-enqueue the URL to the scrape-work queue for one retry attempt before marking as failed.

**Architecture:** The `ListingEtlOrchestrator` will call a new `EnqueueScrapeRetryActivity` when a blob is missing after timeout. This activity uses the existing `IWebscraperClient.NewJobAsync` to submit a single-URL scrape job. The `ScrapeRunListing` table gains a `RetryCount` column to track attempts. If `RetryCount >= 1`, mark as Failed instead of retrying.

**Tech Stack:** Azure Durable Functions, Entity Framework Core, SQL Server migrations

---

## Task 1: Add RetryCount Column to ScrapeRunListings

**Files:**
- Create: `AIOMarketMaker.Core/Data/Migrations/SqlServer/024_AddRetryCountToScrapeRunListings.sql`
- Modify: `AIOMarketMaker.Core/Data/Models/ScrapeRunListing.cs`

**Step 1: Create migration file**

```sql
-- Migration: 024_AddRetryCountToScrapeRunListings (SQL Server)
-- Description: Adds RetryCount column to track ETL retry attempts for failed scrapes
-- Date: 2026-01-28

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('ScrapeRunListings') AND name = 'RetryCount')
BEGIN
    ALTER TABLE ScrapeRunListings ADD RetryCount INT NOT NULL DEFAULT 0;
END
GO
```

**Step 2: Add property to model**

In `ScrapeRunListing.cs`, add after `CompletedUtc`:

```csharp
/// <summary>
/// Number of ETL retry attempts for this listing (max 1)
/// </summary>
public int RetryCount { get; set; } = 0;
```

**Step 3: Rebuild Core project to embed migration**

Run: `dotnet build AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker.Core/Data/Migrations/SqlServer/024_AddRetryCountToScrapeRunListings.sql
git add AIOMarketMaker.Core/Data/Models/ScrapeRunListing.cs
git commit -m "feat: add RetryCount column to ScrapeRunListings for ETL retry tracking"
```

---

## Task 2: Create EnqueueScrapeRetryActivity

**Files:**
- Create: `AIOMarketMaker.Etl/Activities/EnqueueScrapeRetryActivity.cs`
- Create: `AIOMarketMaker.Etl/Models/EnqueueScrapeRetryInput.cs`

**Step 1: Create the input model**

Create `AIOMarketMaker.Etl/Models/EnqueueScrapeRetryInput.cs`:

```csharp
namespace AIOMarketMaker.Etl.Models;

/// <summary>
/// Input for EnqueueScrapeRetryActivity.
/// </summary>
/// <param name="ListingId">The eBay listing ID</param>
/// <param name="FileKey">Which blob to retry: "listing" or "description"</param>
public record EnqueueScrapeRetryInput(string ListingId, string FileKey);
```

**Step 2: Create the activity**

Create `AIOMarketMaker.Etl/Activities/EnqueueScrapeRetryActivity.cs`:

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

/// <summary>
/// Enqueues a single URL to the scrape-work queue for retry.
/// Used when a blob fails to arrive after ETL timeout.
/// </summary>
public class EnqueueScrapeRetryActivity
{
    private readonly IWebscraperClient _webScraper;
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly ILogger<EnqueueScrapeRetryActivity> _logger;

    public EnqueueScrapeRetryActivity(
        IWebscraperClient webScraper,
        IEbayUrlBuilder urlBuilder,
        ILogger<EnqueueScrapeRetryActivity> logger)
    {
        _webScraper = webScraper;
        _urlBuilder = urlBuilder;
        _logger = logger;
    }

    [Function(nameof(EnqueueScrapeRetryActivity))]
    public async Task Run([ActivityTrigger] EnqueueScrapeRetryInput input)
    {
        var url = input.FileKey == "listing"
            ? _urlBuilder.BuildListingUrl(input.ListingId)
            : _urlBuilder.BuildDescriptionUrl(input.ListingId);

        _logger.LogInformation(
            "Enqueuing retry scrape for {ListingId}/{FileKey}: {Url}",
            input.ListingId, input.FileKey, url);

        await _webScraper.NewJobAsync(
            new[] { url },
            groupId: input.ListingId,
            fileKey: input.FileKey);
    }
}
```

**Step 3: Build to verify**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker.Etl/Models/EnqueueScrapeRetryInput.cs
git add AIOMarketMaker.Etl/Activities/EnqueueScrapeRetryActivity.cs
git commit -m "feat: add EnqueueScrapeRetryActivity for re-enqueueing failed scrapes"
```

---

## Task 3: Create IncrementRetryCountActivity

**Files:**
- Create: `AIOMarketMaker.Etl/Activities/IncrementRetryCountActivity.cs`

**Step 1: Create the activity**

Create `AIOMarketMaker.Etl/Activities/IncrementRetryCountActivity.cs`:

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Activities;

public record IncrementRetryCountInput(int ScrapeRunId, string ListingId);

public record IncrementRetryCountResult(int NewRetryCount);

public class IncrementRetryCountActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<IncrementRetryCountActivity> _logger;

    public IncrementRetryCountActivity(EtlDbContext dbContext, ILogger<IncrementRetryCountActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(IncrementRetryCountActivity))]
    public async Task<IncrementRetryCountResult> Run([ActivityTrigger] IncrementRetryCountInput input)
    {
        var mapping = await _dbContext.ScrapeRunListings
            .FirstOrDefaultAsync(s => s.ScrapeRunId == input.ScrapeRunId && s.ListingId == input.ListingId);

        if (mapping == null)
        {
            _logger.LogWarning(
                "ScrapeRunListing not found for ScrapeRunId={ScrapeRunId}, ListingId={ListingId}",
                input.ScrapeRunId, input.ListingId);
            return new IncrementRetryCountResult(999); // Force failure path
        }

        mapping.RetryCount++;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Incremented RetryCount for {ListingId} to {RetryCount}",
            input.ListingId, mapping.RetryCount);

        return new IncrementRetryCountResult(mapping.RetryCount);
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker.Etl/Activities/IncrementRetryCountActivity.cs
git commit -m "feat: add IncrementRetryCountActivity for tracking retry attempts"
```

---

## Task 4: Update ListingEtlOrchestrator with Retry Logic

**Files:**
- Modify: `AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs`

**Step 1: Add using statement**

At the top of the file, the existing usings are sufficient.

**Step 2: Replace the missing blob handling logic**

Replace the current block (lines 72-86):

```csharp
// Check if listing blob exists - if not, mark as failed and exit
if (!state.HasListing)
{
    logger.LogWarning(
        "Listing blob not found for {ListingId} after timeout, marking as Failed",
        input.ListingId);

    var failedInput = new UpdateScrapeRunListingInput(
        lookupResult.ScrapeRunId!.Value,
        input.ListingId,
        "Failed"
    );
    await context.CallActivityAsync(nameof(UpdateScrapeRunListingActivity), failedInput);
    return;
}
```

With this new logic:

```csharp
// Handle missing blobs with retry logic
if (!state.HasListing || !state.HasDescription)
{
    var missingBlob = !state.HasListing ? "listing" : "description";

    // Check retry count
    var retryResult = await context.CallActivityAsync<IncrementRetryCountResult>(
        nameof(IncrementRetryCountActivity),
        new IncrementRetryCountInput(lookupResult.ScrapeRunId!.Value, input.ListingId));

    if (retryResult.NewRetryCount > 1)
    {
        // Max retries exceeded - mark as failed
        logger.LogWarning(
            "Max retries exceeded for {ListingId}/{MissingBlob}, marking as Failed",
            input.ListingId, missingBlob);

        var failedInput = new UpdateScrapeRunListingInput(
            lookupResult.ScrapeRunId!.Value,
            input.ListingId,
            "Failed"
        );
        await context.CallActivityAsync(nameof(UpdateScrapeRunListingActivity), failedInput);
        return;
    }

    // Re-enqueue the missing blob for retry
    logger.LogInformation(
        "Re-enqueueing {MissingBlob} scrape for {ListingId} (retry {RetryCount})",
        missingBlob, input.ListingId, retryResult.NewRetryCount);

    await context.CallActivityAsync(
        nameof(EnqueueScrapeRetryActivity),
        new EnqueueScrapeRetryInput(input.ListingId, missingBlob));

    // Wait again for the blob
    var retryTimeout = context.CurrentUtcDateTime.AddMinutes(TimeoutMinutes);
    using var retryCts = new CancellationTokenSource();

    var retryTimeoutTask = context.CreateTimer(retryTimeout, retryCts.Token);
    var retryEventName = missingBlob == "listing" ? "listing-ready" : "description-ready";
    var retryEvent = context.WaitForExternalEvent<bool>(retryEventName);

    var retryWinner = await Task.WhenAny(retryTimeoutTask, retryEvent);

    if (retryWinner == retryEvent)
    {
        retryCts.Cancel();
        logger.LogInformation("Retry blob arrived for {ListingId}/{MissingBlob}", input.ListingId, missingBlob);

        // Re-check blob state
        state = await context.CallActivityAsync<BlobState>(nameof(CheckBlobsActivity), input);
    }
    else
    {
        logger.LogWarning(
            "Retry timeout for {ListingId}/{MissingBlob}, marking as Failed",
            input.ListingId, missingBlob);

        var failedInput = new UpdateScrapeRunListingInput(
            lookupResult.ScrapeRunId!.Value,
            input.ListingId,
            "Failed"
        );
        await context.CallActivityAsync(nameof(UpdateScrapeRunListingActivity), failedInput);
        return;
    }
}

// Final check - if listing still missing after retry, fail
if (!state.HasListing)
{
    logger.LogWarning(
        "Listing blob still missing for {ListingId} after retry, marking as Failed",
        input.ListingId);

    var failedInput = new UpdateScrapeRunListingInput(
        lookupResult.ScrapeRunId!.Value,
        input.ListingId,
        "Failed"
    );
    await context.CallActivityAsync(nameof(UpdateScrapeRunListingActivity), failedInput);
    return;
}
```

**Step 3: Build to verify**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs
git commit -m "feat: add retry logic to ListingEtlOrchestrator for missing blobs"
```

---

## Task 5: Run Integration Test

**Step 1: Restart the ETL Functions to pick up changes**

Stop the existing func host (Ctrl+C) and restart:

Run: `cd AIOMarketMaker.Etl && func start`
Expected: Functions host starts with no errors

**Step 2: Verify migration runs**

Check the logs for migration output. The migration should apply automatically on first database access.

**Step 3: Manual verification**

Trigger a new scrape job and observe:
- If a listing times out, the orchestrator should re-enqueue it
- Check logs for "Re-enqueueing" messages
- After retry timeout, should see "marking as Failed"

**Step 4: Commit any fixes**

If any issues found, fix and commit.

---

## Summary

The implementation adds:
1. `RetryCount` column to `ScrapeRunListings` table
2. `EnqueueScrapeRetryActivity` - re-enqueues URLs to scrape-work queue
3. `IncrementRetryCountActivity` - tracks retry attempts
4. Updated `ListingEtlOrchestrator` - detects missing blobs and retries once before failing

The retry flow:
1. Blob missing after initial 5-minute timeout
2. Increment `RetryCount` (0 → 1)
3. Re-enqueue URL to scrape-work queue
4. Wait another 5 minutes for blob
5. If still missing, mark as "Failed"
6. If `RetryCount` was already >= 1, skip retry and mark as "Failed" immediately
