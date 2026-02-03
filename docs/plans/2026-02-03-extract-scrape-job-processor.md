# Extract ScrapeJobProcessor Service

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extract business logic from `ScrapeJobQueueTrigger` into a `ScrapeJobProcessor` service, keeping the trigger thin per codebase conventions. Route queue writes through `IWebscraperClient` so the ETL layer never touches WebScraper-internal types.

**Architecture:** The ~200-line `ScrapeJobQueueTrigger` currently does everything: multi-page search, HTML parsing, terminal status filtering, ScrapeRunListing creation, and direct `scrape-work` queue writes using `ScrapeQueueMessage` from `ScraperWorker.Services`. This bypasses the `IWebscraperClient` boundary. We extract the business logic into `ScrapeJobProcessor`, add an `EnqueueScrapeWork` method to `IWebscraperClient` to handle queue writes, and the trigger becomes a thin deserialize-and-delegate method. The processor never touches WebScraper types directly.

**Tech Stack:** .NET 8.0, NUnit, Moq, Azure Storage Queues, AngleSharp, EF Core

---

## Behavioral Parity Checklist

The new `ScrapeJobProcessor` MUST preserve every behavior currently in `ScrapeJobQueueTrigger.RunScrapeAsync`:

- [x] Multi-page sold search (up to 100 pages, stops when no results)
- [x] Multi-page active search (up to 100 pages, stops when no results)
- [x] Duplicate detection via `HashSet<string>` across pages
- [x] Active-to-Sold transition detection (listings marked Active in DB that appear in sold search)
- [x] Merge sold listing IDs into active set before filtering
- [x] Filter only terminal statuses (Sold, Ended, OutOfStock) - active listings are re-scraped
- [x] Update `ScrapeRun.TotalListingsFound` and `ListingsFilteredPreQueue`
- [x] Early completion when no new listings found (set Status=Completed)
- [x] Create `ScrapeRunListing` records with Status=Pending for each new listing
- [x] Enqueue two messages per listing (listing page + description page) to `scrape-work`
- [x] Base64 encode messages for `AzureStorageQueueService.DequeueAsync` compatibility
- [x] Update `ScrapeRun.Status` through phases: Queued -> Searching -> Indexing
- [x] Update `ScrapeRun.CurrentPhase` through: Queued -> Searching Sold -> Searching Active -> Detecting Transitions -> Indexing
- [x] Set failed status with error message on exceptions
- [x] Re-throw exceptions after marking run as failed

## What Changes

| Old Behavior | New Behavior | Impact | Intentional? |
|--------------|--------------|--------|--------------|
| Trigger constructs `ScrapeQueueMessage` directly | Processor calls `IWebscraperClient.EnqueueScrapeWork` | None - same messages reach the queue | YES - proper boundary |
| Trigger uses `QueueServiceClient` + manual base64 | `WebscraperClient` uses `IQueueService` internally | None - same encoding | YES - removes duplication |
| `new EbayUrlBuilder()` in trigger constructor | Injected `IEbayUrlBuilder` via DI | None - already registered in DI | YES - proper DI |
| ETL imports `ScraperWorker.Services` for queue types | ETL only uses `IWebscraperClient` | Cleaner boundary | YES - decoupling |

## What We Gain

- Trigger follows "thin trigger" convention
- Business logic is unit-testable without mocking queue triggers
- All WebScraper communication goes through `IWebscraperClient` - no boundary bypass
- If queue message format changes, only `WebscraperClient` needs updating
- `IEbayUrlBuilder` injected via DI instead of `new` in constructor

---

### Task 1: Add `EnqueueScrapeWork` to `IWebscraperClient`

**Files:**
- Modify: `AIOMarketMaker.Core/Services/WebscraperClient.cs`

Define a domain-level request record and add the method to the interface. The implementation constructs `ScrapeQueueMessage` objects internally and uses `IQueueService` to enqueue them.

**Step 1: Write the failing test**

Create `AIOMarketMaker.Tests/Unit/Services/WebscraperClient_EnqueueTests.cs`:

