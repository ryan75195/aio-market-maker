# E2E Test Suite Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement a minimal E2E test suite that validates the full scraping pipeline (search → parse → store) using mocked eBay HTML and real AIOWebScraper.

**Architecture:** MockEbayServer serves HTML snapshots on localhost:9999, AIOWebScraper fetches from it, parsers process HTML, results stored in in-memory SQLite. Tier 2 tests hit real eBay for contract validation.

**Tech Stack:** NUnit, ASP.NET Core minimal API, Microsoft.EntityFrameworkCore.Sqlite, existing HTML snapshots.

---

## Prerequisites

Before running E2E tests:
```bash
cd <EXTERNAL_SCRAPER_REPO>/ScraperWorker
dotnet run -- --dedicated-mode
```

---

### Task 1: Add ASP.NET Core Package Reference

**Files:**
- Modify: `AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj`

**Step 1: Add package reference for minimal API**

Edit the csproj to add the ASP.NET Core package needed for the mock server:

```xml
<PackageReference Include="Microsoft.AspNetCore.App" />
```

**Step 2: Verify build succeeds**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj
git commit -m "chore: add ASP.NET Core package for E2E mock server"
```

---

### Task 2: Create MockEbayServer

**Files:**
- Create: `AIOMarketMaker.Tests/E2E/MockEbayServer.cs`

**Step 1: Create the E2E directory**

```bash
mkdir -p AIOMarketMaker/AIOMarketMaker.Tests/E2E
```

**Step 2: Write the MockEbayServer class**

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace AIOMarketMaker.Tests.E2E;

public class MockEbayServer : IDisposable
{
    private WebApplication? _app;
    private readonly int _port;
    private readonly string _dataDirectory;
    private Task? _runTask;

    // Map listing IDs to HTML filenames
    private static readonly Dictionary<string, string> ListingFiles = new()
    {
        { "306278488042", "ActiveBuyItNowListing.htm" },
        { "256918168190", "SoldBuyNowListing.htm" },
    };

    public MockEbayServer(int port = 9999)
    {
        _port = port;
        _dataDirectory = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "Data"));
    }

    public string BaseUrl => $"http://localhost:{_port}";

    public void Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();

        // Route: /itm/{id} → Listings/*.htm
        _app.MapGet("/itm/{id}", (string id) => ServeListingHtml(id));

        // Route: /sch/i.html → Search/*.htm
        _app.MapGet("/sch/i.html", (HttpContext ctx) => ServeSearchHtml(ctx.Request.Query));

        _runTask = _app.RunAsync($"http://localhost:{_port}");
    }

    private IResult ServeListingHtml(string id)
    {
        if (ListingFiles.TryGetValue(id, out var filename))
        {
            var filePath = Path.Combine(_dataDirectory, "Listings", filename);
            if (File.Exists(filePath))
            {
                var html = File.ReadAllText(filePath);
                return Results.Content(html, "text/html");
            }
        }
        return Results.NotFound($"No mock HTML for listing {id}");
    }

    private IResult ServeSearchHtml(IQueryCollection query)
    {
        var isSold = query.ContainsKey("LH_Sold");
        var filename = isSold
            ? "Sold_With_Small_Number_of_Real_Results.htm"
            : "SearchResultsContainingPriceRanges.htm";

        var filePath = Path.Combine(_dataDirectory, "Search", filename);
        if (File.Exists(filePath))
        {
            var html = File.ReadAllText(filePath);
            return Results.Content(html, "text/html");
        }
        return Results.NotFound($"No mock HTML for search");
    }

    public void Dispose()
    {
        if (_app != null)
        {
            _app.StopAsync().Wait(TimeSpan.FromSeconds(5));
            _app.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
    }
}
```

**Step 3: Verify build succeeds**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add AIOMarketMaker.Tests/E2E/MockEbayServer.cs
git commit -m "feat: add MockEbayServer for E2E tests"
```

---

### Task 3: Create TestableEbayUrlBuilder

**Files:**
- Create: `AIOMarketMaker.Tests/E2E/TestableEbayUrlBuilder.cs`

The real `EbayUrlBuilder` uses `https://www.ebay.co.uk`. We need a version that points to our mock server.

