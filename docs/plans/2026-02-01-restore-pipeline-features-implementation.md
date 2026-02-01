# Restore Pipeline Features Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Restore all dropped features from the Durable Functions migration to achieve full behavioral parity.

**Architecture:** Multi-phase search (sold then active) with pagination, status progression validation using existing `ListingStatusHelper`, and `ListingStatusHistory` tracking on status/price changes.

**Tech Stack:** .NET 8.0, Azure Functions, Entity Framework Core, NUnit/Moq

**Design Document:** `docs/plans/2026-02-01-restore-pipeline-features-design.md`

---

## ⚠️ TDD POLICY - MANDATORY FOR ALL TASKS

**Every task in this plan MUST follow strict TDD (Test-Driven Development):**

1. **AUDIT** - Find existing tests related to the behavior
2. **EVALUATE** - Do they encode the business requirement correctly?
3. **RED** - Write/modify failing test that encodes the REQUIREMENT
4. **RUN** - Verify test fails for the expected reason
5. **GREEN** - Write minimal code to make test pass
6. **RUN** - Verify test passes
7. **COMMIT** - Commit test AND implementation together

**Test command:**
```bash
dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~<TestClassName>"
```

---

## Existing Code Audit

### Already Exists (Don't Recreate!)

| Component | Location | Status |
|-----------|----------|--------|
| `ListingStatusHelper` | `Core/Services/ListingStatusHelper.cs` | Exists, NOT USED |
| `ListingStatusHistory` | `Core/Data/Models/ListingStatusHistory.cs` | Exists, NOT POPULATED |
| `EtlDbContext.ListingStatusHistory` | `Core/Data/EtlDbContext.cs` | Exists |

### Tests Requiring Modification

| Test | Problem | Action |
|------|---------|--------|
| `RunScrapeForJobAsync_should_filter_out_existing_listings` | Creates listing with NULL status | MODIFY to use terminal statuses |
| `Run_should_return_updated_when_listing_exists` | Doesn't test status validation | ADD status validation test |

---

## Phase 1: Fix Terminal Status Filtering Test

### Task 1.1: Audit and Fix Filtering Test

**Files:**
- Modify: `AIOMarketMaker.Tests/Unit/Triggers/SimplifiedScrapeTrigger_UnitTests.cs:144-214`

**Step 1: Audit existing test**

The current test at line 144-214 creates:
```csharp
var existingListing = new Listing { ListingId = "itm002", ScrapeJobId = jobId };
// Problem: No ListingStatus set - defaults to null!
```

This doesn't test the business requirement: "Only filter terminal statuses (Sold/Ended/OutOfStock)"

**Step 2: Modify test to encode business requirement**

Replace the test with:

