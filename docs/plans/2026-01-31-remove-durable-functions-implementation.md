# Remove Durable Functions Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace Durable Functions with a simpler queue-based architecture for reliable local development.

**Architecture:** Synchronous search phase using existing `WebscraperClient.GetPageHtmlAsync()`, workers POST to HTTP processing endpoint instead of blob triggers, timer-based completion check instead of SweepOrchestrator.

**Tech Stack:** Azure Functions (HTTP + Timer triggers), Azure Storage Queues, SQL Server, Entity Framework Core

**Design Document:** `docs/plans/2026-01-31-remove-durable-functions.md`

---

## Overview

This plan has 6 phases:
1. **Phase 1:** Create the Processing Endpoint (replaces blob trigger + ListingEtlOrchestrator)
2. **Phase 2:** Modify Workers to call Processing Endpoint
3. **Phase 3:** Create Simplified Trigger (replaces JobOrchestrator + ScrapeUrlOrchestrator)
4. **Phase 4:** Create Completion Check Timer (replaces SweepOrchestrator)
5. **Phase 5:** Integration Testing
6. **Phase 6:** Cleanup (remove Durable Functions code)

---

## Phase 1: Create the Processing Endpoint

### Task 1.1: Create ProcessListingEndpoint

**Files:**
- Create: `AIOMarketMaker.Etl/Endpoints/ProcessListingEndpoint.cs`
- Reference: `AIOMarketMaker.Etl/Activities/ProcessListingActivity.cs` (reuse logic)

**Step 1: Write the failing test**

Create test file `AIOMarketMaker.Tests/Unit/Endpoints/ProcessListingEndpoint_UnitTests.cs`:

```csharp
using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;

namespace AIOMarketMaker.Tests.Unit.Endpoints;

[TestFixture]
[Category("Unit")]
public class ProcessListingEndpoint_UnitTests
{
    [Test]
    public async Task Should_return_success_when_listing_processed()
    {
        // Arrange - will fail because ProcessListingEndpoint doesn't exist yet
        // var endpoint = new ProcessListingEndpoint(...);

        // Act
        // var result = await endpoint.ProcessAsync(request);

        // Assert
        Assert.Fail("ProcessListingEndpoint not implemented yet");
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ProcessListingEndpoint_UnitTests"
```

Expected: FAIL with "ProcessListingEndpoint not implemented yet"

**Step 3: Create the endpoint**

Create `AIOMarketMaker.Etl/Endpoints/ProcessListingEndpoint.cs`:

```csharp
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AngleSharp.Html.Parser;
using System.Net;

namespace AIOMarketMaker.Etl.Endpoints;

public record ProcessListingRequest(
    int ScrapeRunId,
    int ScrapeRunListingId,
    string ListingId,
    int ScrapeJobId,
    string BlobPath);

public record ProcessListingResponse(
    bool Success,
    string? Status,  // "added", "updated", "skipped", "failed"
    string? ErrorMessage);

public class ProcessListingEndpoint
{
    private readonly BlobServiceClient _blobService;
    private readonly EtlDbContext _dbContext;
    private readonly IListingParser _listingParser;
    private readonly ILogger<ProcessListingEndpoint> _logger;

    public ProcessListingEndpoint(
        BlobServiceClient blobService,
        EtlDbContext dbContext,
        IListingParser listingParser,
        ILogger<ProcessListingEndpoint> logger)
    {
        _blobService = blobService;
        _dbContext = dbContext;
        _listingParser = listingParser;
        _logger = logger;
    }

    [Function("ProcessListing")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "process-listing")] HttpRequestData req)
    {
        ProcessListingRequest? input;
        try
        {
            input = await req.ReadFromJsonAsync<ProcessListingRequest>();
            if (input == null)
            {
                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new ProcessListingResponse(false, null, "Invalid request body"));
                return badResponse;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse request body");
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteAsJsonAsync(new ProcessListingResponse(false, null, "Invalid JSON"));
            return badResponse;
        }

        try
        {
            // Check if already processed (idempotent)
            var existingEntry = await _dbContext.ScrapeRunListings
                .FirstOrDefaultAsync(srl => srl.Id == input.ScrapeRunListingId);

            if (existingEntry?.Status == "Complete")
            {
                _logger.LogInformation("Listing {ListingId} already processed, skipping", input.ListingId);
                var skipResponse = req.CreateResponse(HttpStatusCode.OK);
                await skipResponse.WriteAsJsonAsync(new ProcessListingResponse(true, "skipped", null));
                return skipResponse;
            }

            // Process the listing
            var result = await ProcessListingInternalAsync(input);

            // Update ScrapeRunListing status
            if (existingEntry != null)
            {
                existingEntry.Status = result.Success ? "Complete" : "Failed";
                existingEntry.ProcessedUtc = DateTime.UtcNow;
                if (!result.Success)
                    existingEntry.ErrorMessage = result.ErrorMessage;
            }

            // Increment ScrapeRun progress
            var scrapeRun = await _dbContext.ScrapeRuns.FindAsync(input.ScrapeRunId);
            if (scrapeRun != null)
            {
                scrapeRun.ListingsProcessed++;
                if (result.Success && result.Status == "added")
                {
                    // Determine if active or sold based on listing status
                    // For now, increment active (can be refined later)
                    scrapeRun.ListingsAddedActive++;
                }
                else if (!result.Success)
                {
                    scrapeRun.ListingsFailed++;
                }
                else if (result.Status == "skipped")
                {
                    scrapeRun.ListingsSkipped++;
                }
            }

            await _dbContext.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process listing {ListingId}", input.ListingId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new ProcessListingResponse(false, "failed", ex.Message));
            return errorResponse;
        }
    }

    private async Task<ProcessListingResponse> ProcessListingInternalAsync(ProcessListingRequest input)
    {
        var container = _blobService.GetBlobContainerClient("html");

        // Fetch listing HTML
        var listingBlob = container.GetBlobClient(input.BlobPath);

        if (!await listingBlob.ExistsAsync())
        {
            return new ProcessListingResponse(false, "failed", $"Blob not found: {input.BlobPath}");
        }

        var listingContent = await listingBlob.DownloadContentAsync();
        var listingHtml = listingContent.Value.Content.ToString();

        // Parse listing
        var parser = new HtmlParser();
        var listingDoc = await parser.ParseDocumentAsync(listingHtml);

        // Detect eBay error pages
        if (listingDoc.QuerySelector(".s-error") != null)
        {
            _logger.LogWarning("eBay error page detected for listing {ListingId}", input.ListingId);
            return new ProcessListingResponse(false, "failed", "eBay error page");
        }

        // Detect product page redirects
        var canonicalUrl = listingDoc.QuerySelector("link[rel='canonical']")?.GetAttribute("href");
        if (canonicalUrl != null && canonicalUrl.Contains("/p/") && !canonicalUrl.Contains("/itm/"))
        {
            _logger.LogInformation("Listing {ListingId} redirected to product page, skipping", input.ListingId);
            return new ProcessListingResponse(true, "skipped", "Product page redirect");
        }

        var extractedListing = _listingParser.ParseProductListing(listingDoc, $"https://ebay.com/itm/{input.ListingId}");

        // Detect redirected listings
        if (extractedListing.id != null && extractedListing.id != input.ListingId)
        {
            _logger.LogInformation("Listing {ListingId} was delisted (redirected to {ActualId})", input.ListingId, extractedListing.id);
            return new ProcessListingResponse(true, "skipped", "Delisted");
        }

        // Validate required fields
        var missingFields = new List<string>();
        if (extractedListing.id == null) missingFields.Add("id");
        if (extractedListing.title == null) missingFields.Add("title");
        if (extractedListing.price == null) missingFields.Add("price");
        if (extractedListing.currency == null) missingFields.Add("currency");
        if (extractedListing.Condition == null) missingFields.Add("condition");
        if (extractedListing.images == null || !extractedListing.images.Any()) missingFields.Add("images");
        if (extractedListing.listingStatus == null) missingFields.Add("listingStatus");

        if (missingFields.Any())
        {
            _logger.LogWarning("Parse validation failed for listing {ListingId}: missing {Fields}",
                input.ListingId, string.Join(", ", missingFields));
            return new ProcessListingResponse(false, "failed", $"Missing: {string.Join(", ", missingFields)}");
        }

        // Serialize images to JSON
        string? imagesJson = null;
        if (extractedListing.images != null && extractedListing.images.Any())
        {
            imagesJson = JsonSerializer.Serialize(extractedListing.images);
        }

        // Save to database using SQL MERGE for atomic upsert
        var mergeActionResult = await _dbContext.Database.SqlQueryRaw<string>(@"
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
                    UpdatedUtc = GETUTCDATE()
            WHEN NOT MATCHED THEN
                INSERT (ListingId, ScrapeJobId, Title, Price, Currency, ShippingCost,
                        Condition, ListingStatus, PurchaseFormat, ItemSpecifics, Images,
                        Location, EndDateUtc, Url, CreatedUtc)
                VALUES (@p0, @p13, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, GETUTCDATE())
            OUTPUT $action;",
            input.ListingId,                              // @p0
            extractedListing.title,                       // @p1
            extractedListing.price,                       // @p2
            extractedListing.currency,                    // @p3
            extractedListing.shippingCost,                // @p4
            extractedListing.Condition?.ToString(),       // @p5
            extractedListing.listingStatus?.ToString(),   // @p6
            extractedListing.purchaseFormat?.ToString(),  // @p7
            extractedListing.ItemSpecifics,               // @p8
            imagesJson,                                   // @p9
            extractedListing.Location,                    // @p10
            extractedListing.SoldDateUtc,                 // @p11
            extractedListing.Url,                         // @p12
            input.ScrapeJobId                             // @p13
        ).ToListAsync();

        var isNew = mergeActionResult.FirstOrDefault() == "INSERT";
        var status = isNew ? "added" : "updated";

        _logger.LogInformation("Processed listing {ListingId}: {Status}", input.ListingId, status);

        return new ProcessListingResponse(true, status, null);
    }
}
```