**Step 1: Write the TestableEbayUrlBuilder class**

```csharp
using System.Web;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.E2E;

public class TestableEbayUrlBuilder : IEbayUrlBuilder
{
    private readonly string _baseUrl;

    public TestableEbayUrlBuilder(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public string BuildSearchUrl(string query, bool sold, int page, Condition condition, BuyingFormat buyingFormat)
    {
        var flags = $"{(sold ? "&LH_Sold=1&LH_Complete=1" : string.Empty)}" +
                    $"{(buyingFormat == BuyingFormat.BUY_NOW ? "&LH_BIN=1" : string.Empty)}" +
                    $"{(buyingFormat == BuyingFormat.AUCTION ? "&LH_Auction=1" : string.Empty)}" +
                    $"{(condition != Condition.NULL ? $"&LH_ItemCondition={GetConditionValue(condition)}" : string.Empty)}";

        return $"{_baseUrl}/sch/i.html?_nkw={HttpUtility.UrlEncode(query)}{flags}" +
               $"&_pgn={page}&_ipg=240&LH_TitleDesc=0";
    }

    public string BuildListingUrl(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("Item ID cannot be null or empty.", nameof(itemId));

        return $"{_baseUrl}/itm/{HttpUtility.UrlEncode(itemId)}";
    }

    private static int GetConditionValue(Condition condition) => condition switch
    {
        Condition.NEW => 1000,
        Condition.USED => 3000,
        Condition.FOR_PARTS_NOT_WORKING => 7000,
        Condition.GOOD_REFURBISHED => 2030,
        Condition.VERY_GOOD_REFURBISHED => 2020,
        Condition.EXCELLENT_REFURBISHED => 2010,
        Condition.OPENED_NEVER_USED => 1500,
        _ => 0
    };
}
```

**Step 2: Verify build succeeds**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker.Tests/E2E/TestableEbayUrlBuilder.cs
git commit -m "feat: add TestableEbayUrlBuilder for mock server URLs"
```

---

### Task 4: Create E2ETestFixture Base Class

**Files:**
- Create: `AIOMarketMaker.Tests/E2E/E2ETestFixture.cs`

**Step 1: Write the shared fixture base class**

```csharp
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ScraperWorker.Services;
using System.Net.Sockets;

namespace AIOMarketMaker.Tests.E2E;

public abstract class E2ETestFixture
{
    protected static MockEbayServer? MockServer;
    protected EtlDbContext DbContext = null!;
    protected IEbayScraper EbayScraper = null!;
    protected SqliteConnection Connection = null!;

    private const string ScraperApiUrl = "http://localhost:7126/";
    private const int MockEbayPort = 9999;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Start mock eBay server (shared across all tests in fixture)
        MockServer = new MockEbayServer(MockEbayPort);
        MockServer.Start();

        // Verify AIOWebScraper is running
        if (!IsScraperApiAvailable())
        {
            Assert.Ignore("AIOWebScraper not running on localhost:7126. Start with: dotnet run -- --dedicated-mode");
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        MockServer?.Dispose();
        MockServer = null;
    }

    [SetUp]
    public async Task SetUp()
    {
        // Create in-memory SQLite database
        Connection = new SqliteConnection("Data Source=:memory:");
        await Connection.OpenAsync();

        var options = new DbContextOptionsBuilder<EtlDbContext>()
            .UseSqlite(Connection)
            .Options;

        DbContext = new EtlDbContext(options);
        await DbContext.Database.EnsureCreatedAsync();

        // Build services with mock URL builder pointing to our mock server
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

        // Use testable URL builder pointing to mock server
        services.AddSingleton<IEbayUrlBuilder>(new TestableEbayUrlBuilder(MockServer!.BaseUrl));

        // Real parsers
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();

        // Real WebscraperClient pointing to real AIOWebScraper
        services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
        {
            client.BaseAddress = new Uri(ScraperApiUrl);
        });
        services.AddSingleton(new ScraperApiConfig(ScraperApiUrl, ""));