```csharp
[Test]
public async Task RunScrapeForJobAsync_should_skip_terminal_statuses_but_include_active_for_rescrape()
{
    // Arrange
    var jobId = 1;
    var searchTerm = "test product";

    var scrapeJob = new ScrapeJob { Id = jobId, SearchTerm = searchTerm, IsEnabled = true };
    _dbContext.ScrapeJobs.Add(scrapeJob);

    // Create listings with different statuses
    var activeListing = new Listing { ListingId = "itm001", ScrapeJobId = jobId, ListingStatus = "Active" };
    var soldListing = new Listing { ListingId = "itm002", ScrapeJobId = jobId, ListingStatus = "Sold" };
    var endedListing = new Listing { ListingId = "itm003", ScrapeJobId = jobId, ListingStatus = "Ended" };
    var outOfStockListing = new Listing { ListingId = "itm004", ScrapeJobId = jobId, ListingStatus = "OutOfStock" };
    var newListing = new Listing { ListingId = "itm005", ScrapeJobId = jobId, ListingStatus = null }; // Null status = not terminal

    _dbContext.Listings.AddRange(activeListing, soldListing, endedListing, outOfStockListing);
    await _dbContext.SaveChangesAsync();

    // Setup webscraper to return HTML
    _webscraperClientMock
        .Setup(w => w.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>>(),
            It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync("<html><body>Mock</body></html>");

    // Parser returns all 5 listing IDs (simulating search results)
    var mockProducts = new List<IEbayProductSummary>
    {
        new EbayProductSummary("itm001", "Active Product", 10m, "GBP", 0m, "url1", BuyingFormat.BUY_NOW, Condition.USED, null, null),
        new EbayProductSummary("itm002", "Sold Product", 20m, "GBP", 0m, "url2", BuyingFormat.BUY_NOW, Condition.USED, null, null),
        new EbayProductSummary("itm003", "Ended Product", 30m, "GBP", 0m, "url3", BuyingFormat.BUY_NOW, Condition.USED, null, null),
        new EbayProductSummary("itm004", "OutOfStock Product", 40m, "GBP", 0m, "url4", BuyingFormat.BUY_NOW, Condition.USED, null, null),
        new EbayProductSummary("itm005", "New Product", 50m, "GBP", 0m, "url5", BuyingFormat.BUY_NOW, Condition.USED, null, null),
    };
    _searchParserMock.Setup(s => s.ParseSearchResults(It.IsAny<IDocument>())).Returns(mockProducts);

    _queueClientMock
        .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
        .ReturnsAsync(Response.FromValue(
            QueuesModelFactory.SendReceipt("id", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "pop", DateTimeOffset.UtcNow.AddMinutes(1)),
            Mock.Of<Response>()));

    var trigger = new SimplifiedScrapeTrigger(
        _loggerMock.Object, _dbContext, _webscraperClientMock.Object,
        _searchParserMock.Object, _queueServiceMock.Object);

    // Act
    var result = await trigger.RunScrapeForJobAsync(jobId, searchTerm, "Manual");

    // Assert
    // itm001 (Active) - INCLUDED (re-scrape for price updates)
    // itm002 (Sold) - EXCLUDED (terminal)
    // itm003 (Ended) - EXCLUDED (terminal)
    // itm004 (OutOfStock) - EXCLUDED (terminal)
    // itm005 (null/new) - INCLUDED (new listing)
    Assert.That(result, Is.EqualTo(2), "Should include Active and null-status listings, exclude terminal statuses");

    var enqueuedListings = _dbContext.ScrapeRunListings.Select(l => l.ListingId).ToList();
    Assert.Multiple(() =>
    {
        Assert.That(enqueuedListings, Does.Contain("itm001"), "Active listing should be re-scraped");
        Assert.That(enqueuedListings, Does.Contain("itm005"), "New listing should be scraped");
        Assert.That(enqueuedListings, Does.Not.Contain("itm002"), "Sold listing should be skipped");
        Assert.That(enqueuedListings, Does.Not.Contain("itm003"), "Ended listing should be skipped");
        Assert.That(enqueuedListings, Does.Not.Contain("itm004"), "OutOfStock listing should be skipped");
    });
}
```

**Step 3: Run test to verify it passes**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~should_skip_terminal_statuses"`

Expected: PASS (we already fixed the implementation in commit b4b69e1)

**Step 4: Remove old test**

Delete the old `RunScrapeForJobAsync_should_filter_out_existing_listings` test since it doesn't encode the correct requirement.

**Step 5: Commit**

```bash
git add AIOMarketMaker.Tests/Unit/Triggers/SimplifiedScrapeTrigger_UnitTests.cs
git commit -m "test: fix terminal status filtering test to encode business requirement

OLD: Created listing with null status - didn't test terminal filtering
NEW: Explicitly tests Active/Sold/Ended/OutOfStock behavior"
```

---

## Phase 2: Add Multi-Page Search

### Task 2.1: Add Test for Multi-Page Search

**Files:**
- Modify: `AIOMarketMaker.Tests/Unit/Triggers/SimplifiedScrapeTrigger_UnitTests.cs`

**Step 1: Write failing test**

Add to `SimplifiedScrapeTrigger_UnitTests.cs`:

```csharp
[Test]
public async Task RunScrapeForJobAsync_should_search_multiple_pages_until_no_results()
{
    // Arrange
    var jobId = 1;
    var scrapeJob = new ScrapeJob { Id = jobId, SearchTerm = "test", IsEnabled = true };
    _dbContext.ScrapeJobs.Add(scrapeJob);
    await _dbContext.SaveChangesAsync();

    // Track which URLs were called
    var calledUrls = new List<string>();
    _webscraperClientMock
        .Setup(w => w.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>>(),
            It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
        .Callback<string, IEnumerable<object>, string, TimeSpan?, CancellationToken>((url, _, _, _, _) => calledUrls.Add(url))
        .ReturnsAsync("<html></html>");

    // Setup parser: Page 1 = 2 products, Page 2 = 1 product, Page 3 = 0 products
    var callCount = 0;
    _searchParserMock
        .Setup(s => s.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() =>
        {
            callCount++;
            return callCount switch
            {
                1 => new List<IEbayProductSummary>
                {
                    new EbayProductSummary("itm001", "P1", 10m, "GBP", 0m, "u1", BuyingFormat.BUY_NOW, Condition.USED, null, null),
                    new EbayProductSummary("itm002", "P2", 20m, "GBP", 0m, "u2", BuyingFormat.BUY_NOW, Condition.USED, null, null),
                },
                2 => new List<IEbayProductSummary>
                {
                    new EbayProductSummary("itm003", "P3", 30m, "GBP", 0m, "u3", BuyingFormat.BUY_NOW, Condition.USED, null, null),
                },
                _ => new List<IEbayProductSummary>()
            };
        });

    _queueClientMock
        .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
        .ReturnsAsync(Response.FromValue(
            QueuesModelFactory.SendReceipt("id", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "pop", DateTimeOffset.UtcNow.AddMinutes(1)),
            Mock.Of<Response>()));

    var trigger = new SimplifiedScrapeTrigger(
        _loggerMock.Object, _dbContext, _webscraperClientMock.Object,
        _searchParserMock.Object, _queueServiceMock.Object);

    // Act
    var result = await trigger.RunScrapeForJobAsync(jobId, "test", "Manual");

    // Assert
    Assert.Multiple(() =>
    {
        Assert.That(result, Is.EqualTo(3), "Should find all 3 listings across pages");
        Assert.That(calledUrls.Count, Is.EqualTo(3), "Should call 3 pages (page 1, 2, 3)");
        Assert.That(calledUrls[0], Does.Contain("_pgn=1").Or.Not.Contain("_pgn"), "First call should be page 1");
        Assert.That(calledUrls[1], Does.Contain("_pgn=2"), "Second call should be page 2");
        Assert.That(calledUrls[2], Does.Contain("_pgn=3"), "Third call should be page 3");
    });
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~should_search_multiple_pages"`

Expected: FAIL - Currently only searches page 1

**Step 3: Implement multi-page search**

Modify `SimplifiedScrapeTrigger.cs` - Add pagination loop in `RunScrapeForJobAsync`:

```csharp
// Replace single page fetch with pagination loop
var allListingIds = new HashSet<string>();
var page = 1;
const int maxPages = 100; // Safety limit

while (page <= maxPages)
{
    var searchUrl = _urlBuilder.BuildSearchUrl(searchTerm, sold: false, page: page, Condition.NULL, BuyingFormat.BUY_NOW);
    var html = await _webscraperClient.GetPageHtmlAsync(searchUrl);

    var browsingContext = BrowsingContext.New(Configuration.Default);
    var document = await browsingContext.OpenAsync(request => request.Content(html));

    var products = _searchParser.ParseSearchResults(document);
    var pageListingIds = products
        .Where(p => !string.IsNullOrEmpty(p.ListingId))
        .Select(p => p.ListingId!)
        .ToList();

    if (pageListingIds.Count == 0)
        break; // No more results

    foreach (var id in pageListingIds)
        allListingIds.Add(id);

    page++;
}

_logger.LogInformation("Searched {PageCount} pages, found {Count} unique listings", page - 1, allListingIds.Count);
```

**Step 4: Run test to verify it passes**

Run: `dotnet test AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~should_search_multiple_pages"`

