# Event-Driven ETL Simplification Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace batch-50 orchestrator polling with fire-and-forget event-driven processing using simplified blob paths and a junction table for progress tracking.

**Architecture:** JobOrchestrator submits all scrape jobs without waiting. Blob triggers fire independently for each listing. ListingEtlOrchestrator looks up ScrapeRunId from junction table and updates progress after processing.

**Tech Stack:** .NET 8, Azure Functions v4, Durable Functions, Azure Blob Storage, SQLite/SQL Server, NUnit/Moq

---

## Phase 1: Database Schema (Junction Table)

### Task 1.1: Create ScrapeRunListings Migration

**Files:**
- Create: `AIOMarketMaker.Core/Data/Migrations/022_CreateScrapeRunListingsTable.sql`

**Step 1: Create the migration file**

```sql
-- Migration: 022_CreateScrapeRunListingsTable
-- Description: Junction table linking ScrapeRuns to Listings for progress tracking
-- Date: 2026-01-27

CREATE TABLE IF NOT EXISTS ScrapeRunListings (
    ScrapeRunId INT NOT NULL,
    ScrapeJobId INT NOT NULL,
    ListingId VARCHAR(20) NOT NULL,
    Status TEXT DEFAULT 'Pending',
    CreatedUtc DATETIME DEFAULT CURRENT_TIMESTAMP,
    CompletedUtc DATETIME NULL,
    PRIMARY KEY (ScrapeRunId, ListingId),
    FOREIGN KEY (ScrapeRunId) REFERENCES ScrapeRuns(Id) ON DELETE CASCADE,
    FOREIGN KEY (ScrapeJobId) REFERENCES ScrapeJobs(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_ScrapeRunListings_ListingId ON ScrapeRunListings(ListingId);
CREATE INDEX IF NOT EXISTS IX_ScrapeRunListings_Status ON ScrapeRunListings(ScrapeRunId, Status);
```

**Step 2: Commit**

```bash
cd <REPO_ROOT>
git add AIOMarketMaker.Core/Data/Migrations/022_CreateScrapeRunListingsTable.sql
git commit -m "feat: add ScrapeRunListings junction table migration"
```

---

### Task 1.2: Create ScrapeRunListing Model

**Files:**
- Create: `AIOMarketMaker.Core/Data/Models/ScrapeRunListing.cs`

**Step 1: Create the model**

```csharp
namespace AIOMarketMaker.Core.Data.Models;

/// <summary>
/// Junction table linking ScrapeRuns to Listings for progress tracking.
/// </summary>
public class ScrapeRunListing
{
    public int ScrapeRunId { get; set; }
    public int ScrapeJobId { get; set; }
    public required string ListingId { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }

    // Navigation properties
    public ScrapeRun? ScrapeRun { get; set; }
    public ScrapeJob? ScrapeJob { get; set; }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Core/Data/Models/ScrapeRunListing.cs
git commit -m "feat: add ScrapeRunListing model"
```

---

### Task 1.3: Add DbSet to EtlDbContext

**Files:**
- Modify: `AIOMarketMaker.Core/Data/EtlDbContext.cs`

**Step 1: Add DbSet and entity configuration**

Add after line 23 (after `public DbSet<ScrapeRun> ScrapeRuns`):
```csharp
    public DbSet<ScrapeRunListing> ScrapeRunListings { get; set; } = null!;
```

Add after line 98 (before the closing brace of `OnModelCreating`):
```csharp
        modelBuilder.Entity<ScrapeRunListing>(entity =>
        {
            entity.ToTable("ScrapeRunListings");
            entity.HasKey(e => new { e.ScrapeRunId, e.ListingId });

            entity.Property(e => e.ListingId).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Status).HasMaxLength(20).HasDefaultValue("Pending");
            entity.Property(e => e.CreatedUtc).HasDefaultValueSql(dateDefaultSql);

            entity.HasIndex(e => e.ListingId);
            entity.HasIndex(e => new { e.ScrapeRunId, e.Status });

            entity.HasOne(e => e.ScrapeRun)
                .WithMany()
                .HasForeignKey(e => e.ScrapeRunId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.ScrapeJob)
                .WithMany()
                .HasForeignKey(e => e.ScrapeJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });
```

**Step 2: Build to verify**