        // Mock job repository (we don't need Azure Storage for E2E tests)
        var mockJobRepo = new Mock<IJobRepository>();
        services.AddSingleton(mockJobRepo.Object);

        // Real EbayScraper
        services.AddSingleton<IEbayScraper, EbayScraper>();

        // Database context
        services.AddSingleton(DbContext);

        var provider = services.BuildServiceProvider();
        EbayScraper = provider.GetRequiredService<IEbayScraper>();
    }

    [TearDown]
    public async Task TearDown()
    {
        await DbContext.DisposeAsync();
        await Connection.DisposeAsync();
    }

    private static bool IsScraperApiAvailable()
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("localhost", 7126);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
```

**Step 2: Verify build succeeds**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker.Tests/E2E/E2ETestFixture.cs
git commit -m "feat: add E2ETestFixture base class with shared setup"
```

---

### Task 5: Implement Tier 1 Test - Search Active Listings

**Files:**
- Create: `AIOMarketMaker.Tests/E2E/ScrapePipeline_E2ETests.cs`

**Step 1: Write the first E2E test**

```csharp
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.E2E;

[TestFixture]
[Category("E2E")]
public class ScrapePipeline_E2ETests : E2ETestFixture
{
    [Test]
    public async Task Should_search_active_listings_and_return_results()
    {
        // Act - search using mock eBay server
        var results = await EbayScraper.SearchActiveListings(
            "test",
            BuyingFormat.BUY_NOW,
            Condition.USED,
            itemLimit: 10);

        // Assert - verify we got parsed results
        var resultList = results.ToList();
        Assert.That(resultList, Is.Not.Empty, "Should return at least one listing from mock HTML");
        Assert.That(resultList.All(r => !string.IsNullOrEmpty(r.ListingId)), Is.True,
            "All results should have a ListingId");
        Assert.That(resultList.All(r => r.Price > 0), Is.True,
            "All results should have a price > 0");
    }
}
```

**Step 2: Run test to verify it fails or passes based on setup**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~Should_search_active_listings_and_return_results" -v n`

Expected: Either PASS (if AIOWebScraper running) or IGNORED (if not running)

**Step 3: Commit**

```bash
git add AIOMarketMaker.Tests/E2E/ScrapePipeline_E2ETests.cs
git commit -m "feat: add E2E test for active listings search"
```

---

### Task 6: Implement Tier 1 Test - Search Sold Listings

**Files:**
- Modify: `AIOMarketMaker.Tests/E2E/ScrapePipeline_E2ETests.cs`

**Step 1: Add test for sold listings with date filter**

Add this test method to the `ScrapePipeline_E2ETests` class:

```csharp
[Test]
public async Task Should_search_sold_listings_with_date_filter()
{
    // Arrange - use date range that matches the mock HTML data
    var startDate = new DateTime(2025, 4, 1);
    var endDate = new DateTime(2025, 5, 15);

    // Act
    var results = await EbayScraper.SearchSoldListings(
        "test",
        BuyingFormat.BUY_NOW,
        Condition.USED,
        startDate,
        endDate);

    // Assert
    var resultList = results.ToList();
    Assert.That(resultList, Is.Not.Empty, "Should return sold listings from mock HTML");
    Assert.That(resultList.All(r => !string.IsNullOrEmpty(r.ListingId)), Is.True,
        "All results should have a ListingId");
    Assert.That(resultList.All(r => r.EndDateUtc >= startDate && r.EndDateUtc <= endDate), Is.True,
        "All results should be within date range");
}
```

**Step 2: Run test**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~Should_search_sold_listings_with_date_filter" -v n`

Expected: Either PASS or IGNORED

**Step 3: Commit**