Expected: PASS

**Step 5: Commit**

```bash
git add AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs AIOMarketMaker.Tests/Unit/Triggers/
git commit -m "feat: add multi-page search to SimplifiedScrapeTrigger

Searches until no results returned (max 100 pages).
Deduplicates listings across pages with HashSet."
```

---

## Phase 3: Add Sold Listing Search

### Task 3.1: Add Test for Sold Listing Search Phase

**Files:**
- Modify: `AIOMarketMaker.Tests/Unit/Triggers/SimplifiedScrapeTrigger_UnitTests.cs`
- Modify: `AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs`

**Step 1: Write failing test**

```csharp
[Test]
public async Task RunScrapeForJobAsync_should_search_sold_listings_before_active()
{
    // Arrange
    var jobId = 1;
    var scrapeJob = new ScrapeJob { Id = jobId, SearchTerm = "test", IsEnabled = true };
    _dbContext.ScrapeJobs.Add(scrapeJob);
    await _dbContext.SaveChangesAsync();

    var calledUrls = new List<string>();
    _webscraperClientMock
        .Setup(w => w.GetPageHtmlAsync(It.IsAny<string>(), It.IsAny<IEnumerable<object>>(),
            It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
        .Callback<string, IEnumerable<object>, string, TimeSpan?, CancellationToken>((url, _, _, _, _) => calledUrls.Add(url))
        .ReturnsAsync("<html></html>");

    _searchParserMock
        .Setup(s => s.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(new List<IEbayProductSummary>()); // Empty results to end pagination quickly

    _queueClientMock
        .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
        .ReturnsAsync(Response.FromValue(
            QueuesModelFactory.SendReceipt("id", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "pop", DateTimeOffset.UtcNow.AddMinutes(1)),
            Mock.Of<Response>()));

    var trigger = new SimplifiedScrapeTrigger(
        _loggerMock.Object, _dbContext, _webscraperClientMock.Object,
        _searchParserMock.Object, _queueServiceMock.Object);

    // Act
    await trigger.RunScrapeForJobAsync(jobId, "test", "Manual");

    // Assert - should search sold THEN active
    Assert.That(calledUrls.Count, Is.GreaterThanOrEqualTo(2), "Should search both sold and active");

    var soldSearchIndex = calledUrls.FindIndex(u => u.Contains("LH_Sold=1") || u.Contains("LH_Complete=1"));
    var activeSearchIndex = calledUrls.FindIndex(u => !u.Contains("LH_Sold") && !u.Contains("LH_Complete"));

    Assert.That(soldSearchIndex, Is.GreaterThanOrEqualTo(0), "Should have searched sold listings");
    Assert.That(activeSearchIndex, Is.GreaterThan(soldSearchIndex), "Active search should come after sold search");
}
```

**Step 2: Run test to verify it fails**

Expected: FAIL - Currently no sold search

**Step 3: Implement sold listing search**

Add sold search phase before active search in `RunScrapeForJobAsync`:

```csharp
// Phase 1: Search Sold Listings
scrapeRun.CurrentPhase = "Searching Sold";
await _dbContext.SaveChangesAsync();

var soldListingIds = new HashSet<string>();
page = 1;
while (page <= maxPages)
{
    var soldUrl = _urlBuilder.BuildSearchUrl(searchTerm, sold: true, page: page, Condition.NULL, BuyingFormat.BUY_NOW);
    // ... same pagination logic
}

// Phase 2: Search Active Listings
scrapeRun.CurrentPhase = "Searching Active";
await _dbContext.SaveChangesAsync();
// ... existing active search code
```

**Step 4: Run test to verify it passes**

**Step 5: Commit**

---

## Phase 4: Add Active→Sold Detection

### Task 4.1: Add Test for Active→Sold Detection

**Files:**
- Modify: `AIOMarketMaker.Tests/Unit/Triggers/SimplifiedScrapeTrigger_UnitTests.cs`
- Modify: `AIOMarketMaker.Etl/Triggers/SimplifiedScrapeTrigger.cs`

**Step 1: Write failing test**

