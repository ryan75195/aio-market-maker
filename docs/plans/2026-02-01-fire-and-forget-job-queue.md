# Fire-and-Forget Job Queue Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make the manual scrape trigger return immediately while jobs run in the background via a queue, preventing one stuck job from blocking others.

**Architecture:** The HTTP trigger creates ScrapeRun records and enqueues job messages to a new `scrape-jobs` queue. A queue-triggered function processes each job independently. This decouples job creation from job execution.

**Tech Stack:** Azure Functions (Queue Trigger), Azure Queue Storage, Entity Framework Core

---

## Current Problem

```
POST /api/scrape/start (2 jobs enabled)
└── foreach job  ← SEQUENTIAL, BLOCKING
    ├── Job 1: RunScrapeForJobAsync() → 3 min
    └── Job 2: RunScrapeForJobAsync() → HANGS → entire request times out
```

## New Architecture

```
POST /api/scrape/start (2 jobs enabled)
├── Create ScrapeRun for Job 1 → enqueue {runId: 123}
├── Create ScrapeRun for Job 2 → enqueue {runId: 124}
└── Return { runIds: [123, 124] }  ← IMMEDIATE (< 1 second)

[Queue: scrape-jobs]
├── {runId: 123} → ProcessScrapeJob() → runs independently
└── {runId: 124} → ProcessScrapeJob() → runs independently
```

---

### Task 1: Create Queue Message Model

**Files:**
- Create: `AIOMarketMaker.Etl/Models/ScrapeJobMessage.cs`

**Step 1: Create the message model**

```csharp
namespace AIOMarketMaker.Etl.Models;

/// <summary>
/// Message format for the scrape-jobs queue.
/// Contains all information needed to run a scrape for a job.
/// </summary>
public record ScrapeJobMessage(
    int ScrapeRunId,
    int JobId,
    string SearchTerm,
    string TriggerType
);
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Etl/Models/ScrapeJobMessage.cs
git commit -m "feat: add ScrapeJobMessage model for job queue"
```

---

### Task 2: Add Queue Client for scrape-jobs

**Files:**
- Modify: `AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs:24-40`

**Step 1: Add second queue client field**

In the class fields (around line 24), add:

```csharp
private readonly QueueClient _queueClient;
private readonly QueueClient _jobQueueClient;  // ADD THIS
private readonly IEbayUrlBuilder _urlBuilder;
```

**Step 2: Initialize in constructor**

In the constructor (around line 38), add:

```csharp
_queueClient = queueService.GetQueueClient("scrape-work");
_jobQueueClient = queueService.GetQueueClient("scrape-jobs");  // ADD THIS
_urlBuilder = new EbayUrlBuilder();
```

**Step 3: Commit**

```bash
git add AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs
git commit -m "feat: add scrape-jobs queue client"
```

---

### Task 3: Write Failing Test for Queue-Based Start

**Files:**
- Modify: `AIOMarketMaker.Tests/Unit/Triggers/SimplifiedScrapeTrigger_UnitTests.cs`

**Step 1: Add test for fire-and-forget behavior**

Add this test to the existing test class:

```csharp
[Test]
[Category("Unit")]
public async Task ManualScrape_should_enqueue_job_messages_and_return_immediately()
{
    // Arrange
    var job = new ScrapeJob { Id = 1, SearchTerm = "Test Product", IsEnabled = true };
    _dbContext.ScrapeJobs.Add(job);
    await _dbContext.SaveChangesAsync();

    var enqueuedMessages = new List<string>();
    var jobQueueMock = new Mock<QueueClient>();
    jobQueueMock
        .Setup(q => q.SendMessageAsync(It.IsAny<string>(), default))
        .Callback<string, CancellationToken>((msg, _) => enqueuedMessages.Add(msg))
        .ReturnsAsync(Mock.Of<Azure.Response<SendReceipt>>());

    _queueServiceMock
        .Setup(q => q.GetQueueClient("scrape-jobs"))
        .Returns(jobQueueMock.Object);

    var trigger = CreateTrigger();
    var request = CreateHttpRequest("POST", "/api/scrape/start");

    // Act
    var response = await trigger.RunManual(request);

    // Assert
    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    Assert.That(enqueuedMessages, Has.Count.EqualTo(1), "Should enqueue one job message");

    // Verify ScrapeRun was created with Queued status
    var scrapeRun = await _dbContext.ScrapeRuns.FirstOrDefaultAsync();
    Assert.That(scrapeRun, Is.Not.Null);
    Assert.That(scrapeRun!.Status, Is.EqualTo("Queued"));

    // Verify message contains correct data
    var message = JsonSerializer.Deserialize<ScrapeJobMessage>(enqueuedMessages[0]);
    Assert.That(message!.ScrapeRunId, Is.EqualTo(scrapeRun.Id));
    Assert.That(message.JobId, Is.EqualTo(job.Id));
    Assert.That(message.SearchTerm, Is.EqualTo("Test Product"));
}
```