```bash
git add AIOMarketMaker.Tests/E2E/ScrapePipeline_E2ETests.cs
git commit -m "feat: add E2E test for sold listings search with date filter"
```

---

### Task 7: Implement Tier 1 Test - Fetch Full Listing Details

**Files:**
- Modify: `AIOMarketMaker.Tests/E2E/ScrapePipeline_E2ETests.cs`

**Step 1: Add test for fetching full listing details**

Add this test method:

```csharp
[Test]
public async Task Should_fetch_full_listing_details()
{
    // Arrange - use listing ID that maps to our mock HTML
    var listingId = "306278488042"; // Maps to ActiveBuyItNowListing.htm

    // Act
    var results = await EbayScraper.GetItemsFromListings(new[] { listingId });

    // Assert
    var resultList = results.ToList();
    Assert.That(resultList, Has.Count.EqualTo(1), "Should return exactly one listing");

    var listing = resultList.First();
    Assert.That(listing.ListingId, Is.EqualTo(listingId), "ListingId should match");
    Assert.That(listing.Title, Is.Not.Null.And.Not.Empty, "Should have a title");
    Assert.That(listing.Price, Is.GreaterThan(0), "Should have a price");
}
```

**Step 2: Run test**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~Should_fetch_full_listing_details" -v n`

Expected: Either PASS or IGNORED

**Step 3: Commit**

```bash
git add AIOMarketMaker.Tests/E2E/ScrapePipeline_E2ETests.cs
git commit -m "feat: add E2E test for fetching full listing details"
```

---

### Task 8: Implement Tier 1 Test - Handle Scraper Failure

**Files:**
- Modify: `AIOMarketMaker.Tests/E2E/ScrapePipeline_E2ETests.cs`

**Step 1: Add test for graceful error handling**

Add this test method:

```csharp
[Test]
public async Task Should_handle_nonexistent_listing_gracefully()
{
    // Arrange - use listing ID that doesn't exist in mock
    var nonexistentId = "999999999999";

    // Act
    var results = await EbayScraper.GetItemsFromListings(new[] { nonexistentId });

    // Assert - should return empty or handle gracefully, not throw
    var resultList = results.ToList();
    Assert.That(resultList, Is.Empty.Or.All.Matches<AIOMarketMaker.Models.Ebay.EbayProduct>(
        p => p.ListingId == nonexistentId),
        "Should either return empty or return item with ID but possibly null fields");
}
```

**Step 2: Run test**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~Should_handle_nonexistent_listing_gracefully" -v n`

Expected: Either PASS or IGNORED

**Step 3: Commit**

```bash
git add AIOMarketMaker.Tests/E2E/ScrapePipeline_E2ETests.cs
git commit -m "feat: add E2E test for graceful error handling"
```

---

### Task 9: Implement Tier 2 Contract Test - Real eBay Search

**Files:**
- Create: `AIOMarketMaker.Tests/E2E/EbayContract_E2ETests.cs`

**Step 1: Write the contract test class**