Run: `dotnet build AIOMarketMaker.Core/AIOMarketMaker.Core.csproj`

Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker.Core/Data/EtlDbContext.cs
git commit -m "feat: add ScrapeRunListings DbSet to EtlDbContext"
```

---

## Phase 2: Scraper Blob Path Simplification

### Task 2.1: Update BlobPathBuilder for Simple Paths

**Files:**
- Modify: `AIOWebScraper/AIOWebScraper.Storage.Azure/BlobPathBuilder.cs`
- Test: `AIOWebScraper/AIOWebScraper.Tests/Unit/BlobPathBuilder_UnitTests.cs`

**Step 1: Write the failing test**

Add to existing test file or create new:
```csharp
using NUnit.Framework;
using ScraperWorker.Services;

namespace AIOWebScraper.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class BlobPathBuilder_SimplePath_UnitTests
{
    [Test]
    public void Should_use_simple_path_when_useSimplePath_is_true()
    {
        var result = BlobPathBuilder.Build(
            jobId: "job123",
            url: "https://ebay.com/itm/12345",
            groupId: "12345",
            fileKey: "listing",
            useSimplePath: true);

        Assert.That(result, Is.EqualTo("12345/listing.html"));
    }

    [Test]
    public void Should_use_grouped_path_when_useSimplePath_is_false()
    {
        var result = BlobPathBuilder.Build(
            jobId: "job123",
            url: "https://ebay.com/itm/12345",
            groupId: "12345",
            fileKey: "listing",
            useSimplePath: false);

        Assert.That(result, Is.EqualTo("job123/12345/listing.html"));
    }