```csharp
[Test]
public async Task RunScrapeForJobAsync_should_detect_active_to_sold_transitions()
{
    // Arrange
    var jobId = 1;
    var scrapeJob = new ScrapeJob { Id = jobId, SearchTerm = "test", IsEnabled = true };
    _dbContext.ScrapeJobs.Add(scrapeJob);

    // Existing Active listing in database
    var activeListing = new Listing { ListingId = "itm001", ScrapeJobId = jobId, ListingStatus = "Active" };
    _dbContext.Listings.Add(activeListing);
    await _dbContext.SaveChangesAsync();

    // Setup: Sold search returns this listing (it sold!)
    var soldProducts = new List<IEbayProductSummary>
    {
        new EbayProductSummary("itm001", "Now Sold", 100m, "GBP", 0m, "url", BuyingFormat.BUY_NOW, Condition.USED, null, null),
    };

    var callCount = 0;
    _searchParserMock
        .Setup(s => s.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() =>
        {
            callCount++;
            // First call = sold search, second = active search
            return callCount == 1 ? soldProducts : new List<IEbayProductSummary>();
        });

    // ... rest of setup

    // Act
    var result = await trigger.RunScrapeForJobAsync(jobId, "test", "Manual");

    // Assert - itm001 should be included for re-scraping to get sold price
    Assert.That(result, Is.GreaterThanOrEqualTo(1));
    var enqueuedListings = _dbContext.ScrapeRunListings.Select(l => l.ListingId).ToList();
    Assert.That(enqueuedListings, Does.Contain("itm001"), "Active→Sold transition should be re-scraped");
}
```

**Step 2-5: Implement and commit**

---

## Phase 5: Add Status Validation to ProcessListingEndpoint

### Task 5.1: Add Test for Invalid Status Transition Rejection

**Files:**
- Modify: `AIOMarketMaker.Tests/Unit/Endpoints/ProcessListingEndpoint_UnitTests.cs`
- Modify: `AIOMarketMaker.Etl/Endpoints/ProcessListingEndpoint.cs`

**Step 1: Write failing test**

```csharp
[Test]
public async Task Run_should_skip_update_when_status_transition_is_invalid()
{
    // Arrange - Existing Sold listing
    var scrapeRun = new ScrapeRun { Id = 1, Status = "Running", ListingsProcessed = 0 };
    var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
    var existingListing = new Listing
    {
        ListingId = "123",
        ScrapeJobId = 1,
        Title = "Original Title",
        Price = 100m,
        ListingStatus = "Sold"  // Terminal status!
    };

    _dbContext.ScrapeRuns.Add(scrapeRun);
    _dbContext.ScrapeJobs.Add(scrapeJob);
    _dbContext.Listings.Add(existingListing);

    var scrapeRunListing = new ScrapeRunListing { ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "123", Status = "Pending" };
    _dbContext.ScrapeRunListings.Add(scrapeRunListing);
    await _dbContext.SaveChangesAsync();

    SetupBlobWithContent("<html></html>");

    // Parser returns Active status (invalid: Sold→Active)
    var parsedListing = new ExtractedEbayListing(
        id: "123",
        title: "New Title",
        price: 90m,
        currency: "GBP",
        shippingCost: null,
        Condition: Condition.USED,
        images: null,
        listingStatus: EbayListingStatus.Active,  // Trying to go Sold→Active!
        purchaseFormat: null,
        ItemSpecifics: null,
        descriptionSource: null,
        SoldDateUtc: null,
        Location: null,
        Url: null
    );

    _listingParserMock
        .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
        .Returns(parsedListing);

    var endpoint = new ProcessListingEndpoint(
        _blobServiceMock.Object, _dbContext, _listingParserMock.Object, _loggerMock.Object);

    var request = new ProcessListingRequest(1, 0, "123", 1, "path");
    var httpRequest = MockHttpRequestData.Create(request);

    // Act
    var response = await endpoint.Run(httpRequest);
    var responseBody = await MockHttpRequestData.ReadResponseAsync<ProcessListingResponse>(response);

    // Assert - Should skip the update
    Assert.Multiple(() =>
    {
        Assert.That(responseBody.Status, Is.EqualTo("skipped"));
        Assert.That(responseBody.ErrorMessage, Does.Contain("invalid").IgnoreCase.Or.Contain("transition").IgnoreCase);
    });

    // Verify listing was NOT updated
    var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.ListingId == "123");
    Assert.Multiple(() =>
    {
        Assert.That(listing!.Title, Is.EqualTo("Original Title"), "Title should not change");
        Assert.That(listing.ListingStatus, Is.EqualTo("Sold"), "Status should remain Sold");
    });
}
```

