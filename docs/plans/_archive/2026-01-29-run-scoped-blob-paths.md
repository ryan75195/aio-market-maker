# Run-Scoped Blob Paths Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix the race condition where duplicate scrape runs for the same search term cause stalled jobs by making blob paths and orchestration IDs unique per scrape run.

**Architecture:** Change blob paths from `html/{listingId}/listing.html` to `html/{scrapeRunId}/{listingId}/listing.html`. This isolates each run's blobs, ensuring blob triggers fire independently per run. Add SQL MERGE upsert to handle concurrent database writes.

**Tech Stack:** .NET 8, Azure Functions, Durable Functions, Azure Blob Storage, SQL Server, Entity Framework Core

---

## Summary of Changes

| Component | Current | New |
|-----------|---------|-----|
| Blob path | `{listingId}/{fileKey}.html` | `{scrapeRunId}/{listingId}/{fileKey}.html` |
| Blob trigger | `html/{listingId}/listing.html` | `html/{scrapeRunId}/{listingId}/listing.html` |
| Orchestration ID | `etl-{listingId}` | `etl-{scrapeRunId}-{listingId}` |
| DB write | Check-then-insert (race) | SQL MERGE (atomic upsert) |

---

## Task 1: Add ScrapeRunId to Queue Message

**Files:**
- Modify: `AIOWebScraper/AIOWebScraper.Storage.Azure/QueueMessage.cs:7-60`

**Step 1: Add ScrapeRunId property to ScrapeQueueMessage**

```csharp
// In ScrapeQueueMessage record, add after FileKey property (line ~54):

/// <summary>
/// Optional scrape run ID for run-scoped blob paths.
/// When provided, blob path becomes: {ScrapeRunId}/{GroupId}/{FileKey}.html
/// </summary>
public int? ScrapeRunId { get; init; }
```

**Step 2: Commit**

```bash
git add AIOWebScraper/AIOWebScraper.Storage.Azure/QueueMessage.cs
git commit -m "feat: add ScrapeRunId to ScrapeQueueMessage for run-scoped paths"
```

---

## Task 2: Update BlobPathBuilder to Support ScrapeRunId

**Files:**
- Modify: `AIOWebScraper/AIOWebScraper.Storage.Azure/BlobPathBuilder.cs`
- Create: `AIOWebScraper/AIOWebScraper.Tests/Unit/BlobPathBuilder_UnitTests.cs`

**Step 1: Write failing tests for new path format**

```csharp
using NUnit.Framework;
using ScraperWorker.Services;

namespace AIOWebScraper.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class BlobPathBuilder_UnitTests
{
    [Test]
    public void Should_include_scrapeRunId_when_provided_with_simple_path()
    {
        var result = BlobPathBuilder.Build(
            jobId: "job123",
            url: "https://example.com",
            groupId: "listing456",
            fileKey: "listing",
            useSimplePath: true,
            scrapeRunId: 17030);

        Assert.That(result, Is.EqualTo("17030/listing456/listing.html"));
    }

    [Test]
    public void Should_use_legacy_format_when_scrapeRunId_is_null()
    {
        var result = BlobPathBuilder.Build(
            jobId: "job123",
            url: "https://example.com",
            groupId: "listing456",
            fileKey: "listing",
            useSimplePath: true,
            scrapeRunId: null);

        Assert.That(result, Is.EqualTo("listing456/listing.html"));
    }

    [Test]
    public void Should_include_scrapeRunId_in_grouped_format()
    {
        var result = BlobPathBuilder.Build(
            jobId: "job123",
            url: "https://example.com",
            groupId: "listing456",
            fileKey: "description",
            useSimplePath: false,
            scrapeRunId: 17030);

        Assert.That(result, Is.EqualTo("job123/17030/listing456/description.html"));
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
cd AIOWebScraper
dotnet test --filter "FullyQualifiedName~BlobPathBuilder_UnitTests" -v n
```

Expected: FAIL - method signature doesn't accept scrapeRunId

**Step 3: Update BlobPathBuilder implementation**

Replace entire file:

```csharp
using System.Text.RegularExpressions;

namespace ScraperWorker.Services;

/// <summary>
/// Builds blob storage paths for scraped HTML content.
/// </summary>
public static class BlobPathBuilder
{
    /// <summary>
    /// Builds the blob path based on provided parameters.
    ///
    /// Path formats:
    /// - Simple with ScrapeRunId: {scrapeRunId}/{groupId}/{fileKey}.html
    /// - Simple without ScrapeRunId: {groupId}/{fileKey}.html (legacy)
    /// - Grouped with ScrapeRunId: {jobId}/{scrapeRunId}/{groupId}/{fileKey}.html
    /// - Grouped without ScrapeRunId: {jobId}/{groupId}/{fileKey}.html
    /// - Legacy: {jobId}/{sanitizedUrl}.html
    /// </summary>
    public static string Build(
        string jobId,
        string url,
        string? groupId,
        string? fileKey,
        bool useSimplePath = false,
        int? scrapeRunId = null)
    {
        if (!string.IsNullOrEmpty(groupId) && !string.IsNullOrEmpty(fileKey))
        {
            if (useSimplePath)
            {
                return scrapeRunId.HasValue
                    ? $"{scrapeRunId}/{groupId}/{fileKey}.html"
                    : $"{groupId}/{fileKey}.html";
            }
            return scrapeRunId.HasValue
                ? $"{jobId}/{scrapeRunId}/{groupId}/{fileKey}.html"
                : $"{jobId}/{groupId}/{fileKey}.html";
        }

        var safeUrl = Regex.Replace(url, @"[^\w\-]", "_");
        return $"{jobId}/{safeUrl}.html";
    }
}
```

**Step 4: Run tests to verify they pass**

```bash
cd AIOWebScraper
dotnet test --filter "FullyQualifiedName~BlobPathBuilder_UnitTests" -v n
```

Expected: PASS

**Step 5: Commit**

```bash
git add AIOWebScraper/AIOWebScraper.Storage.Azure/BlobPathBuilder.cs
git add AIOWebScraper/AIOWebScraper.Tests/Unit/BlobPathBuilder_UnitTests.cs
git commit -m "feat: add scrapeRunId support to BlobPathBuilder"
```

---

## Task 3: Update AzureJobRepository to Pass ScrapeRunId

**Files:**
- Modify: `AIOWebScraper/AIOWebScraper.Storage.Azure/AzureJobRepository.cs:105-115`

**Step 1: Update SaveHtmlAsync to accept scrapeRunId**

Find the `SaveHtmlAsync` method (around line 105) and update:

```csharp
public async Task SaveHtmlAsync(
    string jobId,
    string url,
    string html,
    string? groupId,
    string? fileKey,
    int? scrapeRunId,  // Add this parameter
    CancellationToken ct)
{
    var blobName = BlobPathBuilder.Build(jobId, url, groupId, fileKey, useSimplePath: true, scrapeRunId);
    // ... rest of method unchanged
}
```

**Step 2: Update IJobRepository interface**

Find `IJobRepository` interface and update signature to match.

**Step 3: Update all callers of SaveHtmlAsync**

Search for callers and add `scrapeRunId: null` for backward compatibility initially.

**Step 4: Commit**

```bash
git add AIOWebScraper/AIOWebScraper.Storage.Azure/AzureJobRepository.cs
git commit -m "feat: add scrapeRunId parameter to SaveHtmlAsync"
```

---

## Task 4: Update Worker to Extract and Use ScrapeRunId

**Files:**
- Modify: `AIOWebScraper/ScraperWorker/Services/SimpleQueueWorker.cs`

**Step 1: Find where blobs are saved and pass ScrapeRunId from queue message**

Locate the code that calls `SaveHtmlAsync` and update to pass `message.ScrapeRunId`:

```csharp
await _jobRepository.SaveHtmlAsync(
    message.JobId,
    message.Url,
    html,
    message.GroupId,
    message.FileKey,
    message.ScrapeRunId,  // Pass through from queue message
    ct);
```

**Step 2: Commit**

```bash
git add AIOWebScraper/ScraperWorker/Services/SimpleQueueWorker.cs
git commit -m "feat: pass ScrapeRunId to blob storage in worker"
```

---

## Task 5: Update SubmitScrapeJobsInput to Include ScrapeRunId

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Models/ListingEtlInput.cs:116`

**Step 1: Add ScrapeRunId to the record**

```csharp
// Change from:
public record SubmitScrapeJobsInput(List<string> ListingIds);