**Step 2: Add required using statement**

At the top of the file, add:

```csharp
using AIOMarketMaker.Etl.Models;
```

**Step 3: Run test to verify it fails**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ManualScrape_should_enqueue_job_messages" -v n
```

Expected: FAIL (ScrapeJobMessage doesn't exist yet, or enqueue behavior not implemented)

**Step 4: Commit failing test**

```bash
git add AIOMarketMaker.Tests/Unit/Triggers/SimplifiedScrapeTrigger_UnitTests.cs
git commit -m "test: add failing test for fire-and-forget job queue"
```

---

### Task 4: Refactor ManualScrape to Enqueue Jobs

**Files:**
- Modify: `AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs:295-402`

**Step 1: Replace the RunManual method**

Replace the entire `RunManual` method with:

```csharp
/// <summary>
/// HTTP trigger for manual scrape invocation.
/// Creates ScrapeRun records and enqueues jobs for background processing.
/// Returns immediately with run IDs.
/// </summary>
[Function("ManualScrape")]
public async Task<HttpResponseData> RunManual(
    [HttpTrigger(AuthorizationLevel.Function, "post", Route = "scrape/start")] HttpRequestData req)
{
    _logger.LogInformation("Manual scrape trigger fired at {Time}", DateTime.UtcNow);

    // Parse optional request body
    ManualScrapeRequest? scrapeRequest = null;
    var requestBody = await req.ReadAsStringAsync();
    if (!string.IsNullOrWhiteSpace(requestBody))
    {
        try
        {
            scrapeRequest = JsonSerializer.Deserialize<ManualScrapeRequest>(requestBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse request body, using defaults");
        }
    }

    // Determine which jobs to run
    IEnumerable<(int Id, string SearchTerm)> jobsToRun;

    if (scrapeRequest?.JobId != null)
    {
        // Run specific job
        var job = await _dbContext.ScrapeJobs
            .Where(j => j.Id == scrapeRequest.JobId)
            .Select(j => new { j.Id, j.SearchTerm })
            .FirstOrDefaultAsync();

        if (job == null)
        {
            var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
            await notFoundResponse.WriteAsJsonAsync(new { error = $"Job {scrapeRequest.JobId} not found" });
            return notFoundResponse;
        }

        jobsToRun = new[] { (job.Id, job.SearchTerm) };
    }
    else
    {
        // Run all enabled jobs
        var enabledJobs = await _dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .Select(j => new { j.Id, j.SearchTerm })
            .ToListAsync();

        if (enabledJobs.Count == 0)
        {
            _logger.LogInformation("No enabled jobs found");
            var noJobsResponse = req.CreateResponse(HttpStatusCode.OK);
            await noJobsResponse.WriteAsJsonAsync(new { message = "No enabled jobs", results = Array.Empty<object>() });
            return noJobsResponse;
        }

        jobsToRun = enabledJobs.Select(j => (j.Id, j.SearchTerm));
    }

    // Create ScrapeRuns and enqueue jobs (fire-and-forget)
    var results = new List<object>();
    int? firstRunId = null;
    string? firstInstanceId = null;

    foreach (var (jobId, searchTerm) in jobsToRun)
    {
        // Create ScrapeRun with Queued status
        var scrapeRun = new ScrapeRun
        {
            JobId = jobId,
            Status = "Queued",
            CurrentPhase = "Queued",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        // Enqueue job message
        var message = new ScrapeJobMessage(scrapeRun.Id, jobId, searchTerm, "Manual");
        var messageJson = JsonSerializer.Serialize(message);
        await _jobQueueClient.SendMessageAsync(messageJson);

        _logger.LogInformation("Enqueued scrape job for {SearchTerm} (RunId: {RunId})", searchTerm, scrapeRun.Id);

        firstRunId ??= scrapeRun.Id;
        firstInstanceId ??= scrapeRun.InstanceId;

        results.Add(new { jobId, searchTerm, runId = scrapeRun.Id, status = "Queued" });
    }

    var response = req.CreateResponse(HttpStatusCode.OK);
    await response.WriteAsJsonAsync(new
    {
        instanceId = firstInstanceId ?? Guid.NewGuid().ToString(),
        runId = firstRunId ?? 0,
        results
    });
    return response;
}
```

**Step 2: Add using statement for the model**

At the top of the file, add:

```csharp
using AIOMarketMaker.Etl.Models;
```

**Step 3: Run test to verify it passes**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ManualScrape_should_enqueue_job_messages" -v n
```

Expected: PASS

**Step 4: Commit**

```bash
git add AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs
git commit -m "feat: refactor ManualScrape to enqueue jobs instead of running inline"
```

---

### Task 5: Write Failing Test for Queue Trigger

**Files:**
- Create: `AIOMarketMaker.Tests/Unit/Triggers/ScrapeJobQueueTrigger_UnitTests.cs`

**Step 1: Create test file**

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Triggers;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Tests.Unit.Triggers;

[TestFixture]
[Category("Unit")]
public class ScrapeJobQueueTrigger_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<ILogger<ScrapeJobQueueTrigger>> _loggerMock = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<EtlDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new EtlDbContext(options);
        _loggerMock = new Mock<ILogger<ScrapeJobQueueTrigger>>();
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task ProcessJob_should_update_status_to_Searching_when_started()
    {
        // Arrange
        var job = new ScrapeJob { Id = 1, SearchTerm = "Test", IsEnabled = true };
        _dbContext.ScrapeJobs.Add(job);

        var scrapeRun = new ScrapeRun
        {
            Id = 100,
            JobId = 1,
            Status = "Queued",
            CurrentPhase = "Queued",
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        var message = new ScrapeJobMessage(100, 1, "Test", "Manual");
        var messageJson = JsonSerializer.Serialize(message);

        // This will fail because ScrapeJobQueueTrigger doesn't exist yet
        var trigger = new ScrapeJobQueueTrigger(
            _loggerMock.Object,
            _dbContext,
            /* other dependencies */);

        // Act
        await trigger.ProcessJob(messageJson);

        // Assert
        var updatedRun = await _dbContext.ScrapeRuns.FindAsync(100);
        Assert.That(updatedRun!.Status, Is.EqualTo("Searching"));
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ProcessJob_should_update_status" -v n
```

Expected: FAIL (ScrapeJobQueueTrigger doesn't exist)

**Step 3: Commit failing test**

```bash
git add AIOMarketMaker.Tests/Unit/Triggers/ScrapeJobQueueTrigger_UnitTests.cs
git commit -m "test: add failing test for queue trigger"
```

---

### Task 6: Create Queue Trigger Function

**Files:**
- Create: `AIOMarketMaker.Etl/Triggers/ScrapeJobQueueTrigger.cs`

**Step 1: Create the queue trigger**

```csharp
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Azure.Storage.Queues;
using AngleSharp;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Models;
using ScraperWorker.Services;

namespace AIOMarketMaker.Etl.Triggers;

/// <summary>
/// Queue-triggered function that processes scrape jobs.
/// Each job runs independently - failures don't affect other jobs.
/// </summary>
public class ScrapeJobQueueTrigger
{
    private readonly ILogger<ScrapeJobQueueTrigger> _logger;
    private readonly EtlDbContext _dbContext;
    private readonly IWebscraperClient _webscraperClient;
    private readonly ISearchParser _searchParser;
    private readonly QueueClient _workQueueClient;
    private readonly IEbayUrlBuilder _urlBuilder;

    public ScrapeJobQueueTrigger(
        ILogger<ScrapeJobQueueTrigger> logger,
        EtlDbContext dbContext,
        IWebscraperClient webscraperClient,
        ISearchParser searchParser,
        QueueServiceClient queueService)
    {
        _logger = logger;
        _dbContext = dbContext;
        _webscraperClient = webscraperClient;
        _searchParser = searchParser;
        _workQueueClient = queueService.GetQueueClient("scrape-work");
        _urlBuilder = new EbayUrlBuilder();
    }

    [Function("ProcessScrapeJob")]
    public async Task ProcessJob(
        [QueueTrigger("scrape-jobs", Connection = "AzureWebJobsStorage")] string messageJson)
    {
        var message = JsonSerializer.Deserialize<ScrapeJobMessage>(messageJson);
        if (message == null)
        {
            _logger.LogError("Failed to deserialize queue message: {Message}", messageJson);
            return;
        }

        _logger.LogInformation("Processing scrape job: RunId={RunId}, JobId={JobId}, SearchTerm={SearchTerm}",
            message.ScrapeRunId, message.JobId, message.SearchTerm);

        // Get the ScrapeRun
        var scrapeRun = await _dbContext.ScrapeRuns.FindAsync(message.ScrapeRunId);
        if (scrapeRun == null)
        {
            _logger.LogError("ScrapeRun {RunId} not found", message.ScrapeRunId);
            return;
        }

        try
        {
            // Update status to Searching
            scrapeRun.Status = "Searching";
            scrapeRun.CurrentPhase = "Searching Sold";
            await _dbContext.SaveChangesAsync();

            // Run the actual scrape logic (reuse from SimplifiedScrapeTrigger)
            var listingsCount = await RunScrapeAsync(scrapeRun, message.SearchTerm);

            _logger.LogInformation("Scrape job completed: RunId={RunId}, ListingsEnqueued={Count}",
                message.ScrapeRunId, listingsCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scrape job failed: RunId={RunId}, Error={Error}",
                message.ScrapeRunId, ex.Message);

            scrapeRun.Status = "Failed";
            scrapeRun.ErrorMessage = ex.Message;
            scrapeRun.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            throw; // Re-throw to trigger queue retry/poison
        }
    }

    /// <summary>
    /// Runs the scrape for a job. This is the core logic extracted from SimplifiedScrapeTrigger.
    /// </summary>
    private async Task<int> RunScrapeAsync(ScrapeRun scrapeRun, string searchTerm)
    {
        const int maxPages = 100;

        // Phase 1: Search Sold Listings
        scrapeRun.CurrentPhase = "Searching Sold";
        await _dbContext.SaveChangesAsync();

        var soldListingIds = new HashSet<string>();
        var page = 1;

        while (page <= maxPages)
        {
            var soldUrl = _urlBuilder.BuildSearchUrl(searchTerm, sold: true, page: page, Condition.NULL, BuyingFormat.BUY_NOW);
            var html = await _webscraperClient.GetPageHtmlAsync(soldUrl);

            _logger.LogInformation("Fetched sold page {Page} ({Bytes} bytes)", page, html.Length);

            var browsingContext = BrowsingContext.New(Configuration.Default);
            var document = await browsingContext.OpenAsync(request => request.Content(html));

            var products = _searchParser.ParseSearchResults(document);
            var pageListingIds = products
                .Where(p => !string.IsNullOrEmpty(p.ListingId))
                .Select(p => p.ListingId!)
                .ToList();

            if (pageListingIds.Count == 0)
                break;

            foreach (var id in pageListingIds)
                soldListingIds.Add(id);

            page++;
        }

        _logger.LogInformation("Sold search complete: {PageCount} pages, {Count} unique sold listings", page - 1, soldListingIds.Count);

        // Phase 2: Search Active Listings
        scrapeRun.CurrentPhase = "Searching Active";
        await _dbContext.SaveChangesAsync();

        var allListingIds = new HashSet<string>();
        page = 1;

        while (page <= maxPages)
        {
            var activeUrl = _urlBuilder.BuildSearchUrl(searchTerm, sold: false, page: page, Condition.NULL, BuyingFormat.BUY_NOW);
            var html = await _webscraperClient.GetPageHtmlAsync(activeUrl);

            _logger.LogInformation("Fetched active page {Page} ({Bytes} bytes)", page, html.Length);

            var browsingContext = BrowsingContext.New(Configuration.Default);
            var document = await browsingContext.OpenAsync(request => request.Content(html));

            var products = _searchParser.ParseSearchResults(document);
            var pageListingIds = products
                .Where(p => !string.IsNullOrEmpty(p.ListingId))
                .Select(p => p.ListingId!)
                .ToList();

            if (pageListingIds.Count == 0)
                break;

            foreach (var id in pageListingIds)
                allListingIds.Add(id);

            page++;
        }

        // Combine sold + active
        foreach (var id in soldListingIds)
            allListingIds.Add(id);

        scrapeRun.TotalListingsFound = allListingIds.Count;
        _logger.LogInformation("Total unique listings found: {Count}", allListingIds.Count);

        // Phase 3: Filter existing terminal listings
        scrapeRun.CurrentPhase = "Filtering";
        await _dbContext.SaveChangesAsync();

        var terminalStatuses = new[] { "Sold", "Ended", "OutOfStock" };
        var existingTerminalIds = await _dbContext.Listings
            .Where(l => allListingIds.Contains(l.ListingId) && terminalStatuses.Contains(l.ListingStatus))
            .Select(l => l.ListingId)
            .ToListAsync();

        var listingsToProcess = allListingIds.Except(existingTerminalIds).ToList();
        _logger.LogInformation("After filtering: {Count} listings to process ({Skipped} terminal skipped)",
            listingsToProcess.Count, existingTerminalIds.Count);

        // Phase 4: Create ScrapeRunListings and enqueue work
        scrapeRun.CurrentPhase = "Indexing";
        scrapeRun.Status = "Indexing";
        await _dbContext.SaveChangesAsync();

        foreach (var listingId in listingsToProcess)
        {
            // Create junction record
            var srl = new ScrapeRunListing
            {
                ScrapeRunId = scrapeRun.Id,
                ListingId = listingId,
                Status = "Pending",
                CreatedUtc = DateTime.UtcNow
            };
            _dbContext.ScrapeRunListings.Add(srl);

            // Enqueue work message
            var workMessage = new
            {
                ScrapeRunId = scrapeRun.Id,
                ScrapeJobId = scrapeRun.JobId,
                ListingId = listingId,
                ListingUrl = $"https://www.ebay.co.uk/itm/{listingId}",
                IsSold = soldListingIds.Contains(listingId)
            };
            var messageJson = JsonSerializer.Serialize(workMessage);
            await _workQueueClient.SendMessageAsync(messageJson);
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Enqueued {Count} listings for processing", listingsToProcess.Count);

        return listingsToProcess.Count;
    }
}
```

**Step 2: Run test to verify it passes**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ProcessJob_should_update_status" -v n
```

Expected: PASS (after updating test to provide all dependencies)

**Step 3: Commit**

```bash
git add AIOMarketMaker.Etl/Triggers/ScrapeJobQueueTrigger.cs
git commit -m "feat: add queue trigger for processing scrape jobs"
```

---

### Task 7: Update Nightly Trigger to Use Queue

**Files:**
- Modify: `AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs` (RunNightly method)

**Step 1: Update the RunNightly method to enqueue instead of run inline**

Find the `RunNightly` method and replace the foreach loop with queue-based approach:

```csharp
[Function("NightlyScrape")]
public async Task RunNightly(
    [TimerTrigger("0 0 2 * * *")] TimerInfo timer)  // 2 AM daily
{
    _logger.LogInformation("Nightly scrape trigger fired at {Time}", DateTime.UtcNow);

    var enabledJobs = await _dbContext.ScrapeJobs
        .Where(j => j.IsEnabled)
        .Select(j => new { j.Id, j.SearchTerm })
        .ToListAsync();

    if (enabledJobs.Count == 0)
    {
        _logger.LogInformation("No enabled jobs for nightly scrape");
        return;
    }

    // Enqueue all jobs (fire-and-forget)
    foreach (var job in enabledJobs)
    {
        var scrapeRun = new ScrapeRun
        {
            JobId = job.Id,
            Status = "Queued",
            CurrentPhase = "Queued",
            TriggerType = "Nightly",
            StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        var message = new ScrapeJobMessage(scrapeRun.Id, job.Id, job.SearchTerm, "Nightly");
        var messageJson = JsonSerializer.Serialize(message);
        await _jobQueueClient.SendMessageAsync(messageJson);

        _logger.LogInformation("Enqueued nightly scrape for {SearchTerm} (RunId: {RunId})",
            job.SearchTerm, scrapeRun.Id);
    }

    _logger.LogInformation("Enqueued {Count} jobs for nightly scrape", enabledJobs.Count);
}
```

**Step 2: Remove the old RunScrapeForJobAsync method**

The `RunScrapeForJobAsync` method is no longer needed in SimplifiedScrapeTrigger since the logic moved to ScrapeJobQueueTrigger. Delete it (lines 50-250 approximately).

**Step 3: Commit**

```bash
git add AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs
git commit -m "feat: update nightly trigger to use queue, remove inline scrape logic"
```

---

### Task 8: Integration Test

**Files:**
- Modify: `AIOMarketMaker.Tests/Integration/SimplifiedPipeline_IntegrationTests.cs` (if exists)

**Step 1: Add integration test**

```csharp
[Test]
[Category("Integration")]
[Explicit("Requires Azurite and local infrastructure")]
public async Task Manual_scrape_should_enqueue_jobs_and_return_immediately()
{
    // Arrange - call the scrape/start endpoint
    var startTime = DateTime.UtcNow;

    var response = await _httpClient.PostAsync("/api/scrape/start", null);

    var elapsed = DateTime.UtcNow - startTime;

    // Assert - should return quickly (< 5 seconds)
    Assert.That(elapsed.TotalSeconds, Is.LessThan(5),
        "Fire-and-forget should return immediately");

    // Verify jobs were queued
    var content = await response.Content.ReadAsStringAsync();
    var result = JsonDocument.Parse(content);
    Assert.That(result.RootElement.GetProperty("results").GetArrayLength(), Is.GreaterThan(0));
}
```

**Step 2: Run integration test (optional)**

```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Integration" -v n
```

**Step 3: Commit**

```bash
git add AIOMarketMaker.Tests/Integration/
git commit -m "test: add integration test for fire-and-forget scrape"
```

---

### Task 9: Manual Testing

**Step 1: Start the infrastructure**

```bash
# Terminal 1: Azurite
npx azurite --blobPort 10000 --queuePort 10001 --tablePort 10002

# Terminal 2: ETL Functions
cd AIOMarketMaker/AIOMarketMaker.Etl
func start

# Terminal 3: API Functions
cd AIOMarketMaker/AIOMarketMaker.Functions
func start
```

**Step 2: Trigger a scrape and verify immediate return**

```bash
# Should return immediately with runIds
curl -X POST http://localhost:7072/api/scrape/start

# Check the queue has messages
az storage message peek --queue-name "scrape-jobs" --num-messages 5 \
  --connection-string "UseDevelopmentStorage=true"

# Monitor the ScrapeRuns status
sqlcmd -S "(localdb)\MSSQLLocalDB" -d AIOMarketMaker \
  -Q "SELECT Id, Status, CurrentPhase FROM ScrapeRuns ORDER BY Id DESC" -W
```

**Step 3: Verify queue processing**

Watch the ETL Functions logs - you should see:
- "Processing scrape job: RunId=X, JobId=Y, SearchTerm=Z"
- Status updates from Queued → Searching → Indexing → Completed

---

## Summary

This plan:
1. Creates a `ScrapeJobMessage` model for queue messages
2. Adds a `scrape-jobs` queue client
3. Refactors `ManualScrape` to create runs and enqueue (fire-and-forget)
4. Creates `ScrapeJobQueueTrigger` to process jobs from the queue
5. Updates `NightlyScrape` to use the same pattern
6. Adds tests for the new behavior

**Benefits:**
- HTTP response is instant (< 1 second)
- Jobs run independently in parallel
- One job failing doesn't block others
- Automatic retry via queue poison handling
- Better observability (Queued status visible in UI)