**Step 2: Run test to verify it fails**

Expected: FAIL - Currently updates regardless of status transition validity

**Step 3: Implement status validation**

Add to `ProcessListingEndpoint.cs`:

```csharp
using AIOMarketMaker.Core.Services; // For ListingStatusHelper

// Before upsert:
var existingListing = await _dbContext.Listings
    .FirstOrDefaultAsync(l => l.ListingId == input.ListingId && l.ScrapeJobId == input.ScrapeJobId);

if (existingListing != null)
{
    var newStatus = parsedListing.listingStatus?.ToString();
    if (!ListingStatusHelper.CanUpdateStatus(existingListing.ListingStatus, newStatus))
    {
        _logger.LogWarning("Invalid status transition for {ListingId}: {OldStatus} → {NewStatus}",
            input.ListingId, existingListing.ListingStatus, newStatus);

        // Still mark as processed, but skip the update
        await IncrementScrapeRunCountersAsync(input.ScrapeRunId, "skipped");

        var skipResponse = req.CreateResponse(HttpStatusCode.OK);
        await skipResponse.WriteAsJsonAsync(new ProcessListingResponse(
            true, "skipped", $"Invalid status transition: {existingListing.ListingStatus} → {newStatus}"));
        return skipResponse;
    }
}
```

**Step 4: Run test to verify it passes**

**Step 5: Commit**

---

## Phase 6: Add ListingStatusHistory Tracking

### Task 6.1: Add Test for History Creation on Status Change

**Files:**
- Modify: `AIOMarketMaker.Tests/Unit/Endpoints/ProcessListingEndpoint_UnitTests.cs`
- Modify: `AIOMarketMaker.Etl/Endpoints/ProcessListingEndpoint.cs`

**Step 1: Write failing test**

```csharp
[Test]
public async Task Run_should_create_status_history_when_status_changes()
{
    // Arrange - Existing Active listing
    var scrapeRun = new ScrapeRun { Id = 1, Status = "Running", ListingsProcessed = 0 };
    var scrapeJob = new ScrapeJob { Id = 1, SearchTerm = "test" };
    var existingListing = new Listing
    {
        Id = 1,
        ListingId = "123",
        ScrapeJobId = 1,
        Title = "Product",
        Price = 100m,
        ListingStatus = "Active"
    };

    _dbContext.ScrapeRuns.Add(scrapeRun);
    _dbContext.ScrapeJobs.Add(scrapeJob);
    _dbContext.Listings.Add(existingListing);

    var scrapeRunListing = new ScrapeRunListing { ScrapeRunId = 1, ScrapeJobId = 1, ListingId = "123", Status = "Pending" };
    _dbContext.ScrapeRunListings.Add(scrapeRunListing);
    await _dbContext.SaveChangesAsync();

    SetupBlobWithContent("<html></html>");

    // Parser returns Sold status (valid transition: Active→Sold)
    var parsedListing = new ExtractedEbayListing(
        id: "123",
        title: "Product",
        price: 95m,  // Sold price
        currency: "GBP",
        shippingCost: null,
        Condition: Condition.USED,
        images: null,
        listingStatus: EbayListingStatus.Sold,
        purchaseFormat: null,
        ItemSpecifics: null,
        descriptionSource: null,
        SoldDateUtc: DateTime.UtcNow,
        Location: null,
        Url: null
    );

    _listingParserMock
        .Setup(p => p.ParseProductListing(It.IsAny<IDocument>(), It.IsAny<string>()))
        .Returns(parsedListing);

    var endpoint = new ProcessListingEndpoint(
        _blobServiceMock.Object, _dbContext, _listingParserMock.Object, _loggerMock.Object);

    var request = new ProcessListingRequest(1, 0, "123", 1, "path");
    var httpRequest = MockHttpRequestData.Create(request);

    // Act
    await endpoint.Run(httpRequest);

    // Assert - ListingStatusHistory should be created
    var history = await _dbContext.ListingStatusHistory
        .Where(h => h.ListingId == 1)
        .ToListAsync();

    Assert.That(history.Count, Is.EqualTo(1), "Should create one history record");
    Assert.Multiple(() =>
    {
        Assert.That(history[0].ListingStatus, Is.EqualTo("Sold"));
        Assert.That(history[0].Price, Is.EqualTo(95m));
        Assert.That(history[0].Source, Is.EqualTo("StatusUpdate"));
    });
}
```