    [Test]
    public void Should_default_to_grouped_path_for_backward_compatibility()
    {
        var result = BlobPathBuilder.Build(
            jobId: "job123",
            url: "https://ebay.com/itm/12345",
            groupId: "12345",
            fileKey: "listing");

        Assert.That(result, Is.EqualTo("job123/12345/listing.html"));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOWebScraper/AIOWebScraper.Tests --filter "FullyQualifiedName~BlobPathBuilder_SimplePath" -v n`

Expected: FAIL - No overload with useSimplePath parameter

**Step 3: Update BlobPathBuilder implementation**

Replace contents of `BlobPathBuilder.cs`:
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
    /// If useSimplePath is true: {groupId}/{fileKey}.html (no jobId prefix)
    /// If GroupId and FileKey are both provided: {jobId}/{groupId}/{fileKey}.html
    /// Otherwise uses legacy format: {jobId}/{sanitizedUrl}.html
    /// </summary>
    public static string Build(string jobId, string url, string? groupId, string? fileKey, bool useSimplePath = false)
    {
        if (!string.IsNullOrEmpty(groupId) && !string.IsNullOrEmpty(fileKey))
        {
            if (useSimplePath)
            {
                return $"{groupId}/{fileKey}.html";
            }
            return $"{jobId}/{groupId}/{fileKey}.html";
        }

        var safeUrl = Regex.Replace(url, @"[^\w\-]", "_");
        return $"{jobId}/{safeUrl}.html";
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test AIOWebScraper/AIOWebScraper.Tests --filter "FullyQualifiedName~BlobPathBuilder_SimplePath" -v n`

Expected: PASS (3 tests)

**Step 5: Commit**

```bash
cd <EXTERNAL_SCRAPER_REPO>
git add AIOWebScraper.Storage.Azure/BlobPathBuilder.cs AIOWebScraper.Tests/Unit/BlobPathBuilder_UnitTests.cs
git commit -m "feat: add useSimplePath option to BlobPathBuilder"
```

---

### Task 2.2: Update AzureJobRepository to Use Simple Paths

**Files:**
- Modify: `AIOWebScraper/AIOWebScraper.Storage.Azure/AzureJobRepository.cs`

**Step 1: Find and update SaveContentAsync**

The method currently calls `BlobPathBuilder.Build(jobId, url, groupId, fileKey)`. Update to:
```csharp
var blobName = BlobPathBuilder.Build(jobId, url, groupId, fileKey, useSimplePath: true);
```

**Step 2: Find and update GetFileContentsAsync**

Similarly update:
```csharp
var blobName = BlobPathBuilder.Build(jobId, url, groupId, fileKey, useSimplePath: true);
```

**Step 3: Run existing tests**

Run: `dotnet test AIOWebScraper/AIOWebScraper.Tests --filter Category=Unit -v n`

Expected: All tests pass

**Step 4: Commit**

```bash
git add AIOWebScraper.Storage.Azure/AzureJobRepository.cs
git commit -m "feat: use simple blob paths (no jobId prefix) in AzureJobRepository"
```

---

## Phase 3: Update Blob Triggers

### Task 3.1: Simplify ListingBlobTrigger Path

**Files:**
- Modify: `AIOMarketMaker.Etl/Triggers/ListingBlobTrigger.cs`

**Step 1: Update the trigger path pattern**

Change line 20 from:
```csharp
[BlobTrigger("html/{jobId}/{listingId}/listing.html", Connection = "blobStorageConnectionString")] string html,
```

To:
```csharp
[BlobTrigger("html/{listingId}/listing.html", Connection = "blobStorageConnectionString")] string html,
```

**Step 2: Update method signature and logic**

Remove `jobId` parameter (line 22-23) and update the method:
```csharp
[Function("OnListingBlobCreated")]
public async Task Run(
    [BlobTrigger("html/{listingId}/listing.html", Connection = "blobStorageConnectionString")] string html,
    [DurableClient] DurableTaskClient client,
    string listingId)
{
    var instanceId = $"etl-{listingId}";
    _logger.LogInformation("Listing blob trigger fired for {ListingId}", listingId);

    var existingInstance = await client.GetInstanceAsync(instanceId);
    if (existingInstance == null)
    {
        _logger.LogInformation("Starting new orchestration {InstanceId}", instanceId);
        await client.ScheduleNewOrchestrationInstanceAsync(
            "ListingEtlOrchestrator",
            new ListingEtlInput(listingId, TriggerSource.Listing),
            new StartOrchestrationOptions { InstanceId = instanceId });
    }
    else
    {
        _logger.LogInformation("Orchestration {InstanceId} already exists, raising event", instanceId);
        await client.RaiseEventAsync(instanceId, "listing-ready", true);
    }
}
```

**Step 3: Commit**

```bash
cd <REPO_ROOT>
git add AIOMarketMaker.Etl/Triggers/ListingBlobTrigger.cs
git commit -m "feat: simplify ListingBlobTrigger path to html/{listingId}/listing.html"
```

---

### Task 3.2: Simplify DescriptionBlobTrigger Path

**Files:**
- Modify: `AIOMarketMaker.Etl/Triggers/DescriptionBlobTrigger.cs`

**Step 1: Update the trigger path pattern and method**

```csharp
[Function("OnDescriptionBlobCreated")]
public async Task Run(
    [BlobTrigger("html/{listingId}/description.html", Connection = "blobStorageConnectionString")] string html,
    [DurableClient] DurableTaskClient client,
    string listingId)
{
    var instanceId = $"etl-{listingId}";
    _logger.LogInformation("Description blob trigger fired for {ListingId}", listingId);

    var existingInstance = await client.GetInstanceAsync(instanceId);
    if (existingInstance == null)
    {
        _logger.LogInformation("Starting new orchestration {InstanceId}", instanceId);
        await client.ScheduleNewOrchestrationInstanceAsync(
            "ListingEtlOrchestrator",
            new ListingEtlInput(listingId, TriggerSource.Description),
            new StartOrchestrationOptions { InstanceId = instanceId });
    }
    else
    {
        _logger.LogInformation("Orchestration {InstanceId} already exists, raising event", instanceId);
        await client.RaiseEventAsync(instanceId, "description-ready", true);
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Triggers/DescriptionBlobTrigger.cs
git commit -m "feat: simplify DescriptionBlobTrigger path to html/{listingId}/description.html"
```

---

### Task 3.3: Update ListingEtlInput Model

**Files:**
- Modify: `AIOMarketMaker.Etl/Models/ListingEtlInput.cs`

**Step 1: Update the record to remove JobId**

Change line 9-13 from:
```csharp
public record ListingEtlInput(
    string JobId,
    string ListingId,
    TriggerSource TriggerSource
);
```

To:
```csharp
public record ListingEtlInput(
    string ListingId,
    TriggerSource TriggerSource
);
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Models/ListingEtlInput.cs
git commit -m "refactor: remove JobId from ListingEtlInput (no longer needed)"
```

---

## Phase 4: Update ListingEtlOrchestrator

### Task 4.1: Create LookupScrapeRunActivity

**Files:**
- Create: `AIOMarketMaker.Etl/Activities/LookupScrapeRunActivity.cs`

**Step 1: Create the activity**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Activities;

public record ScrapeRunLookupResult(int? ScrapeRunId, int? ScrapeJobId, bool Found);

public class LookupScrapeRunActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<LookupScrapeRunActivity> _logger;

    public LookupScrapeRunActivity(EtlDbContext dbContext, ILogger<LookupScrapeRunActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(LookupScrapeRunActivity))]
    public async Task<ScrapeRunLookupResult> Run([ActivityTrigger] string listingId)
    {
        var mapping = await _dbContext.ScrapeRunListings
            .Where(s => s.ListingId == listingId && s.Status == "Pending")
            .OrderByDescending(s => s.CreatedUtc)
            .FirstOrDefaultAsync();

        if (mapping == null)
        {
            _logger.LogWarning("No pending ScrapeRunListing found for {ListingId}", listingId);
            return new ScrapeRunLookupResult(null, null, false);
        }

        _logger.LogInformation("Found ScrapeRunListing for {ListingId}: ScrapeRunId={ScrapeRunId}, ScrapeJobId={ScrapeJobId}",
            listingId, mapping.ScrapeRunId, mapping.ScrapeJobId);

        return new ScrapeRunLookupResult(mapping.ScrapeRunId, mapping.ScrapeJobId, true);
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Activities/LookupScrapeRunActivity.cs
git commit -m "feat: add LookupScrapeRunActivity for junction table lookup"
```

---

### Task 4.2: Create UpdateScrapeRunListingActivity

**Files:**
- Create: `AIOMarketMaker.Etl/Activities/UpdateScrapeRunListingActivity.cs`

**Step 1: Create the activity**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Activities;

public record UpdateScrapeRunListingInput(int ScrapeRunId, string ListingId, string Status);

public class UpdateScrapeRunListingActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<UpdateScrapeRunListingActivity> _logger;

    public UpdateScrapeRunListingActivity(EtlDbContext dbContext, ILogger<UpdateScrapeRunListingActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(UpdateScrapeRunListingActivity))]
    public async Task Run([ActivityTrigger] UpdateScrapeRunListingInput input)
    {
        var mapping = await _dbContext.ScrapeRunListings
            .FirstOrDefaultAsync(s => s.ScrapeRunId == input.ScrapeRunId && s.ListingId == input.ListingId);

        if (mapping == null)
        {
            _logger.LogWarning("ScrapeRunListing not found for ScrapeRunId={ScrapeRunId}, ListingId={ListingId}",
                input.ScrapeRunId, input.ListingId);
            return;
        }

        mapping.Status = input.Status;
        if (input.Status == "Complete" || input.Status == "Failed")
        {
            mapping.CompletedUtc = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();

        // If completing, also increment ScrapeRun progress
        if (input.Status == "Complete")
        {
            await _dbContext.Database.ExecuteSqlRawAsync(@"
                UPDATE ScrapeRuns
                SET ListingsProcessed = ListingsProcessed + 1,
                    Status = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                  AND Status = 'Running' THEN 'Completed' ELSE Status END,
                    CompletedUtc = CASE WHEN ListingsProcessed + 1 >= TotalListingsFound
                                        AND Status = 'Running' THEN datetime('now') ELSE CompletedUtc END
                WHERE Id = {0}", input.ScrapeRunId);
        }

        _logger.LogInformation("Updated ScrapeRunListing {ListingId} to {Status}", input.ListingId, input.Status);
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Activities/UpdateScrapeRunListingActivity.cs
git commit -m "feat: add UpdateScrapeRunListingActivity for progress tracking"
```

---

### Task 4.3: Update ListingEtlOrchestrator

**Files:**
- Modify: `AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs`

**Step 1: Update to use junction table lookup**

Replace the entire file:
```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Orchestrators;

public class ListingEtlOrchestrator
{
    private const int TimeoutMinutes = 5;

    [Function(nameof(ListingEtlOrchestrator))]
    public async Task Run([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<ListingEtlOrchestrator>();
        var input = context.GetInput<ListingEtlInput>()!;

        logger.LogInformation(
            "Starting ETL orchestration for listing {ListingId} (triggered by {Source})",
            input.ListingId, input.TriggerSource);

        // Step 1: Lookup ScrapeRun from junction table
        var lookup = await context.CallActivityAsync<ScrapeRunLookupResult>(
            nameof(LookupScrapeRunActivity), input.ListingId);

        if (!lookup.Found)
        {
            logger.LogWarning("No pending ScrapeRunListing found for {ListingId}, skipping", input.ListingId);
            return;
        }

        // Step 2: Check what blobs exist (using simplified path)
        var checkInput = new CheckBlobsInput(input.ListingId);
        var state = await context.CallActivityAsync<BlobState>(
            nameof(CheckBlobsActivity), checkInput);

        // Step 3: Wait for partner if needed
        if (!state.HasBoth)
        {
            logger.LogInformation(
                "Waiting for {MissingBlob} blob for listing {ListingId}",
                state.MissingBlob, input.ListingId);

            var timeout = context.CurrentUtcDateTime.AddMinutes(TimeoutMinutes);
            using var cts = new CancellationTokenSource();

            var timeoutTask = context.CreateTimer(timeout, cts.Token);
            var eventName = state.MissingBlob == "listing" ? "listing-ready" : "description-ready";
            var partnerEvent = context.WaitForExternalEvent<bool>(eventName);

            var winner = await Task.WhenAny(timeoutTask, partnerEvent);

            if (winner == partnerEvent)
            {
                cts.Cancel();
                logger.LogInformation("Partner blob arrived for listing {ListingId}", input.ListingId);
                state = await context.CallActivityAsync<BlobState>(
                    nameof(CheckBlobsActivity), checkInput);
            }
            else
            {
                logger.LogWarning(
                    "Timeout waiting for {MissingBlob} blob for listing {ListingId}",
                    state.MissingBlob, input.ListingId);
            }
        }

        // Step 4: Process listing
        var processInput = new ProcessListingInput(
            input.ListingId,
            lookup.ScrapeJobId!.Value,
            state.HasDescription
        );

        await context.CallActivityAsync(nameof(ProcessListingActivity), processInput);

        // Step 5: Update junction table status
        await context.CallActivityAsync(
            nameof(UpdateScrapeRunListingActivity),
            new UpdateScrapeRunListingInput(lookup.ScrapeRunId!.Value, input.ListingId, "Complete"));

        logger.LogInformation(
            "ETL orchestration completed for listing {ListingId} (hasDescription={HasDescription})",
            input.ListingId, state.HasDescription);
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Orchestrators/ListingEtlOrchestrator.cs
git commit -m "feat: update ListingEtlOrchestrator to use junction table for progress"
```

---

### Task 4.4: Update CheckBlobsActivity for Simple Paths

**Files:**
- Modify: `AIOMarketMaker.Etl/Activities/CheckBlobsActivity.cs`
- Modify: `AIOMarketMaker.Etl/Models/ListingEtlInput.cs`

**Step 1: Add CheckBlobsInput record**

Add to `ListingEtlInput.cs`:
```csharp
public record CheckBlobsInput(string ListingId);
```

**Step 2: Update CheckBlobsActivity**

```csharp
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Activities;

public class CheckBlobsActivity
{
    private readonly BlobServiceClient _blobService;
    private readonly ILogger<CheckBlobsActivity> _logger;

    public CheckBlobsActivity(BlobServiceClient blobService, ILogger<CheckBlobsActivity> logger)
    {
        _blobService = blobService;
        _logger = logger;
    }

    [Function(nameof(CheckBlobsActivity))]
    public async Task<BlobState> Run([ActivityTrigger] CheckBlobsInput input)
    {
        var container = _blobService.GetBlobContainerClient("html");

        // Simple paths: html/{listingId}/listing.html
        var listingBlobPath = $"{input.ListingId}/listing.html";
        var descriptionBlobPath = $"{input.ListingId}/description.html";

        var listingBlob = container.GetBlobClient(listingBlobPath);
        var descriptionBlob = container.GetBlobClient(descriptionBlobPath);

        var hasListing = await listingBlob.ExistsAsync();
        var hasDescription = await descriptionBlob.ExistsAsync();

        _logger.LogInformation(
            "Blob check for {ListingId}: listing={HasListing}, description={HasDescription}",
            input.ListingId, hasListing.Value, hasDescription.Value);

        string? missingBlob = null;
        if (!hasListing.Value) missingBlob = "listing";
        else if (!hasDescription.Value) missingBlob = "description";

        return new BlobState(hasListing.Value, hasDescription.Value, missingBlob);
    }
}
```

**Step 3: Commit**

```bash
git add AIOMarketMaker.Etl/Activities/CheckBlobsActivity.cs AIOMarketMaker.Etl/Models/ListingEtlInput.cs
git commit -m "feat: update CheckBlobsActivity for simplified blob paths"
```

---

### Task 4.5: Update ProcessListingActivity

**Files:**
- Modify: `AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs`
- Modify: `AIOMarketMaker.Etl/Models/ListingEtlInput.cs`

**Step 1: Update ProcessListingInput record**

Change in `ListingEtlInput.cs` from:
```csharp
public record ProcessListingInput(
    string JobId,
    string ListingId,
    int ScrapeJobId,
    bool HasDescription
);
```

To:
```csharp
public record ProcessListingInput(
    string ListingId,
    int ScrapeJobId,
    bool HasDescription
);
```

**Step 2: Update ProcessListingActivity**

Update the blob paths to use simple format (remove JobId):

Change lines 38-39 from:
```csharp
var listingBlobPath = $"{input.JobId}/{input.ListingId}/listing.html";
```

To:
```csharp
var listingBlobPath = $"{input.ListingId}/listing.html";
```

Change line 57 from:
```csharp
var descBlobPath = $"{input.JobId}/{input.ListingId}/description.html";
```

To:
```csharp
var descBlobPath = $"{input.ListingId}/description.html";
```

Remove the progress update SQL at lines 131-143 (this is now handled by UpdateScrapeRunListingActivity).

**Step 3: Commit**

```bash
git add AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs AIOMarketMaker.Etl/Models/ListingEtlInput.cs
git commit -m "feat: update ProcessListingActivity for simplified blob paths"
```

---

## Phase 5: Update JobOrchestrator (Fire-and-Forget)

### Task 5.1: Create InsertScrapeRunListingsActivity

**Files:**
- Create: `AIOMarketMaker.Etl/Activities/InsertScrapeRunListingsActivity.cs`

**Step 1: Create the activity**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Etl.Activities;

public record InsertScrapeRunListingsInput(int ScrapeRunId, int ScrapeJobId, List<string> ListingIds);

public class InsertScrapeRunListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<InsertScrapeRunListingsActivity> _logger;

    public InsertScrapeRunListingsActivity(EtlDbContext dbContext, ILogger<InsertScrapeRunListingsActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(InsertScrapeRunListingsActivity))]
    public async Task Run([ActivityTrigger] InsertScrapeRunListingsInput input)
    {
        var listings = input.ListingIds.Select(listingId => new ScrapeRunListing
        {
            ScrapeRunId = input.ScrapeRunId,
            ScrapeJobId = input.ScrapeJobId,
            ListingId = listingId,
            Status = "Pending",
            CreatedUtc = DateTime.UtcNow
        });

        _dbContext.ScrapeRunListings.AddRange(listings);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Inserted {Count} ScrapeRunListings for ScrapeRunId={ScrapeRunId}",
            input.ListingIds.Count, input.ScrapeRunId);
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Activities/InsertScrapeRunListingsActivity.cs
git commit -m "feat: add InsertScrapeRunListingsActivity for bulk junction table inserts"
```

---

### Task 5.2: Create SubmitScrapeJobsActivity (Fire-and-Forget)

**Files:**
- Create: `AIOMarketMaker.Etl/Activities/SubmitScrapeJobsActivity.cs`

**Step 1: Create the activity**

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Etl.Activities;

public record SubmitScrapeJobsInput(List<string> ListingIds);

public class SubmitScrapeJobsActivity
{
    private readonly IWebscraperClient _webScraper;
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly ILogger<SubmitScrapeJobsActivity> _logger;

    public SubmitScrapeJobsActivity(
        IWebscraperClient webScraper,
        IEbayUrlBuilder urlBuilder,
        ILogger<SubmitScrapeJobsActivity> logger)
    {
        _webScraper = webScraper;
        _urlBuilder = urlBuilder;
        _logger = logger;
    }

    [Function(nameof(SubmitScrapeJobsActivity))]
    public async Task Run([ActivityTrigger] SubmitScrapeJobsInput input)
    {
        _logger.LogInformation("Submitting {Count} listing+description scrape jobs", input.ListingIds.Count);

        foreach (var listingId in input.ListingIds)
        {
            try
            {
                // Submit listing page scrape
                var listingUrl = _urlBuilder.BuildListingUrl(listingId);
                await _webScraper.NewJobAsync(
                    new[] { listingUrl },
                    groupId: listingId,
                    fileKey: "listing");

                // Submit description page scrape (parallel - don't wait for listing)
                var descriptionUrl = _urlBuilder.BuildDescriptionUrl(listingId);
                await _webScraper.NewJobAsync(
                    new[] { descriptionUrl },
                    groupId: listingId,
                    fileKey: "description");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to submit scrape jobs for {ListingId}", listingId);
                // Continue with other listings - don't fail the whole batch
            }
        }

        _logger.LogInformation("Submitted all scrape jobs for {Count} listings", input.ListingIds.Count);
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Activities/SubmitScrapeJobsActivity.cs
git commit -m "feat: add SubmitScrapeJobsActivity for fire-and-forget scrape submission"
```

---

### Task 5.3: Update JobOrchestrator to Fire-and-Forget

**Files:**
- Modify: `AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs`

**Step 1: Replace batch-50 logic with fire-and-forget**

Replace lines 147-220 (the batch fetching section) with:

```csharp
            // Step 4: Insert junction table entries
            await context.CallActivityAsync(
                nameof(InsertScrapeRunListingsActivity),
                new InsertScrapeRunListingsInput(
                    int.Parse(scrapeInstanceId.Split('-').Last()), // Extract ScrapeRunId from instanceId
                    jobId,
                    newListingIds));

            // Step 5: Submit all scrape jobs (fire-and-forget)
            // Blob triggers will handle the rest
            await context.CallActivityAsync(
                nameof(SubmitScrapeJobsActivity),
                new SubmitScrapeJobsInput(newListingIds));

            // Report progress: Scraping (blob triggers will update ListingsProcessed)
            await context.CallActivityAsync(
                nameof(UpdateScrapeRunProgressActivity),
                new UpdateProgressInput(scrapeInstanceId,
                    TotalListingsFound: newListingIds.Count,
                    ListingsProcessed: 0,
                    CurrentPhase: "Scraping"));

            // Step 6: Update job timestamp
            await context.CallActivityAsync(nameof(UpdateJobTimestampActivity), jobId);

            logger.LogInformation("Job {JobId} submitted {Count} listings for scraping (fire-and-forget)",
                jobId, newListingIds.Count);

            return new JobResult(jobId, true, newListingIds.Count, null);
```

Also remove the `SaveListingsActivity` call (now handled by ProcessListingActivity via blob triggers).

**Step 2: Remove FetchListingOrchestrator calls**

Delete or comment out the batch fetching code that calls `FetchListingOrchestrator`.

**Step 3: Build to verify**

Run: `dotnet build AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`

Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs
git commit -m "feat: replace batch-50 with fire-and-forget scrape submission"
```

---

## Phase 6: Verification

### Task 6.1: Run All Unit Tests

**Step 1: Run AIOWebScraper tests**

Run: `dotnet test AIOWebScraper/AIOWebScraper.Tests --filter Category=Unit -v n`

Expected: All tests pass

**Step 2: Run AIOMarketMaker tests**

Run: `dotnet test AIOMarketMaker.Tests --filter Category=Unit -v n`

Expected: All tests pass

**Step 3: Build both solutions**

Run: `dotnet build AIOWebScraper/AIOWebScraper.sln && dotnet build AIOMarketMaker/AIOMarketMaker.sln`

Expected: Build succeeded (0 errors)

**Step 4: Final commit**

```bash
cd <REPO_ROOT>
git add -A
git commit -m "feat: complete event-driven ETL simplification

- Simplified blob paths: html/{listingId}/listing.html
- Added ScrapeRunListings junction table for progress tracking
- Fire-and-forget scrape submission (no batch-50)
- Parallel listing + description scraping
- Per-listing progress updates via blob triggers"
```

---

## Summary

| Phase | Tasks | Description |
|-------|-------|-------------|
| 1 | 1.1-1.3 | Database schema (junction table) |
| 2 | 2.1-2.2 | Scraper blob path simplification |
| 3 | 3.1-3.3 | Update blob triggers |
| 4 | 4.1-4.5 | Update ListingEtlOrchestrator |
| 5 | 5.1-5.3 | Update JobOrchestrator (fire-and-forget) |
| 6 | 6.1 | Verification |

**Total tasks:** 14
**Estimated commits:** 16