// To:
public record SubmitScrapeJobsInput(int ScrapeRunId, List<string> ListingIds);
```

**Step 2: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Models/ListingEtlInput.cs
git commit -m "feat: add ScrapeRunId to SubmitScrapeJobsInput"
```

---

## Task 6: Update JobOrchestrator to Pass ScrapeRunId

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs:173-175`

**Step 1: Update the call to SubmitScrapeJobsActivity**

```csharp
// Change from:
var submitResult = await context.CallActivityAsync<SubmitScrapeJobsResult>(
    nameof(SubmitScrapeJobsActivity),
    new SubmitScrapeJobsInput(newListingIds));

// To:
var submitResult = await context.CallActivityAsync<SubmitScrapeJobsResult>(
    nameof(SubmitScrapeJobsActivity),
    new SubmitScrapeJobsInput(scrapeRunId, newListingIds));
```

**Step 2: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs
git commit -m "feat: pass ScrapeRunId to SubmitScrapeJobsActivity"
```

---

## Task 7: Update IWebscraperClient Interface

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Core/Services/WebscraperClient.cs:14-20`

**Step 1: Add scrapeRunId parameter to NewJobAsync**

```csharp
public interface IWebscraperClient
{
    Task<StartResponse> NewJobAsync(
       IEnumerable<string> urls,
       IEnumerable<object>? proxies = null,
       string? correlationId = null,
       string? groupId = null,
       string? fileKey = null,
       int? scrapeRunId = null,  // Add this
       CancellationToken ct = default);
    // ... rest unchanged
}
```

**Step 2: Update implementation to include scrapeRunId in request body**

Find the `NewJobAsync` implementation and add `scrapeRunId` to the request payload.

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Services/WebscraperClient.cs
git commit -m "feat: add scrapeRunId to IWebscraperClient.NewJobAsync"
```

---

## Task 8: Update SubmitScrapeJobsActivity to Use ScrapeRunId

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Activities/SubmitScrapeJobsActivity.cs:43-55`

**Step 1: Pass scrapeRunId when calling NewJobAsync**

```csharp
// Submit listing page scrape
var listingUrl = _urlBuilder.BuildListingUrl(listingId);
await _webScraper.NewJobAsync(
    new[] { listingUrl },
    groupId: listingId,
    fileKey: "listing",
    scrapeRunId: input.ScrapeRunId);  // Add this

// Submit description page scrape
var descriptionUrl = _urlBuilder.BuildDescriptionUrl(listingId);
await _webScraper.NewJobAsync(
    new[] { descriptionUrl },
    groupId: listingId,
    fileKey: "description",
    scrapeRunId: input.ScrapeRunId);  // Add this
```

**Step 2: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/SubmitScrapeJobsActivity.cs
git commit -m "feat: pass ScrapeRunId when submitting scrape jobs"
```

---

## Task 9: Update Azure Functions API to Accept ScrapeRunId

**Files:**
- Modify: `AIOWebScraper/AIOWebScraper/NewJob.cs` (or similar controller)

**Step 1: Add scrapeRunId to the request model**

Find the request model for NewJob and add:

```csharp
public int? ScrapeRunId { get; set; }
```

**Step 2: Pass to queue message**

When creating `ScrapeQueueMessage`, include `ScrapeRunId = request.ScrapeRunId`.

**Step 3: Commit**

```bash
git add AIOWebScraper/AIOWebScraper/NewJob.cs
git commit -m "feat: accept scrapeRunId in NewJob API"
```

---

## Task 10: Update Blob Triggers with New Path Pattern

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Triggers/ListingBlobTrigger.cs:18-24`
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Triggers/DescriptionBlobTrigger.cs`

**Step 1: Update listing blob trigger path**

```csharp
// Change from:
[BlobTrigger("html/{listingId}/listing.html", Connection = "blobStorageConnectionString")] string html,
[DurableClient] DurableTaskClient client,
string listingId)

// To:
[BlobTrigger("html/{scrapeRunId}/{listingId}/listing.html", Connection = "blobStorageConnectionString")] string html,
[DurableClient] DurableTaskClient client,
int scrapeRunId,
string listingId)
```