```csharp
using Moq;
using NUnit.Framework;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Services;
using ScraperWorker.Services;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class WebscraperClient_EnqueueTests
{
    private Mock<IQueueService> _queueServiceMock = null!;
    private Mock<IJobRepository> _jobRepositoryMock = null!;
    private Mock<ILogger<WebscraperClient>> _loggerMock = null!;

    [SetUp]
    public void Setup()
    {
        _queueServiceMock = new Mock<IQueueService>();
        _jobRepositoryMock = new Mock<IJobRepository>();
        _loggerMock = new Mock<ILogger<WebscraperClient>>();
    }

    private WebscraperClient CreateClient() => new(
        new HttpClient(),
        new ScraperApiConfig("http://localhost:7126", ""),
        _jobRepositoryMock.Object,
        _loggerMock.Object,
        _queueServiceMock.Object);

    [Test]
    public async Task Should_enqueue_listing_and_description_for_each_item()
    {
        var items = new[]
        {
            new ScrapeWorkItem("ABC123", "https://ebay.co.uk/itm/ABC123", "https://vi.vipr.ebaydesc.com/item=ABC123")
        };

        await CreateClient().EnqueueScrapeWork(items, scrapeRunId: 1, scrapeJobId: 10);

        _queueServiceMock.Verify(
            q => q.EnqueueBatchAsync(
                It.Is<IEnumerable<ScrapeQueueMessage>>(msgs =>
                    msgs.Count() == 2
                    && msgs.Any(m => m.FileKey == "listing" && m.GroupId == "ABC123" && m.ScrapeRunId == 1 && m.ScrapeJobId == 10)
                    && msgs.Any(m => m.FileKey == "description" && m.GroupId == "ABC123")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_share_job_guid_between_listing_and_description()
    {
        var items = new[]
        {
            new ScrapeWorkItem("ABC123", "https://ebay.co.uk/itm/ABC123", "https://vi.vipr.ebaydesc.com/item=ABC123")
        };

        IEnumerable<ScrapeQueueMessage>? capturedMessages = null;
        _queueServiceMock
            .Setup(q => q.EnqueueBatchAsync(It.IsAny<IEnumerable<ScrapeQueueMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ScrapeQueueMessage>, CancellationToken>((msgs, _) => capturedMessages = msgs.ToList());

        await CreateClient().EnqueueScrapeWork(items, scrapeRunId: 1, scrapeJobId: 10);

        Assert.That(capturedMessages, Is.Not.Null);
        var messageList = capturedMessages!.ToList();
        Assert.That(messageList[0].JobId, Is.EqualTo(messageList[1].JobId),
            "Listing and description for same item should share a JobId");
    }

    [Test]
    public async Task Should_enqueue_multiple_items_in_single_batch()
    {
        var items = new[]
        {
            new ScrapeWorkItem("ABC123", "https://ebay.co.uk/itm/ABC123", "https://desc/ABC123"),
            new ScrapeWorkItem("DEF456", "https://ebay.co.uk/itm/DEF456", "https://desc/DEF456")
        };

        await CreateClient().EnqueueScrapeWork(items, scrapeRunId: 1, scrapeJobId: 10);

        _queueServiceMock.Verify(
            q => q.EnqueueBatchAsync(
                It.Is<IEnumerable<ScrapeQueueMessage>>(msgs => msgs.Count() == 4),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~WebscraperClient_EnqueueTests" -v n
```

Expected: FAIL - `ScrapeWorkItem` doesn't exist, `EnqueueScrapeWork` doesn't exist, constructor doesn't accept `IQueueService`.

**Step 3: Implement the interface addition and method**

In `WebscraperClient.cs`, add the record above the interface and the new method to both the interface and class:

```csharp
// Add this record above IWebscraperClient
public record ScrapeWorkItem(string ListingId, string ListingUrl, string DescriptionUrl);
```

Add to `IWebscraperClient`:
```csharp
Task EnqueueScrapeWork(
    IEnumerable<ScrapeWorkItem> items,
    int scrapeRunId,
    int scrapeJobId,
    CancellationToken ct = default);
```

Add `IQueueService` as an optional constructor parameter to `WebscraperClient`:
```csharp
private readonly IQueueService? _queueService;

public WebscraperClient(
    HttpClient http,
    ScraperApiConfig config,
    IJobRepository jobRepository,
    ILogger<WebscraperClient> logger,
    IQueueService? queueService = null)
{
    _http = http ?? throw new ArgumentNullException(nameof(http));
    _jobRepository = jobRepository ?? throw new ArgumentNullException(nameof(jobRepository));
    _logger = logger;
    _apiKey = config?.ApiKey ?? "";
    _queueService = queueService;
}
```