**Step 2: Run test to verify it fails**

Expected: FAIL - Currently doesn't create history records

**Step 3: Implement history tracking**

Add after successful upsert in `ProcessListingEndpoint.cs`:

```csharp
// After updating the listing, check if we need to create history
if (existingListing != null)
{
    var statusChanged = existingListing.ListingStatus != newStatus;
    var priceChanged = existingListing.Price != parsedListing.price;

    if (statusChanged || priceChanged)
    {
        var historyRecord = new ListingStatusHistory
        {
            ListingId = existingListing.Id,
            ListingStatus = newStatus ?? "Unknown",
            Price = parsedListing.price,
            SoldDateUtc = parsedListing.SoldDateUtc,
            RecordedUtc = DateTime.UtcNow,
            Source = statusChanged ? "StatusUpdate" : "PriceUpdate"
        };
        _dbContext.ListingStatusHistory.Add(historyRecord);
        await _dbContext.SaveChangesAsync();
    }
}
else
{
    // New listing - create initial history record
    var listing = await _dbContext.Listings.FirstAsync(l => l.ListingId == input.ListingId);
    var historyRecord = new ListingStatusHistory
    {
        ListingId = listing.Id,
        ListingStatus = newStatus ?? "Active",
        Price = parsedListing.price,
        SoldDateUtc = parsedListing.SoldDateUtc,
        RecordedUtc = DateTime.UtcNow,
        Source = "InitialScrape"
    };
    _dbContext.ListingStatusHistory.Add(historyRecord);
    await _dbContext.SaveChangesAsync();
}
```

**Step 4: Run test to verify it passes**

**Step 5: Commit**

---

## Phase 7: Remove [Explicit] from Integration Tests

### Task 7.1: Enable Integration Tests in CI

**Files:**
- Modify: `AIOMarketMaker.Tests/Integration/SimplifiedPipeline_IntegrationTests.cs`

**Step 1: Find and remove [Explicit] attributes**

```bash
grep -n "Explicit" AIOMarketMaker.Tests/Integration/*.cs
```

**Step 2: Remove [Explicit] and ensure tests can run**

**Step 3: Commit**

```bash
git commit -m "test: enable integration tests in CI by removing [Explicit]"
```

---

## Summary

| Phase | Tasks | New Tests | Modified Tests | Implementation Changes |
|-------|-------|-----------|----------------|----------------------|
| 1 | Fix filtering test | 0 | 1 (replace) | 0 |
| 2 | Multi-page search | 1 | 0 | 1 (SimplifiedScrapeTrigger) |
| 3 | Sold listing search | 1 | 0 | 1 (SimplifiedScrapeTrigger) |
| 4 | Active→Sold detection | 1 | 0 | 1 (SimplifiedScrapeTrigger) |
| 5 | Status validation | 1 | 0 | 1 (ProcessListingEndpoint) |
| 6 | History tracking | 1 | 0 | 1 (ProcessListingEndpoint) |
| 7 | Enable CI tests | 0 | 0 | 0 |

**Total:** ~6 new tests, 1 modified test, 2 files with significant changes