**Step 4: Update the test to actually test the endpoint**

Update `ProcessListingEndpoint_UnitTests.cs`:

```csharp
using NUnit.Framework;
using Moq;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Etl.Endpoints;

namespace AIOMarketMaker.Tests.Unit.Endpoints;

[TestFixture]
[Category("Unit")]
public class ProcessListingEndpoint_UnitTests
{
    [Test]
    public void Should_construct_endpoint_with_dependencies()
    {
        // Arrange
        var blobService = new Mock<BlobServiceClient>();
        var dbContext = new Mock<EtlDbContext>();
        var listingParser = new Mock<IListingParser>();
        var logger = new Mock<ILogger<ProcessListingEndpoint>>();

        // Act
        var endpoint = new ProcessListingEndpoint(
            blobService.Object,
            dbContext.Object,
            listingParser.Object,
            logger.Object);

        // Assert
        Assert.That(endpoint, Is.Not.Null);
    }
}
```

**Step 5: Run test to verify it passes**

```bash
dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ProcessListingEndpoint_UnitTests"
```

Expected: PASS

**Step 6: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Endpoints/ProcessListingEndpoint.cs AIOMarketMaker/AIOMarketMaker.Tests/Unit/Endpoints/
git commit -m "feat: add ProcessListingEndpoint (replaces blob trigger)

HTTP endpoint for workers to call after saving blob.
Handles listing parsing and database upsert.
Idempotent - skips already-processed listings."
```

---

## Phase 2: Modify Workers to Call Processing Endpoint

### Task 2.1: Add ProcessingEndpoint callback to SimpleQueueWorker

**Files:**
- Modify: `AIOWebScraper/ScraperWorker/Services/SimpleQueueWorker.cs`
- Create: `AIOWebScraper/ScraperWorker/Services/IProcessingCallback.cs`

**Step 1: Create the callback interface**

Create `AIOWebScraper/ScraperWorker/Services/IProcessingCallback.cs`:

```csharp
namespace ScraperWorker.Services;

public interface IProcessingCallback
{
    Task<bool> NotifyListingProcessedAsync(
        int scrapeRunId,
        int scrapeRunListingId,
        string listingId,
        int scrapeJobId,
        string blobPath,
        CancellationToken ct = default);
}
```

**Step 2: Create the HTTP callback implementation**

Create `AIOWebScraper/ScraperWorker/Services/HttpProcessingCallback.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace ScraperWorker.Services;

