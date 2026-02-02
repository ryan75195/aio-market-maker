# Direct Queue Writes for Scrape Jobs Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Eliminate activity timeout by bypassing HTTP round-trips and writing directly to the scrape-work queue.

**Architecture:** Replace `SubmitScrapeJobsActivity`'s sequential HTTP calls to WebScraper API with a single batch write to Azure Storage Queue. The queue message format stays identical - workers process messages the same way regardless of how they were enqueued.

**Tech Stack:** Azure.Storage.Queues, IQueueService from AIOWebScraper.Storage.Azure

---

## Background

The `SubmitScrapeJobsActivity` times out after 10 minutes when processing 1000+ listings because:
- Each listing requires 2 HTTP calls to `/api/NewJob` (listing + description)
- 2000 sequential HTTP calls × ~200ms each = ~400 seconds
- Activity timeout = 10 minutes

The HTTP call creates job tracking records (JobEntity, JobItemEntity) that are **never used** by this flow - progress is tracked via `ScrapeRunListings` table and blob triggers.

**Solution:** Write `ScrapeQueueMessage` directly to the queue, skipping the HTTP layer entirely.

---

## Task 1: Register IQueueService in ETL DI Container

**Files:**
- Modify: `AIOMarketMaker.Etl/Program.cs:56-63`

**Step 1: Add QueueServiceClient and IQueueService registration**

After the `TableServiceClient` registration (around line 58), add:

```csharp
// Azure Queue client for direct queue writes
var queueConnectionString = configuration.GetValue<string>("queueStorageConnectionString")
    ?? configuration.GetValue<string>("AzureWebJobsStorage")
    ?? "UseDevelopmentStorage=true";
services.AddSingleton(_ => new Azure.Storage.Queues.QueueServiceClient(queueConnectionString));
services.AddSingleton<IQueueService, AzureStorageQueueService>();
```

**Step 2: Add using statement**

Add at top of file:
```csharp
using Azure.Storage.Queues;
```

**Step 3: Verify build succeeds**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Program.cs
git commit -m "feat(etl): register IQueueService for direct queue writes"
```

---

## Task 2: Create Unit Test for Refactored Activity

**Files:**
- Create: `AIOMarketMaker.Tests/UnitTests/Activities/SubmitScrapeJobsActivityTests.cs`

**Step 1: Write the failing test**

```csharp
using Moq;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;
using ScraperWorker.Services;

namespace AIOMarketMaker.Tests.UnitTests.Activities;

[TestFixture]
[Category("Unit")]
public class SubmitScrapeJobsActivityTests
{
    private Mock<IQueueService> _mockQueueService = null!;
    private Mock<IEbayUrlBuilder> _mockUrlBuilder = null!;
    private Mock<ILogger<SubmitScrapeJobsActivity>> _mockLogger = null!;
    private SubmitScrapeJobsActivity _activity = null!;

    [SetUp]
    public void SetUp()
    {
        _mockQueueService = new Mock<IQueueService>();
        _mockUrlBuilder = new Mock<IEbayUrlBuilder>();
        _mockLogger = new Mock<ILogger<SubmitScrapeJobsActivity>>();

        _mockUrlBuilder.Setup(x => x.BuildListingUrl(It.IsAny<string>()))
            .Returns<string>(id => $"https://www.ebay.co.uk/itm/{id}");
        _mockUrlBuilder.Setup(x => x.BuildDescriptionUrl(It.IsAny<string>()))
            .Returns<string>(id => $"https://vi.vipr.ebaydesc.com/ws/eBayISAPI.dll?item={id}");

        _activity = new SubmitScrapeJobsActivity(
            _mockQueueService.Object,
            _mockUrlBuilder.Object,
            _mockLogger.Object);
    }

    [Test]
    public async Task Should_enqueue_two_messages_per_listing()
    {
        // Arrange
        var input = new SubmitScrapeJobsInput(
            ScrapeRunId: 123,
            ListingIds: new List<string> { "111", "222" });

        var enqueuedMessages = new List<ScrapeQueueMessage>();
        _mockQueueService
            .Setup(x => x.EnqueueBatchAsync(It.IsAny<IEnumerable<ScrapeQueueMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ScrapeQueueMessage>, CancellationToken>((msgs, _) => enqueuedMessages.AddRange(msgs))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(result.SubmittedCount, Is.EqualTo(2));
            Assert.That(result.FailedCount, Is.EqualTo(0));
            Assert.That(enqueuedMessages, Has.Count.EqualTo(4)); // 2 listings × 2 URLs each
        });
    }