Add the implementation:
```csharp
public async Task EnqueueScrapeWork(
    IEnumerable<ScrapeWorkItem> items,
    int scrapeRunId,
    int scrapeJobId,
    CancellationToken ct = default)
{
    if (_queueService == null)
        throw new InvalidOperationException("QueueService not configured. Cannot enqueue scrape work.");

    var messages = new List<ScrapeQueueMessage>();
    foreach (var item in items)
    {
        var jobGuid = Guid.NewGuid().ToString("N");

        messages.Add(new ScrapeQueueMessage
        {
            JobId = jobGuid,
            Url = item.ListingUrl,
            GroupId = item.ListingId,
            FileKey = "listing",
            ScrapeRunId = scrapeRunId,
            ScrapeJobId = scrapeJobId,
            EnqueuedAt = DateTimeOffset.UtcNow
        });

        messages.Add(new ScrapeQueueMessage
        {
            JobId = jobGuid,
            Url = item.DescriptionUrl,
            GroupId = item.ListingId,
            FileKey = "description",
            ScrapeRunId = scrapeRunId,
            ScrapeJobId = scrapeJobId,
            EnqueuedAt = DateTimeOffset.UtcNow
        });
    }

    await _queueService.EnqueueBatchAsync(messages, ct);

    _logger.LogInformation("Enqueued {Count} scrape work messages for {ItemCount} listings",
        messages.Count, messages.Count / 2);
}
```

**Step 4: Run tests to verify they pass**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~WebscraperClient_EnqueueTests" -v n
```

Expected: All 3 tests PASS.

**Step 5: Run all existing unit tests to check nothing broke**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit" -v n
```

Expected: All pass. The optional `queueService` parameter means existing constructor calls still work.

**Step 6: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Services/WebscraperClient.cs AIOMarketMaker/AIOMarketMaker.Tests/Unit/Services/WebscraperClient_EnqueueTests.cs
git commit -m "feat: add EnqueueScrapeWork to IWebscraperClient"
```

---

### Task 2: Register IQueueService in DI for WebscraperClient

**Files:**
- Modify: `AIOMarketMaker.Etl/Program.cs`

The `IQueueService` and `AzureStorageQueueService` are already registered (line 72). `WebscraperClient` is registered via `AddHttpClient` (line 110-114). Since `IQueueService` is a singleton and `WebscraperClient` is created by `AddHttpClient`, DI will automatically inject `IQueueService` into the new optional constructor parameter.

**Step 1: Verify the registrations already exist**

Check that `Program.cs` has:
- Line 72: `services.AddSingleton<IQueueService, AzureStorageQueueService>();`
- Lines 110-114: `services.AddHttpClient<IWebscraperClient, WebscraperClient>(...)`

No code changes needed if both exist - DI will resolve the `IQueueService` parameter automatically.

**Step 2: Build to verify**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.sln
```

Expected: Clean build.

**Step 3: Commit (only if changes were needed)**

---

### Task 3: Create IScrapeJobProcessor interface and empty class

**Files:**
- Create: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`
- Modify: `AIOMarketMaker.Etl/Program.cs`

**Step 1: Create the interface and empty implementation**

```csharp
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Services;

public interface IScrapeJobProcessor
{
    Task Process(ScrapeJobMessage message);
}

public class ScrapeJobProcessor : IScrapeJobProcessor
{
    public Task Process(ScrapeJobMessage message)
    {
        throw new NotImplementedException();
    }
}
```

**Step 2: Register in DI**

In `Program.cs`, after the `services.AddScoped<IScrapeRunService, ScrapeRunService>();` line (line 73), add:

```csharp
services.AddScoped<IScrapeJobProcessor, ScrapeJobProcessor>();
```

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs AIOMarketMaker/AIOMarketMaker.Etl/Program.cs
git commit -m "feat: add IScrapeJobProcessor interface and stub"
```

---

### Task 4: Write failing tests for ScrapeJobProcessor

**Files:**
- Create: `AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs`

Tests verify the key behaviors. The processor uses `IWebscraperClient` and `IEbayUrlBuilder` - no WebScraper-internal types.

**Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Tests.Utils;
using AngleSharp.Dom;

namespace AIOMarketMaker.Tests.Unit.Services;

[TestFixture]
[Category("Unit")]
public class ScrapeJobProcessor_UnitTests
{
    private EtlDbContext _dbContext = null!;
    private Mock<ILogger<ScrapeJobProcessor>> _loggerMock = null!;
    private Mock<IWebscraperClient> _webscraperClientMock = null!;
    private Mock<ISearchParser> _searchParserMock = null!;
    private Mock<IEbayUrlBuilder> _urlBuilderMock = null!;

    [SetUp]
    public void Setup()
    {
        _dbContext = InMemoryDbContextFactory.Create();
        _loggerMock = new Mock<ILogger<ScrapeJobProcessor>>();
        _webscraperClientMock = new Mock<IWebscraperClient>();
        _searchParserMock = new Mock<ISearchParser>();
        _urlBuilderMock = new Mock<IEbayUrlBuilder>();

        // Default: empty search results (stops pagination)
        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<html></html>");

        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(Enumerable.Empty<IEbayProductSummary>());

        _urlBuilderMock
            .Setup(u => u.BuildListingUrl(It.IsAny<string>()))
            .Returns<string>(id => $"https://ebay.co.uk/itm/{id}");
        _urlBuilderMock
            .Setup(u => u.BuildDescriptionUrl(It.IsAny<string>()))
            .Returns<string>(id => $"https://vi.vipr.ebaydesc.com/ws/eBayISAPI.dll?item={id}");
        _urlBuilderMock
            .Setup(u => u.BuildSearchUrl(It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<int>(), It.IsAny<Condition>(), It.IsAny<BuyingFormat>()))
            .Returns("https://ebay.co.uk/sch/test");
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    private ScrapeJobProcessor CreateProcessor() => new(
        _loggerMock.Object, _dbContext, _webscraperClientMock.Object,
        _searchParserMock.Object, _urlBuilderMock.Object);

    [Test]
    public async Task Should_complete_run_when_no_listings_found()
    {
        // Arrange
        var scrapeRun = new ScrapeRun
        {
            Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
            TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        // Act
        await CreateProcessor().Process(message);

        // Assert
        var run = await _dbContext.ScrapeRuns.FindAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(run!.Status, Is.EqualTo("Completed"));
            Assert.That(run.CurrentPhase, Is.EqualTo("Completed"));
            Assert.That(run.CompletedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Should_enqueue_via_webscraper_client_for_new_listings()
    {
        // Arrange
        var scrapeRun = new ScrapeRun
        {
            Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
            TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        // Return one listing on active search page 1, empty everywhere else
        var callCount = 0;
        var mockSummary = new Mock<IEbayProductSummary>();
        mockSummary.Setup(s => s.ListingId).Returns("ABC123");
        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                // Call 1: sold page 1 (empty) → stops sold search
                // Call 2: active page 1 (returns listing)
                // Call 3: active page 2 (empty) → stops active search
                return callCount == 2
                    ? new[] { mockSummary.Object }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        // Act
        await CreateProcessor().Process(message);

        // Assert - should call EnqueueScrapeWork with one item
        _webscraperClientMock.Verify(
            w => w.EnqueueScrapeWork(
                It.Is<IEnumerable<ScrapeWorkItem>>(items =>
                    items.Count() == 1
                    && items.First().ListingId == "ABC123"),
                1, 1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_skip_terminal_listings_but_rescrape_active()
    {
        // Arrange
        var scrapeRun = new ScrapeRun
        {
            Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
            TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);

        // Active listing should be re-scraped
        _dbContext.Listings.Add(new Listing
        {
            ListingId = "ACTIVE1", ScrapeJobId = 1,
            Title = "Active Item", ListingStatus = "Active"
        });
        // Sold listing should be skipped
        _dbContext.Listings.Add(new Listing
        {
            ListingId = "SOLD1", ScrapeJobId = 1,
            Title = "Sold Item", ListingStatus = "Sold"
        });
        await _dbContext.SaveChangesAsync();

        // Return both listings in active search
        var callCount = 0;
        var activeSummary = new Mock<IEbayProductSummary>();
        activeSummary.Setup(s => s.ListingId).Returns("ACTIVE1");
        var soldSummary = new Mock<IEbayProductSummary>();
        soldSummary.Setup(s => s.ListingId).Returns("SOLD1");

        _searchParserMock
            .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2
                    ? new[] { activeSummary.Object, soldSummary.Object }
                    : Enumerable.Empty<IEbayProductSummary>();
            });

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        // Act
        await CreateProcessor().Process(message);

        // Assert
        var run = await _dbContext.ScrapeRuns.FindAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(run!.TotalListingsFound, Is.EqualTo(2));
            Assert.That(run.ListingsFilteredPreQueue, Is.EqualTo(1),
                "Sold listing should be filtered as terminal");
        });

        // Should enqueue only the active listing
        _webscraperClientMock.Verify(
            w => w.EnqueueScrapeWork(
                It.Is<IEnumerable<ScrapeWorkItem>>(items =>
                    items.Count() == 1
                    && items.First().ListingId == "ACTIVE1"),
                1, 1, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Should_set_failed_status_and_rethrow_on_exception()
    {
        // Arrange
        var scrapeRun = new ScrapeRun
        {
            Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
            TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
            InstanceId = Guid.NewGuid().ToString()
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        _webscraperClientMock
            .Setup(w => w.GetPageHtmlAsync(
                It.IsAny<string>(), It.IsAny<IEnumerable<object>?>(),
                It.IsAny<string?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var message = new ScrapeJobMessage(1, 1, "Test", "Manual");

        // Act & Assert
        Assert.ThrowsAsync<HttpRequestException>(() => CreateProcessor().Process(message));

        var run = await _dbContext.ScrapeRuns.FindAsync(1);
        Assert.Multiple(() =>
        {
            Assert.That(run!.Status, Is.EqualTo("Failed"));
            Assert.That(run.ErrorMessage, Is.EqualTo("Connection refused"));
            Assert.That(run.CompletedUtc, Is.Not.Null);
        });
    }
}
```

**Step 2: Run tests to verify they fail**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeJobProcessor_UnitTests" -v n
```

Expected: FAIL - `ScrapeJobProcessor` constructor doesn't accept those parameters yet.

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "test: add failing tests for ScrapeJobProcessor"
```

---

### Task 5: Implement ScrapeJobProcessor

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`

Move business logic from the trigger. Key difference from the old code: instead of constructing `ScrapeQueueMessage` and using `QueueClient`, the processor builds `ScrapeWorkItem` objects and calls `_webscraperClient.EnqueueScrapeWork(...)`. No `using ScraperWorker.Services` needed.

**Step 1: Implement the processor**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AngleSharp;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Services;

public interface IScrapeJobProcessor
{
    Task Process(ScrapeJobMessage message);
}

public class ScrapeJobProcessor : IScrapeJobProcessor
{
    private readonly ILogger<ScrapeJobProcessor> _logger;
    private readonly EtlDbContext _dbContext;
    private readonly IWebscraperClient _webscraperClient;
    private readonly ISearchParser _searchParser;
    private readonly IEbayUrlBuilder _urlBuilder;

    public ScrapeJobProcessor(
        ILogger<ScrapeJobProcessor> logger,
        EtlDbContext dbContext,
        IWebscraperClient webscraperClient,
        ISearchParser searchParser,
        IEbayUrlBuilder urlBuilder)
    {
        _logger = logger;
        _dbContext = dbContext;
        _webscraperClient = webscraperClient;
        _searchParser = searchParser;
        _urlBuilder = urlBuilder;
    }

    public async Task Process(ScrapeJobMessage message)
    {
        _logger.LogInformation("Processing scrape job: RunId={RunId}, JobId={JobId}, SearchTerm={SearchTerm}",
            message.ScrapeRunId, message.JobId, message.SearchTerm);

        var scrapeRun = await _dbContext.ScrapeRuns.FindAsync(message.ScrapeRunId);
        if (scrapeRun == null)
        {
            _logger.LogError("ScrapeRun {RunId} not found", message.ScrapeRunId);
            return;
        }

        try
        {
            scrapeRun.Status = "Searching";
            scrapeRun.CurrentPhase = "Searching Sold";
            await _dbContext.SaveChangesAsync();

            await RunScrape(scrapeRun, message.JobId, message.SearchTerm);

            _logger.LogInformation("Scrape job completed: RunId={RunId}", message.ScrapeRunId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scrape job failed: RunId={RunId}", message.ScrapeRunId);
            scrapeRun.Status = "Failed";
            scrapeRun.ErrorMessage = ex.Message;
            scrapeRun.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            throw;
        }
    }

    private async Task RunScrape(ScrapeRun scrapeRun, int jobId, string searchTerm)
    {
        const int maxPages = 100;

        // Phase 1: Search Sold Listings
        var soldListingIds = await SearchListings(searchTerm, sold: true, maxPages);

        _logger.LogInformation("Sold search complete: {Count} unique sold listings", soldListingIds.Count);

        // Phase 2: Search Active Listings
        scrapeRun.CurrentPhase = "Searching Active";
        await _dbContext.SaveChangesAsync();

        var allListingIds = await SearchListings(searchTerm, sold: false, maxPages);

        _logger.LogInformation("Active search complete: {Count} unique active listings", allListingIds.Count);

        // Phase 3: Detect Active->Sold transitions
        scrapeRun.CurrentPhase = "Detecting Transitions";
        await _dbContext.SaveChangesAsync();

        var activeToSoldListings = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == jobId
                     && soldListingIds.Contains(l.ListingId)
                     && l.ListingStatus == "Active")
            .Select(l => l.ListingId)
            .ToListAsync();

        _logger.LogInformation("Found {Count} listings that transitioned from Active to Sold", activeToSoldListings.Count);

        // Include sold listings in the processing queue
        foreach (var id in soldListingIds)
            allListingIds.Add(id);

        // Filter out listings with terminal statuses
        var terminalStatuses = new HashSet<string> { "Sold", "Ended", "OutOfStock" };
        var terminalListingIdsList = await _dbContext.Listings
            .Where(l => l.ScrapeJobId == jobId
                     && allListingIds.Contains(l.ListingId)
                     && l.ListingStatus != null
                     && terminalStatuses.Contains(l.ListingStatus))
            .Select(l => l.ListingId)
            .ToListAsync();
        var existingListingIds = terminalListingIdsList.ToHashSet();

        var newListingIds = allListingIds
            .Where(id => !existingListingIds.Contains(id))
            .ToList();

        _logger.LogInformation("Filtered to {NewCount} listings to process ({TerminalCount} have terminal status)",
            newListingIds.Count, existingListingIds.Count);

        scrapeRun.TotalListingsFound = allListingIds.Count;
        scrapeRun.ListingsFilteredPreQueue = existingListingIds.Count;
        if (newListingIds.Count == 0)
        {
            scrapeRun.Status = "Completed";
            scrapeRun.CurrentPhase = "Completed";
            scrapeRun.CompletedUtc = DateTime.UtcNow;
            _logger.LogInformation("No new listings found for job {JobId} - marking as completed", jobId);
            await _dbContext.SaveChangesAsync();
            return;
        }
        scrapeRun.Status = "Indexing";
        scrapeRun.CurrentPhase = "Indexing";
        await _dbContext.SaveChangesAsync();

        // Create ScrapeRunListing records
        foreach (var listingId in newListingIds)
        {
            _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
            {
                ScrapeRunId = scrapeRun.Id,
                ScrapeJobId = jobId,
                ListingId = listingId,
                Status = "Pending",
                CreatedUtc = DateTime.UtcNow
            });
        }
        await _dbContext.SaveChangesAsync();

        // Build work items and enqueue via WebscraperClient
        var workItems = newListingIds.Select(id => new ScrapeWorkItem(
            id,
            _urlBuilder.BuildListingUrl(id),
            _urlBuilder.BuildDescriptionUrl(id)));

        await _webscraperClient.EnqueueScrapeWork(workItems, scrapeRun.Id, jobId);

        _logger.LogInformation("Enqueued {Count} listings for processing. Search phase complete for job {JobId}.",
            newListingIds.Count, jobId);
    }

    private async Task<HashSet<string>> SearchListings(string searchTerm, bool sold, int maxPages)
    {
        var listingIds = new HashSet<string>();
        var page = 1;

        while (page <= maxPages)
        {
            var url = _urlBuilder.BuildSearchUrl(searchTerm, sold: sold, page: page, Condition.NULL, BuyingFormat.BUY_NOW);
            var html = await _webscraperClient.GetPageHtmlAsync(url);

            _logger.LogInformation("Fetched {Type} page {Page} ({Bytes} bytes)",
                sold ? "sold" : "active", page, html.Length);

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
                listingIds.Add(id);

            page++;
        }

        return listingIds;
    }
}
```

**Step 2: Run tests to verify they pass**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeJobProcessor_UnitTests" -v n
```

Expected: All 4 tests PASS.

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs
git commit -m "feat: implement ScrapeJobProcessor with WebscraperClient boundary"
```

---

### Task 6: Slim down ScrapeJobQueueTrigger

**Files:**
- Modify: `AIOMarketMaker.Etl/Triggers/ScrapeJobQueueTrigger.cs`

Replace the entire trigger with a thin wrapper.

**Step 1: Rewrite the trigger**

```csharp
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;

namespace AIOMarketMaker.Etl.Triggers;

public class ScrapeJobQueueTrigger
{
    private readonly ILogger<ScrapeJobQueueTrigger> _logger;
    private readonly IScrapeJobProcessor _processor;

    public ScrapeJobQueueTrigger(
        ILogger<ScrapeJobQueueTrigger> logger,
        IScrapeJobProcessor processor)
    {
        _logger = logger;
        _processor = processor;
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

        await _processor.Process(message);
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Triggers/ScrapeJobQueueTrigger.cs
git commit -m "refactor: slim ScrapeJobQueueTrigger to delegate to IScrapeJobProcessor"
```

---

### Task 7: Update trigger tests

**Files:**
- Modify: `AIOMarketMaker.Tests/Unit/Triggers/ScrapeJobQueueTrigger_UnitTests.cs`

The trigger now only deserializes and delegates. Tests should verify that behavior.

**Step 1: Rewrite trigger tests**

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;
using AIOMarketMaker.Etl.Triggers;

namespace AIOMarketMaker.Tests.Unit.Triggers;

[TestFixture]
[Category("Unit")]
public class ScrapeJobQueueTrigger_UnitTests
{
    private Mock<ILogger<ScrapeJobQueueTrigger>> _loggerMock = null!;
    private Mock<IScrapeJobProcessor> _processorMock = null!;

    [SetUp]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<ScrapeJobQueueTrigger>>();
        _processorMock = new Mock<IScrapeJobProcessor>();
    }

    [Test]
    public async Task Should_delegate_to_processor_with_deserialized_message()
    {
        var message = new ScrapeJobMessage(100, 1, "Test", "Manual");
        var messageJson = JsonSerializer.Serialize(message);

        var trigger = new ScrapeJobQueueTrigger(_loggerMock.Object, _processorMock.Object);

        await trigger.ProcessJob(messageJson);

        _processorMock.Verify(
            p => p.Process(It.Is<ScrapeJobMessage>(m =>
                m.ScrapeRunId == 100 && m.JobId == 1
                && m.SearchTerm == "Test" && m.TriggerType == "Manual")),
            Times.Once);
    }

    [Test]
    public async Task Should_not_call_processor_when_message_is_invalid()
    {
        var trigger = new ScrapeJobQueueTrigger(_loggerMock.Object, _processorMock.Object);

        await trigger.ProcessJob("not valid json {{{");

        _processorMock.Verify(p => p.Process(It.IsAny<ScrapeJobMessage>()), Times.Never);
    }
}
```

**Step 2: Run all unit tests**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit" -v n
```

Expected: All unit tests PASS.

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/Unit/Triggers/ScrapeJobQueueTrigger_UnitTests.cs
git commit -m "test: update trigger tests for thin trigger pattern"
```

---

### Task 8: Full build and test verification

**Step 1: Build**

```bash
dotnet build AIOMarketMaker/AIOMarketMaker.sln
```

Expected: Clean build with no errors.

**Step 2: Run all unit tests**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Unit" -v n
```

Expected: All tests pass.

**Step 3: Verify no `ScraperWorker.Services` imports remain in trigger or processor**

The trigger (`ScrapeJobQueueTrigger.cs`) and processor (`ScrapeJobProcessor.cs`) should NOT have `using ScraperWorker.Services;`. Only `WebscraperClient.cs` should reference that namespace.

**Step 4: Commit if any fixes were needed**