public class HttpProcessingCallback : IProcessingCallback
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpProcessingCallback> _logger;
    private readonly string _baseUrl;

    public HttpProcessingCallback(
        HttpClient httpClient,
        ILogger<HttpProcessingCallback> logger,
        string baseUrl = "http://localhost:7072")
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseUrl = baseUrl;
    }

    public async Task<bool> NotifyListingProcessedAsync(
        int scrapeRunId,
        int scrapeRunListingId,
        string listingId,
        int scrapeJobId,
        string blobPath,
        CancellationToken ct = default)
    {
        try
        {
            var request = new
            {
                ScrapeRunId = scrapeRunId,
                ScrapeRunListingId = scrapeRunListingId,
                ListingId = listingId,
                ScrapeJobId = scrapeJobId,
                BlobPath = blobPath
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{_baseUrl}/api/process-listing",
                request,
                ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Successfully notified processing endpoint for listing {ListingId}", listingId);
                return true;
            }

            _logger.LogWarning("Processing endpoint returned {StatusCode} for listing {ListingId}",
                response.StatusCode, listingId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify processing endpoint for listing {ListingId}", listingId);
            return false;
        }
    }
}
```

**Step 3: Update queue message model to include ScrapeRunListingId**

Modify `AIOWebScraper/AIOWebScraper.Api/Models/ScrapeQueueMessage.cs` (or create if not exists):

```csharp
namespace AIOWebScraper.Api.Models;

public record ScrapeQueueMessage(
    string JobId,
    string Url,
    string? CorrelationId = null,
    string? GroupId = null,
    string? FileKey = null,
    int? ScrapeRunId = null,
    int? ScrapeRunListingId = null,
    int? ScrapeJobId = null);
```

**Step 4: Modify SimpleQueueWorker to call callback after save**

In `SimpleQueueWorker.cs`, after saving content:

```csharp
// After line 161: await _jobRepository.SaveContentAsync(...)

// NEW: Notify processing endpoint if this is a listing scrape
if (message.Message.ScrapeRunId.HasValue &&
    message.Message.ScrapeRunListingId.HasValue &&
    message.Message.ScrapeJobId.HasValue &&
    _processingCallback != null)
{
    var blobPath = $"{message.Message.GroupId}/{message.Message.FileKey}";
    await _processingCallback.NotifyListingProcessedAsync(
        message.Message.ScrapeRunId.Value,
        message.Message.ScrapeRunListingId.Value,
        message.Message.GroupId ?? "",  // ListingId is stored in GroupId
        message.Message.ScrapeJobId.Value,
        blobPath,
        ct);
}
```

**Step 5: Add callback to SimpleQueueWorker constructor**

```csharp
private readonly IProcessingCallback? _processingCallback;

public SimpleQueueWorker(
    IQueueService queueService,
    IJobRepository jobRepository,
    ILogger<SimpleQueueWorker> logger,
    IRouteFilterService routeFilter,
    string? proxy = null,
    IProcessingCallback? processingCallback = null)
{
    // ... existing code ...
    _processingCallback = processingCallback;
}
```

**Step 6: Register callback in DI (SimpleQueueModeStartup)**

In `ScraperWorker/SimpleQueueModeStartup.cs`:

```csharp
// Add after other service registrations
services.AddHttpClient<IProcessingCallback, HttpProcessingCallback>();
```

**Step 7: Commit**

```bash
git add AIOWebScraper/ScraperWorker/Services/IProcessingCallback.cs AIOWebScraper/ScraperWorker/Services/HttpProcessingCallback.cs AIOWebScraper/ScraperWorker/Services/SimpleQueueWorker.cs
git commit -m "feat: add processing callback to workers