```csharp
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ScraperWorker.Services;
using System.Net.Sockets;

namespace AIOMarketMaker.Tests.E2E;

[TestFixture]
[Category("E2E")]
[Category("Contract")]
public class EbayContract_E2ETests
{
    private IEbayScraper _scraper = null!;
    private const string ScraperApiUrl = "http://localhost:7126/";

    [SetUp]
    public void SetUp()
    {
        // Check if scraper is available
        if (!IsScraperApiAvailable())
        {
            Assert.Ignore("AIOWebScraper not running on localhost:7126");
        }

        // Build services with REAL eBay URL builder
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));

        // Real URL builder (hits actual eBay)
        services.AddSingleton<IEbayUrlBuilder, EbayUrlBuilder>();

        // Real parsers
        services.AddSingleton<ISearchParser, EbaySearchParser>();
        services.AddSingleton<IListingParser, EbayListingParser>();

        // Real WebscraperClient
        services.AddHttpClient<IWebscraperClient, WebscraperClient>(client =>
        {
            client.BaseAddress = new Uri(ScraperApiUrl);
        });
        services.AddSingleton(new ScraperApiConfig(ScraperApiUrl, ""));

        // Mock job repository
        var mockJobRepo = new Mock<IJobRepository>();
        services.AddSingleton(mockJobRepo.Object);

        services.AddSingleton<IEbayScraper, EbayScraper>();

        var provider = services.BuildServiceProvider();
        _scraper = provider.GetRequiredService<IEbayScraper>();
    }

    [Test]
    [Explicit]
    public async Task Should_parse_real_ebay_search_page()
    {
        // Act - search real eBay for something common
        var results = await _scraper.SearchActiveListings(
            "iphone case",
            BuyingFormat.BUY_NOW,
            Condition.NEW,
            itemLimit: 5);

        // Assert - should get at least one result if eBay HTML is still parseable
        var resultList = results.ToList();
        Assert.That(resultList, Is.Not.Empty,
            "PARSER MAY BE BROKEN: Could not parse any results from real eBay search page");
        Assert.That(resultList.First().ListingId, Is.Not.Null.And.Not.Empty,
            "PARSER MAY BE BROKEN: ListingId not extracted");
        Assert.That(resultList.First().Title, Is.Not.Null.And.Not.Empty,
            "PARSER MAY BE BROKEN: Title not extracted");
    }

    [Test]
    [Explicit]
    public async Task Should_parse_real_ebay_listing_page()
    {
        // First, find a real listing ID from search
        var searchResults = await _scraper.SearchActiveListings(
            "phone charger",
            BuyingFormat.BUY_NOW,
            Condition.NEW,
            itemLimit: 1);

        var searchList = searchResults.ToList();
        if (!searchList.Any())
        {
            Assert.Ignore("Could not find any listings to test");
        }

        var listingId = searchList.First().ListingId;

        // Act - fetch the full listing
        var results = await _scraper.GetItemsFromListings(new[] { listingId });

        // Assert
        var resultList = results.ToList();
        Assert.That(resultList, Has.Count.EqualTo(1),
            "PARSER MAY BE BROKEN: Could not fetch listing details");
        Assert.That(resultList.First().Title, Is.Not.Null.And.Not.Empty,
            "PARSER MAY BE BROKEN: Title not extracted from listing page");
        Assert.That(resultList.First().Price, Is.GreaterThan(0),
            "PARSER MAY BE BROKEN: Price not extracted from listing page");
    }

    private static bool IsScraperApiAvailable()
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("localhost", 7126);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
```

**Step 2: Verify build succeeds**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add AIOMarketMaker.Tests/E2E/EbayContract_E2ETests.cs
git commit -m "feat: add Tier 2 contract tests for real eBay validation"
```

---

### Task 10: Run All E2E Tests and Verify

**Step 1: Ensure AIOWebScraper is running**

In a separate terminal:
```bash
cd <EXTERNAL_SCRAPER_REPO>/ScraperWorker
dotnet run -- --dedicated-mode
```

**Step 2: Run Tier 1 E2E tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=E2E&Category!=Contract" -v n`

Expected: 4 tests pass (or ignored if scraper not running)

**Step 3: Optionally run Tier 2 contract tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "Category=Contract" -v n`

Expected: 2 tests pass (hits real eBay - use sparingly)

**Step 4: Final commit**

```bash
git add -A
git commit -m "test: complete E2E test suite implementation"
```

---

## Summary

| Task | Component | Tests |
|------|-----------|-------|
| 1 | Package reference | - |
| 2 | MockEbayServer | - |
| 3 | TestableEbayUrlBuilder | - |
| 4 | E2ETestFixture | - |
| 5-8 | Tier 1 tests | 4 tests |
| 9 | Tier 2 tests | 2 tests |
| 10 | Verification | - |

**Run commands:**
```bash
# Tier 1 only (CI, fast)
dotnet test --filter "Category=E2E&Category!=Contract"

# Tier 2 only (manual/scheduled)
dotnet test --filter "Category=Contract"
```