**Step 2: Update orchestration instance ID to include scrapeRunId**

```csharp
// Change from:
var instanceId = $"etl-{listingId}";

// To:
var instanceId = $"etl-{scrapeRunId}-{listingId}";
```

**Step 3: Update ListingEtlInput to include ScrapeRunId**

Add scrapeRunId to the orchestration input so downstream activities can use it.

**Step 4: Do the same for DescriptionBlobTrigger**

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Triggers/ListingBlobTrigger.cs
git add AIOMarketMaker/AIOMarketMaker.Etl/Triggers/DescriptionBlobTrigger.cs
git commit -m "feat: update blob triggers for run-scoped paths"
```

---

## Task 11: Update ListingEtlInput and Orchestrator

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Models/ListingEtlInput.cs`
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs`

**Step 1: Add ScrapeRunId to ListingEtlInput**

```csharp
// Change from:
public record ListingEtlInput(string ListingId, TriggerSource TriggerSource);

// To:
public record ListingEtlInput(int ScrapeRunId, string ListingId, TriggerSource TriggerSource);
```

**Step 2: Update orchestrator to use ScrapeRunId from input**

The orchestrator should use `input.ScrapeRunId` instead of looking up from junction table.

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Models/ListingEtlInput.cs
git add AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs
git commit -m "feat: pass ScrapeRunId through ETL orchestration"
```

---

## Task 12: Implement SQL MERGE Upsert in ProcessListingActivity

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs:87-138`
- Create: `AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Activities/ProcessListingActivity_UnitTests.cs`

**Step 1: Write test for concurrent upsert behavior**

```csharp
[Test]
[Category("Integration")]
public async Task Should_handle_concurrent_inserts_without_exception()
{
    // This test verifies MERGE handles races gracefully
    // Implementation depends on your test infrastructure
}
```

**Step 2: Replace check-then-insert with SQL MERGE**

Replace lines 87-138 with:

```csharp
// Use SQL MERGE for atomic upsert
var mergeResult = await _dbContext.Database.ExecuteSqlRawAsync(@"
    MERGE INTO Listings WITH (HOLDLOCK) AS target
    USING (SELECT @p0 AS ListingId) AS source
    ON target.ListingId = source.ListingId
    WHEN MATCHED THEN
        UPDATE SET
            Title = @p1,
            Price = @p2,
            Currency = @p3,
            ShippingCost = @p4,
            Condition = @p5,
            ListingStatus = @p6,
            PurchaseFormat = @p7,
            ItemSpecifics = @p8,
            Images = @p9,
            Location = @p10,
            EndDateUtc = @p11,
            Url = @p12,
            Description = @p13,
            DescriptionStatus = @p14,
            UpdatedUtc = GETUTCDATE()
    WHEN NOT MATCHED THEN
        INSERT (ListingId, ScrapeJobId, Title, Price, Currency, ShippingCost,
                Condition, ListingStatus, PurchaseFormat, ItemSpecifics, Images,
                Location, EndDateUtc, Url, Description, DescriptionStatus, CreatedUtc)
        VALUES (@p0, @p15, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, GETUTCDATE())
    OUTPUT $action;",
    input.ListingId,           // @p0
    extractedListing.title,    // @p1
    extractedListing.price,    // @p2
    // ... etc for all parameters
);

// MERGE OUTPUT: 'INSERT' means new, 'UPDATE' means existing
var isNew = mergeResult > 0; // Simplified - need to capture OUTPUT properly
```

**Note:** The actual implementation needs to use `FromSqlRaw` with OUTPUT capture to determine if INSERT or UPDATE occurred. This is a simplification.

**Step 3: Create helper method for cleaner code**

Consider creating a `UpsertListingAsync` method that encapsulates the MERGE logic and returns `bool isNew`.

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs
git commit -m "feat: implement SQL MERGE upsert for concurrent safety"
```

---

## Task 13: Update ProcessListingActivity Blob Path

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs:36-42,60-67`

**Step 1: Update ProcessListingInput to include ScrapeRunId**

```csharp
public record ProcessListingInput(
    string ListingId,
    int ScrapeJobId,
    int ScrapeRunId,  // Add this
    bool HasDescription
);
```

**Step 2: Update blob path construction to include ScrapeRunId**

```csharp
// Change from:
var listingBlobPath = $"{input.ListingId}/listing.html";