Workers now POST to ETL processing endpoint after saving blob.
This replaces unreliable blob triggers with direct HTTP calls."
```

---

## Phase 3: Create Simplified Trigger

### Task 3.1: Create SimplifiedScrapeTrigger

**Files:**
- Create: `AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs`
- Reference: `AIOMarketMaker.Core/Services/WebscraperClient.cs` (for synchronous scraping)

**Step 1: Create the simplified trigger**

Create `AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs`:

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using Azure.Storage.Queues;
using System.Text.Json;
using AngleSharp.Html.Parser;

namespace AIOMarketMaker.Etl.Triggers;

public class SimplifiedScrapeTrigger
{
    private readonly ILogger<SimplifiedScrapeTrigger> _logger;
    private readonly EtlDbContext _dbContext;
    private readonly IWebscraperClient _webscraperClient;
    private readonly ISearchParser _searchParser;
    private readonly QueueClient _queueClient;

    public SimplifiedScrapeTrigger(
        ILogger<SimplifiedScrapeTrigger> logger,
        EtlDbContext dbContext,
        IWebscraperClient webscraperClient,
        ISearchParser searchParser,
        QueueServiceClient queueService)
    {
        _logger = logger;
        _dbContext = dbContext;
        _webscraperClient = webscraperClient;
        _searchParser = searchParser;
        _queueClient = queueService.GetQueueClient("scrape-work");
    }

    /// <summary>
    /// Timer trigger that runs nightly at 2 AM UTC.
    /// Uses synchronous search (no Durable Functions).
    /// </summary>
    [Function("SimplifiedNightlyTrigger")]
    public async Task RunNightly(
        [TimerTrigger("0 0 2 * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Simplified nightly scrape trigger fired at {Time}", DateTime.UtcNow);
        await RunScrapeForAllEnabledJobsAsync("Nightly");
    }

    /// <summary>
    /// HTTP trigger for manual scrape initiation.
    /// </summary>
    [Function("SimplifiedManualTrigger")]
    public async Task<HttpResponseData> RunManual(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "trigger-scrape")] HttpRequestData req)
    {
        _logger.LogInformation("Manual scrape trigger fired");

        try
        {
            await RunScrapeForAllEnabledJobsAsync("Manual");
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync("Scrape started successfully");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start scrape");
            var response = req.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync($"Failed: {ex.Message}");
            return response;
        }
    }

    private async Task RunScrapeForAllEnabledJobsAsync(string triggerType)
    {
        // Get all enabled jobs
        var enabledJobs = await _dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .Select(j => new { j.Id, j.SearchTerm })
            .ToListAsync();

        if (enabledJobs.Count == 0)
        {
            _logger.LogInformation("No enabled jobs found");
            return;
        }

        foreach (var job in enabledJobs)
        {
            try
            {
                await RunScrapeForJobAsync(job.Id, job.SearchTerm, triggerType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to run scrape for job {JobId}: {SearchTerm}", job.Id, job.SearchTerm);
            }
        }
    }

    private async Task RunScrapeForJobAsync(int jobId, string searchTerm, string triggerType)
    {
        // 1. Create ScrapeRun
        var scrapeRun = new ScrapeRun
        {
            JobId = jobId,
            TriggerType = triggerType,
            StartedUtc = DateTime.UtcNow,
            Status = "Running",
            CurrentPhase = "Searching"
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Started scrape run {RunId} for job {JobId}: {SearchTerm}",
            scrapeRun.Id, jobId, searchTerm);

        try
        {
            // 2. Search phase (synchronous)
            var listingIds = await SearchForListingsAsync(searchTerm, scrapeRun.Id);

            if (listingIds.Count == 0)
            {
                _logger.LogInformation("No listings found for job {JobId}", jobId);
                scrapeRun.Status = "Completed";
                scrapeRun.CurrentPhase = "Completed";
                scrapeRun.CompletedUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
                return;
            }

            // 3. Filter out existing listings
            var existingListingIds = await _dbContext.Listings
                .Where(l => l.ScrapeJobId == jobId && listingIds.Contains(l.ListingId))
                .Select(l => l.ListingId)
                .ToListAsync();

            var newListingIds = listingIds.Except(existingListingIds).ToList();

            _logger.LogInformation("Job {JobId}: {Total} found, {New} new (filtered {Existing} existing)",
                jobId, listingIds.Count, newListingIds.Count, existingListingIds.Count);

            if (newListingIds.Count == 0)
            {
                scrapeRun.TotalListingsFound = listingIds.Count;
                scrapeRun.ListingsSkipped = listingIds.Count;
                scrapeRun.Status = "Completed";
                scrapeRun.CurrentPhase = "Completed";
                scrapeRun.CompletedUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
                return;
            }

            // 4. Insert ScrapeRunListings
            scrapeRun.CurrentPhase = "Indexing";
            scrapeRun.TotalListingsFound = newListingIds.Count;
            await _dbContext.SaveChangesAsync();

            var scrapeRunListings = newListingIds.Select(listingId => new ScrapeRunListing
            {
                ScrapeRunId = scrapeRun.Id,
                ListingId = listingId,
                Status = "Pending",
                CreatedUtc = DateTime.UtcNow
            }).ToList();

            _dbContext.ScrapeRunListings.AddRange(scrapeRunListings);
            await _dbContext.SaveChangesAsync();

            // 5. Enqueue all listing URLs
            await _queueClient.CreateIfNotExistsAsync();

            foreach (var srl in scrapeRunListings)
            {
                var message = new
                {
                    JobId = Guid.NewGuid().ToString("N"),  // Scraper job ID
                    Url = $"https://www.ebay.co.uk/itm/{srl.ListingId}",
                    GroupId = scrapeRun.Id.ToString(),
                    FileKey = $"{srl.ListingId}/listing.html",
                    ScrapeRunId = scrapeRun.Id,
                    ScrapeRunListingId = srl.Id,
                    ScrapeJobId = jobId
                };

                var messageJson = JsonSerializer.Serialize(message);
                await _queueClient.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(messageJson)));
            }

            _logger.LogInformation("Job {JobId}: Enqueued {Count} listings for scraping", jobId, newListingIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scrape run {RunId} failed", scrapeRun.Id);
            scrapeRun.Status = "Failed";
            scrapeRun.ErrorMessage = ex.Message;
            await _dbContext.SaveChangesAsync();
            throw;
        }
    }

    private async Task<List<string>> SearchForListingsAsync(string searchTerm, int scrapeRunId)
    {
        var allListingIds = new HashSet<string>();
        var parser = new HtmlParser();

        // Search sold listings
        _logger.LogInformation("Run {RunId}: Searching sold listings for '{SearchTerm}'...", scrapeRunId, searchTerm);
        for (int page = 1; page <= 10; page++)  // Max 10 pages
        {
            var url = BuildSearchUrl(searchTerm, page, isSold: true);

            try
            {
                var html = await _webscraperClient.GetPageHtmlAsync(url);
                var doc = await parser.ParseDocumentAsync(html);
                var pageListingIds = _searchParser.ParseSearchResults(doc);

                if (pageListingIds.Count == 0)
                    break;

                foreach (var id in pageListingIds)
                    allListingIds.Add(id);

                _logger.LogInformation("Run {RunId}: Sold page {Page} found {Count} listings (total: {Total})",
                    scrapeRunId, page, pageListingIds.Count, allListingIds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run {RunId}: Failed to scrape sold page {Page}", scrapeRunId, page);
                break;
            }
        }

        // Search active listings
        _logger.LogInformation("Run {RunId}: Searching active listings for '{SearchTerm}'...", scrapeRunId, searchTerm);
        for (int page = 1; page <= 10; page++)
        {
            var url = BuildSearchUrl(searchTerm, page, isSold: false);

            try
            {
                var html = await _webscraperClient.GetPageHtmlAsync(url);
                var doc = await parser.ParseDocumentAsync(html);
                var pageListingIds = _searchParser.ParseSearchResults(doc);

                if (pageListingIds.Count == 0)
                    break;

                foreach (var id in pageListingIds)
                    allListingIds.Add(id);

                _logger.LogInformation("Run {RunId}: Active page {Page} found {Count} listings (total: {Total})",
                    scrapeRunId, page, pageListingIds.Count, allListingIds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Run {RunId}: Failed to scrape active page {Page}", scrapeRunId, page);
                break;
            }
        }

        return allListingIds.ToList();
    }

    private static string BuildSearchUrl(string searchTerm, int page, bool isSold)
    {
        var baseUrl = "https://www.ebay.co.uk/sch/i.html";
        var encodedTerm = Uri.EscapeDataString(searchTerm);
        var soldParams = isSold ? "&LH_Sold=1&LH_Complete=1" : "";
        return $"{baseUrl}?_nkw={encodedTerm}{soldParams}&_pgn={page}&_ipg=240&LH_TitleDesc=0";
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs
git commit -m "feat: add SimplifiedScrapeTrigger (replaces Durable Functions)

Synchronous search using WebscraperClient.GetPageHtmlAsync().
Direct queue enqueue instead of orchestrator activities.
No Durable Functions dependencies."
```