    [Test]
    public async Task Should_set_correct_message_properties()
    {
        // Arrange
        var input = new SubmitScrapeJobsInput(
            ScrapeRunId: 456,
            ListingIds: new List<string> { "12345" });

        var enqueuedMessages = new List<ScrapeQueueMessage>();
        _mockQueueService
            .Setup(x => x.EnqueueBatchAsync(It.IsAny<IEnumerable<ScrapeQueueMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ScrapeQueueMessage>, CancellationToken>((msgs, _) => enqueuedMessages.AddRange(msgs))
            .Returns(Task.CompletedTask);

        // Act
        await _activity.Run(input, null!);

        // Assert
        var listingMsg = enqueuedMessages.First(m => m.FileKey == "listing");
        var descMsg = enqueuedMessages.First(m => m.FileKey == "description");

        Assert.Multiple(() =>
        {
            // Listing message
            Assert.That(listingMsg.Url, Does.Contain("/itm/12345"));
            Assert.That(listingMsg.GroupId, Is.EqualTo("12345"));
            Assert.That(listingMsg.FileKey, Is.EqualTo("listing"));
            Assert.That(listingMsg.ScrapeRunId, Is.EqualTo(456));
            Assert.That(listingMsg.JobId, Is.Not.Null.And.Not.Empty);

            // Description message
            Assert.That(descMsg.Url, Does.Contain("item=12345"));
            Assert.That(descMsg.GroupId, Is.EqualTo("12345"));
            Assert.That(descMsg.FileKey, Is.EqualTo("description"));
            Assert.That(descMsg.ScrapeRunId, Is.EqualTo(456));
        });
    }

    [Test]
    public async Task Should_call_enqueue_batch_once()
    {
        // Arrange
        var input = new SubmitScrapeJobsInput(
            ScrapeRunId: 789,
            ListingIds: new List<string> { "a", "b", "c" });

        // Act
        await _activity.Run(input, null!);

        // Assert - batch write should be called exactly once
        _mockQueueService.Verify(
            x => x.EnqueueBatchAsync(It.IsAny<IEnumerable<ScrapeQueueMessage>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~SubmitScrapeJobsActivityTests" -v n`

Expected: FAIL - constructor signature mismatch (activity still expects `IWebscraperClient`)

**Step 3: Commit failing test**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/UnitTests/Activities/SubmitScrapeJobsActivityTests.cs
git commit -m "test(etl): add unit tests for direct queue write activity (red)"
```

---

## Task 3: Refactor SubmitScrapeJobsActivity to Use Direct Queue Writes

**Files:**
- Modify: `AIOMarketMaker.Etl/Activities/SubmitScrapeJobsActivity.cs`

**Step 1: Replace implementation**

Replace entire file content with:

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Models;
using ScraperWorker.Services;

namespace AIOMarketMaker.Etl.Activities;

/// <summary>
/// Submits fire-and-forget scrape jobs by writing directly to the queue.
/// For each listing ID, two queue messages are created (listing + description).
/// This bypasses the WebScraper HTTP API for performance - no job tracking records
/// are created since this flow uses blob triggers and ScrapeRunListings for tracking.
/// </summary>
public class SubmitScrapeJobsActivity
{
    private readonly IQueueService _queueService;
    private readonly IEbayUrlBuilder _urlBuilder;
    private readonly ILogger<SubmitScrapeJobsActivity> _logger;

    public SubmitScrapeJobsActivity(
        IQueueService queueService,
        IEbayUrlBuilder urlBuilder,
        ILogger<SubmitScrapeJobsActivity> logger)
    {
        _queueService = queueService;
        _urlBuilder = urlBuilder;
        _logger = logger;
    }

    [Function(nameof(SubmitScrapeJobsActivity))]
    public async Task<SubmitScrapeJobsResult> Run(
        [ActivityTrigger] SubmitScrapeJobsInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Submitting scrape jobs for {Count} listings via direct queue write",
            input.ListingIds.Count);

        var messages = new List<ScrapeQueueMessage>();

        foreach (var listingId in input.ListingIds)
        {
            // Generate a unique job ID for this listing (used for blob path fallback)
            var jobId = Guid.NewGuid().ToString("N");

            // Listing page message
            messages.Add(new ScrapeQueueMessage
            {
                JobId = jobId,
                Url = _urlBuilder.BuildListingUrl(listingId),
                GroupId = listingId,
                FileKey = "listing",
                ScrapeRunId = input.ScrapeRunId,
                EnqueuedAt = DateTimeOffset.UtcNow
            });

            // Description page message
            messages.Add(new ScrapeQueueMessage
            {
                JobId = jobId,
                Url = _urlBuilder.BuildDescriptionUrl(listingId),
                GroupId = listingId,
                FileKey = "description",
                ScrapeRunId = input.ScrapeRunId,
                EnqueuedAt = DateTimeOffset.UtcNow
            });
        }

        try
        {
            await _queueService.EnqueueBatchAsync(messages, CancellationToken.None);

            _logger.LogInformation(
                "Successfully enqueued {MessageCount} messages for {ListingCount} listings",
                messages.Count, input.ListingIds.Count);

            return new SubmitScrapeJobsResult(input.ListingIds.Count, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue scrape jobs batch");
            return new SubmitScrapeJobsResult(0, input.ListingIds.Count);
        }
    }
}
```

**Step 2: Run tests to verify they pass**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~SubmitScrapeJobsActivityTests" -v n`

Expected: All 3 tests PASS

**Step 3: Verify build succeeds**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Etl/AIOMarketMaker.Etl.csproj`

Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Activities/SubmitScrapeJobsActivity.cs
git commit -m "feat(etl): refactor SubmitScrapeJobsActivity to use direct queue writes

Eliminates activity timeout by bypassing HTTP round-trips to WebScraper API.
Instead of 2000 sequential HTTP calls (~400s), we now batch write to Azure
Queue directly (~100ms).

The WebScraper job tracking (JobEntity, JobItemEntity) was unused by this
flow - progress is tracked via ScrapeRunListings table and blob triggers."
```

---

## Task 4: Integration Test - Verify Messages Are Enqueued Correctly

**Files:**
- Create: `AIOMarketMaker.Tests/IntegrationTests/Activities/SubmitScrapeJobsActivity_IntegrationTests.cs`

**Step 1: Write integration test using Azurite**

```csharp
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Azure.Storage.Queues;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Activities;
using AIOMarketMaker.Etl.Models;
using ScraperWorker.Services;
using System.Text;
using System.Text.Json;

namespace AIOMarketMaker.Tests.IntegrationTests.Activities;

[TestFixture]
[Category("Integration")]
[Explicit("Requires Azurite running on localhost")]
public class SubmitScrapeJobsActivity_IntegrationTests
{
    private const string AzuriteConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "QueueEndpoint=http://127.0.0.1:10001/devstoreaccount1;";

    private QueueServiceClient _queueServiceClient = null!;
    private QueueClient _workQueue = null!;
    private IQueueService _queueService = null!;
    private SubmitScrapeJobsActivity _activity = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _queueServiceClient = new QueueServiceClient(AzuriteConnectionString);
        _workQueue = _queueServiceClient.GetQueueClient("scrape-work");
        await _workQueue.CreateIfNotExistsAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        // Clear queue before each test
        await _workQueue.ClearMessagesAsync();

        var logger = new LoggerFactory().CreateLogger<AzureStorageQueueService>();
        _queueService = new AzureStorageQueueService(_queueServiceClient, logger);

        var urlBuilder = new EbayUrlBuilder();
        var activityLogger = new LoggerFactory().CreateLogger<SubmitScrapeJobsActivity>();

        _activity = new SubmitScrapeJobsActivity(_queueService, urlBuilder, activityLogger);
    }

    [Test]
    public async Task Should_write_messages_to_azure_queue()
    {
        // Arrange
        var input = new SubmitScrapeJobsInput(
            ScrapeRunId: 999,
            ListingIds: new List<string> { "123456789", "987654321" });

        // Act
        var result = await _activity.Run(input, null!);

        // Assert
        Assert.That(result.SubmittedCount, Is.EqualTo(2));

        // Verify messages are in queue
        var properties = await _workQueue.GetPropertiesAsync();
        Assert.That(properties.Value.ApproximateMessagesCount, Is.EqualTo(4));

        // Peek and verify message content
        var messages = await _workQueue.ReceiveMessagesAsync(maxMessages: 4);
        var decoded = messages.Value.Select(m =>
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(m.Body.ToString()));
            return JsonSerializer.Deserialize<ScrapeQueueMessage>(json);
        }).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Count(m => m!.FileKey == "listing"), Is.EqualTo(2));
            Assert.That(decoded.Count(m => m!.FileKey == "description"), Is.EqualTo(2));
            Assert.That(decoded.All(m => m!.ScrapeRunId == 999), Is.True);
        });
    }
}
```

**Step 2: Run integration test (requires Azurite)**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~SubmitScrapeJobsActivity_IntegrationTests" -v n`

Expected: PASS (if Azurite is running)

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/IntegrationTests/Activities/SubmitScrapeJobsActivity_IntegrationTests.cs
git commit -m "test(etl): add integration test for direct queue writes"
```

---

## Task 5: Manual End-to-End Test

**Steps:**

1. Start local environment: `/setup-local-env restart --workers 5`

2. Trigger a manual scrape via UI or API

3. Monitor with `/monitor-scrape`

4. Verify:
   - Activity completes in < 5 seconds (not 10 minute timeout)
   - Queue messages appear
   - Workers process messages
   - Blob triggers fire
   - Listings are added to database

**Expected:** Scrape jobs with 1000+ listings complete without timeout.

---

## Summary of Changes

| File | Change |
|------|--------|
| `AIOMarketMaker.Etl/Program.cs` | Register `QueueServiceClient` and `IQueueService` |
| `AIOMarketMaker.Etl/Activities/SubmitScrapeJobsActivity.cs` | Replace HTTP calls with `IQueueService.EnqueueBatchAsync` |
| `AIOMarketMaker.Tests/.../SubmitScrapeJobsActivityTests.cs` | New unit tests |
| `AIOMarketMaker.Tests/.../SubmitScrapeJobsActivity_IntegrationTests.cs` | New integration test |

## Performance Impact

| Metric | Before | After |
|--------|--------|-------|
| Time to submit 1000 listings | ~400s (timeout) | ~100-500ms |
| HTTP calls | 2000 | 0 |
| Table Storage writes | 4000 (unused) | 0 |
| Queue messages | 2000 | 2000 (same) |