// To:
var listingBlobPath = $"{input.ScrapeRunId}/{input.ListingId}/listing.html";

// And for description:
var descBlobPath = $"{input.ScrapeRunId}/{input.ListingId}/description.html";
```

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs
git add AIOMarketMaker/AIOMarketMaker.Etl/Models/ListingEtlInput.cs
git commit -m "feat: use run-scoped blob paths in ProcessListingActivity"
```

---

## Task 14: Update CheckBlobsActivity for New Paths

**Files:**
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Activities/CheckBlobsActivity.cs`

**Step 1: Update blob path checks to include ScrapeRunId**

Ensure all blob existence checks use the new path format `{scrapeRunId}/{listingId}/{fileKey}.html`.

**Step 2: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/CheckBlobsActivity.cs
git commit -m "feat: update CheckBlobsActivity for run-scoped paths"
```

---

## Task 15: Add Blob Cleanup After ETL (Optional Enhancement)

**Files:**
- Create: `AIOMarketMaker/AIOMarketMaker.Etl/Activities/DeleteListingBlobsActivity.cs`
- Modify: `AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs`

**Step 1: Create DeleteListingBlobsActivity**

```csharp
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Etl.Activities;

public record DeleteBlobsInput(int ScrapeRunId, string ListingId);

public class DeleteListingBlobsActivity
{
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<DeleteListingBlobsActivity> _logger;

    public DeleteListingBlobsActivity(
        BlobServiceClient blobService,
        ILogger<DeleteListingBlobsActivity> logger)
    {
        _blobService = blobService;
        _logger = logger;
    }

    [Function(nameof(DeleteListingBlobsActivity))]
    public async Task Run([ActivityTrigger] DeleteBlobsInput input)
    {
        var container = _blobService.GetBlobContainerClient("html");

        var listingBlob = container.GetBlobClient($"{input.ScrapeRunId}/{input.ListingId}/listing.html");
        var descBlob = container.GetBlobClient($"{input.ScrapeRunId}/{input.ListingId}/description.html");

        await listingBlob.DeleteIfExistsAsync();
        await descBlob.DeleteIfExistsAsync();

        _logger.LogDebug("Deleted blobs for {ScrapeRunId}/{ListingId}", input.ScrapeRunId, input.ListingId);
    }
}
```

**Step 2: Call from orchestrator after successful processing**

In `ListingEtlOrchestrator.cs`, after `UpdateScrapeRunListingActivity`:

```csharp
// Clean up blobs after successful processing
await context.CallActivityAsync(
    nameof(DeleteListingBlobsActivity),
    new DeleteBlobsInput(input.ScrapeRunId, input.ListingId));
```

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/DeleteListingBlobsActivity.cs
git add AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs
git commit -m "feat: add blob cleanup after ETL completion"
```

---

## Task 16: Integration Testing

**Step 1: Start local environment**

```bash
/setup-local-env start --workers 5
```

**Step 2: Trigger duplicate scrape runs**

Use the UI or API to start two scrape runs for the same search term simultaneously.

**Step 3: Monitor with /monitor-scrape**

Verify both runs complete successfully without stalling.

**Step 4: Verify database**

```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker -Q "
SELECT Id, Status, CurrentPhase, TotalListingsFound, ListingsProcessed, ListingsAdded, ListingsSkipped
FROM ScrapeRuns
ORDER BY Id DESC" -W
```

Both runs should show Completed status.

---

## Rollback Plan

If issues arise:

1. Revert blob trigger paths to original format
2. Revert BlobPathBuilder to not include scrapeRunId
3. Deploy and verify existing functionality works

The changes are additive - old blobs without scrapeRunId in path will still be accessible.

---

## Success Criteria

1. Two simultaneous scrape runs for "PlayStation 5" both complete
2. Listings table has correct data (no unique constraint violations)
3. Both runs show accurate ListingsAdded/ListingsSkipped counts
4. No orphaned "Pending" ScrapeRunListings after runs complete
5. Blob storage shows paths like `html/17030/123456/listing.html`