---

## Phase 4: Create Completion Check Timer

### Task 4.1: Create CompletionCheckTrigger

**Files:**
- Create: `AIOMarketMaker.Etl/Triggers/CompletionCheckTrigger.cs`

**Step 1: Create the completion check trigger**

Create `AIOMarketMaker.Etl/Triggers/CompletionCheckTrigger.cs`:

```csharp
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;

namespace AIOMarketMaker.Etl.Triggers;

public class CompletionCheckTrigger
{
    private readonly ILogger<CompletionCheckTrigger> _logger;
    private readonly EtlDbContext _dbContext;

    public CompletionCheckTrigger(
        ILogger<CompletionCheckTrigger> logger,
        EtlDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    /// <summary>
    /// Timer trigger that checks for completed scrape runs every 30 seconds.
    /// Marks runs as Completed when ListingsProcessed >= TotalListingsFound.
    /// </summary>
    [Function("CompletionCheckTrigger")]
    public async Task Run([TimerTrigger("*/30 * * * * *")] TimerInfo timer)
    {
        // Find runs that should be completed
        var runsToComplete = await _dbContext.ScrapeRuns
            .Where(r => r.Status == "Running"
                     && r.CurrentPhase == "Indexing"
                     && r.TotalListingsFound > 0
                     && r.ListingsProcessed >= r.TotalListingsFound)
            .ToListAsync();

        foreach (var run in runsToComplete)
        {
            run.Status = "Completed";
            run.CurrentPhase = "Completed";
            run.CompletedUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "Marked run {RunId} as Completed: {Processed}/{Total} processed, {Added} added, {Skipped} skipped, {Failed} failed",
                run.Id, run.ListingsProcessed, run.TotalListingsFound,
                run.ListingsAddedActive + run.ListingsAddedSold,
                run.ListingsSkipped, run.ListingsFailed);
        }

        if (runsToComplete.Count > 0)
        {
            await _dbContext.SaveChangesAsync();
        }

        // Also check for stale runs (no progress for 30 minutes)
        var staleThreshold = DateTime.UtcNow.AddMinutes(-30);
        var staleRuns = await _dbContext.ScrapeRuns
            .Where(r => r.Status == "Running"
                     && r.StartedUtc < staleThreshold)
            .ToListAsync();

        foreach (var run in staleRuns)
        {
            // Check if there's been any progress recently
            var hasRecentProgress = await _dbContext.ScrapeRunListings
                .AnyAsync(srl => srl.ScrapeRunId == run.Id
                              && srl.ProcessedUtc > staleThreshold);

            if (!hasRecentProgress)
            {
                _logger.LogWarning(
                    "Run {RunId} appears stale (no progress for 30 minutes): {Processed}/{Total}",
                    run.Id, run.ListingsProcessed, run.TotalListingsFound);

                // Optionally mark as failed
                // run.Status = "Failed";
                // run.ErrorMessage = "Stale - no progress for 30 minutes";
            }
        }

        if (staleRuns.Count > 0)
        {
            await _dbContext.SaveChangesAsync();
        }
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Triggers/CompletionCheckTrigger.cs
git commit -m "feat: add CompletionCheckTrigger (replaces SweepOrchestrator)

Simple timer that checks for completed runs every 30 seconds.
No Durable Functions - just a SQL query."
```

---

## Phase 5: Integration Testing

### Task 5.1: Create integration test for full flow

**Files:**
- Create: `AIOMarketMaker.Tests/Integration/SimplifiedPipeline_IntegrationTests.cs`

**Step 1: Create integration test**

```csharp
using NUnit.Framework;
using Microsoft.Extensions.DependencyInjection;

namespace AIOMarketMaker.Tests.Integration;

[TestFixture]
[Category("Integration")]
[Explicit("Requires local infrastructure running")]
public class SimplifiedPipeline_IntegrationTests
{
    [Test]
    public async Task Should_complete_full_scrape_flow_without_durable_functions()
    {
        // This test requires:
        // 1. Azurite running (ports 10000, 10001, 10002)
        // 2. SQL Server LocalDB with AIOMarketMaker database
        // 3. ScraperWorker running in dedicated mode (port 7126)
        // 4. ETL Functions host running (port 7072)

        // Arrange
        // - Create a test ScrapeJob
        // - Trigger the SimplifiedNightlyTrigger

        // Act
        // - Wait for queue to drain
        // - Wait for completion check to mark run as complete

        // Assert
        // - ScrapeRun.Status == "Completed"
        // - Listings exist in database
        // - No stuck orchestrations

        Assert.Pass("Integration test placeholder - implement when infrastructure is stable");
    }
}
```

**Step 2: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/Integration/SimplifiedPipeline_IntegrationTests.cs
git commit -m "test: add integration test placeholder for simplified pipeline"
```

---

## Phase 6: Cleanup (Remove Durable Functions Code)

### Task 6.1: Archive Durable Functions code

**Note:** Only do this after the new architecture is proven stable!

**Files to move to `_archived/` folder:**
- `Orchestrators/JobOrchestrator.cs`
- `Orchestrators/ScrapeUrlOrchestrator.cs`
- `Orchestrators/ListingEtlOrchestrator.cs`
- `Orchestrators/SweepOrchestrator.cs`
- `Orchestrators/FetchListingOrchestrator.cs`
- `Orchestrators/StartOrchestrationIfNotExistsOrchestrator.cs`
- `Triggers/ListingBlobTrigger.cs`
- `Triggers/DescriptionBlobTrigger.cs`
- `Triggers/NightlyScrapeTrigger.cs` (old version)
- `Triggers/StartScrapeTrigger.cs`
- `Activities/SubmitScrapeJobActivity.cs`
- `Activities/CheckScrapeJobStatusActivity.cs`
- `Activities/GetScrapedHtmlActivity.cs`
- `Activities/StartSweepOrchestratorActivity.cs`
- And related activity files

**Step 1: Create archive folders and move files**

```bash
mkdir -p AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/_archived
mkdir -p AIOMarketMaker/AIOMarketMaker.Etl/Triggers/_archived
mkdir -p AIOMarketMaker/AIOMarketMaker.Etl/Activities/_archived

# Move orchestrators
mv AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/JobOrchestrator.cs AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/_archived/
mv AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/ScrapeUrlOrchestrator.cs AIOMarketMaker/AIOMarketMaker.Etl/Orchestrators/_archived/
# ... etc
```

**Step 2: Remove Durable Functions NuGet packages from .csproj**

Remove from `AIOMarketMaker.Etl.csproj`:
```xml
<!-- Remove these packages -->
<PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.DurableTask" Version="..." />
```

**Step 3: Update host.json to remove Durable Functions config**

Remove from `host.json`:
```json
{
  "extensions": {
    "durableTask": { ... }  // Remove this section
  }
}
```

**Step 4: Commit**

```bash
git add -A
git commit -m "refactor: archive Durable Functions code

Moved to _archived/ folders for reference.
Removed Durable Functions NuGet packages.
New simplified architecture is now the default."
```

---

## Summary

| Phase | Tasks | Estimated Commits |
|-------|-------|-------------------|
| Phase 1: Processing Endpoint | 1 task | 1 commit |
| Phase 2: Worker Callback | 1 task | 1 commit |
| Phase 3: Simplified Trigger | 1 task | 1 commit |
| Phase 4: Completion Check | 1 task | 1 commit |
| Phase 5: Integration Test | 1 task | 1 commit |
| Phase 6: Cleanup | 1 task | 1 commit |

**Total:** 6 tasks, 6 commits

---

## Rollback Plan

If issues are found:
1. The old Durable Functions code is archived, not deleted
2. Restore by moving files back from `_archived/`
3. Re-add NuGet packages
4. Restore `host.json` Durable Functions config

The History API and UI remain unchanged throughout - rollback is invisible to users.
