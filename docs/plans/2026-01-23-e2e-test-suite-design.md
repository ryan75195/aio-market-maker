# E2E Test Suite Design

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:writing-plans to create the implementation plan from this design.

**Goal:** Create a minimal E2E test suite that validates the full scraping pipeline locally while also detecting eBay HTML changes.

**Architecture:** Two-tier testing approach - deterministic tests for CI using mocked eBay (HTML snapshots), plus contract smoke tests hitting real eBay on schedule.

**Tech Stack:** NUnit, in-memory SQLite, ASP.NET Core minimal API (mock server), existing HTML snapshots.

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                        Test Fixture                              │
├─────────────────────────────────────────────────────────────────┤
│  [OneTimeSetUp]                                                  │
│    • Start MockEbayServer (localhost:9999)                       │
│    • Verify AIOWebScraper running (localhost:7126)               │
│                                                                  │
│  [SetUp] per test                                                │
│    • Create in-memory SQLite + run migrations                    │
│    • Build DI container with real services                       │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  Test: Should_scrape_search_and_store_listings                   │
│                                                                  │
│    EbayScraper.SearchActiveListings("test")                      │
│         │                                                        │
│         ▼                                                        │
│    WebscraperClient → AIOWebScraper → MockEbayServer             │
│         │                     (serves HTML snapshots)            │
│         ▼                                                        │
│    EbaySearchParser (extracts listing IDs)                       │
│         │                                                        │
│         ▼                                                        │
│    Assert: Listings saved to in-memory SQLite                    │
└─────────────────────────────────────────────────────────────────┘
```

**Key points:**
- MockEbayServer serves existing HTML snapshots from `Tests/Data/`
- Real AIOWebScraper handles the actual scraping (must be started manually)
- Real parsers process the HTML
- Real EF Core writes to in-memory SQLite
- Only eBay itself is mocked (for Tier 1)

---

## Tier 1: Deterministic E2E Tests

Fast, reliable tests that run on every commit/PR.

### Test Cases

| # | Test Name | What it validates |
|---|-----------|-------------------|
| 1 | `Should_search_active_listings_and_store_in_db` | Search → Parse summaries → Store listings |
| 2 | `Should_search_sold_listings_with_date_filter` | Sold search → Date filtering → Store with status |
| 3 | `Should_fetch_full_listing_details` | Get single listing → Parse details (specs, images, description) → Store |
| 4 | `Should_handle_scraper_failure_gracefully` | AIOWebScraper returns error → No crash, appropriate error handling |

### Test Attributes

```csharp
[TestFixture]
[Category("E2E")]
public class ScrapePipeline_E2ETests
```

---

## Tier 2: Contract Smoke Tests

Hit real eBay to detect HTML structure changes. Run manually or on schedule.

### Test Cases

| # | Test Name | What it validates |
|---|-----------|-------------------|
| 1 | `Should_parse_real_ebay_search_page` | Fetch real search, verify parser extracts ≥1 result |
| 2 | `Should_parse_real_ebay_listing_page` | Fetch known active listing, verify core fields parse |

### Test Attributes

```csharp
[TestFixture]
[Category("E2E")]
[Category("Contract")]
public class EbayContract_E2ETests
{
    [Test]
    [Explicit]  // Won't run in normal test runs
    public async Task Should_parse_real_ebay_search_page()
```

### On Failure Workflow

1. Developer investigates if eBay changed HTML
2. Updates selectors in parsers
3. Updates HTML snapshots for Tier 1 tests
4. Commits fix

---

## MockEbayServer Component

Simple HTTP server serving existing HTML snapshots.

### URL Routing

| Request Pattern | Serves |
|-----------------|--------|
| `/sch/i.html?_nkw=*&LH_Sold=1` | `Search/Sold_With_Small_Number_of_Real_Results.htm` |
| `/sch/i.html?_nkw=*` | `Search/SearchResultsContainingPriceRanges.htm` |
| `/itm/306278488042` | `Listings/ActiveBuyItNowListing.htm` |
| `/itm/256918168190` | `Listings/SoldBuyNowListing.htm` |

### Implementation Sketch

```csharp
public class MockEbayServer : IDisposable
{
    private readonly WebApplication _app;
    private readonly int _port;
    private readonly string _dataDirectory;

    public MockEbayServer(int port = 9999)
    {
        _port = port;
        _dataDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "Data");
    }

    public void Start()
    {
        var builder = WebApplication.CreateBuilder();
        _app = builder.Build();

        // Route: /itm/{id} → Listings/*.htm
        _app.MapGet("/itm/{id}", (string id) =>
            ServeListingHtml(id));

        // Route: /sch/i.html → Search/*.htm
        _app.MapGet("/sch/i.html", (HttpContext ctx) =>
            ServeSearchHtml(ctx.Request.Query));

        _app.RunAsync($"http://localhost:{_port}");
    }

    public void Dispose() => _app?.StopAsync();
}
```

---

## Database Setup

Each test gets a fresh in-memory SQLite database.

```csharp
[SetUp]
public async Task SetUp()
{
    var options = new DbContextOptionsBuilder<EtlDbContext>()
        .UseSqlite("Data Source=:memory:")
        .Options;

    _dbContext = new EtlDbContext(options);
    await _dbContext.Database.OpenConnectionAsync();
    await _dbContext.Database.EnsureCreatedAsync();

    // Run migrations if needed
    var migrationRunner = new MigrationRunner(_dbContext);
    await migrationRunner.RunMigrationsAsync();
}
```

---

## File Structure

```
AIOMarketMaker.Tests/
├── E2E/
│   ├── MockEbayServer.cs           # HTTP server serving snapshots
│   ├── E2ETestFixture.cs           # Shared setup (mock server, DI)
│   ├── ScrapePipeline_E2ETests.cs  # Tier 1: 4 deterministic tests
│   └── EbayContract_E2ETests.cs    # Tier 2: 2 real eBay smoke tests
├── Data/
│   ├── Listings/                   # (existing - 7 HTML files)
│   └── Search/                     # (existing - 2 HTML files)
```

---

## Prerequisites

### Manual Setup Required

1. **Start AIOWebScraper** before running E2E tests:
   ```bash
   cd AIOWebScraper/ScraperWorker
   dotnet run -- --dedicated-mode
   ```

2. Tests skip gracefully with `Assert.Ignore()` if AIOWebScraper not running.

### No New Dependencies

Uses existing packages:
- `Microsoft.AspNetCore.Mvc.Testing`
- `Microsoft.EntityFrameworkCore.Sqlite`

---

## Run Commands

```bash
# Tier 1 only (CI, fast)
dotnet test --filter "Category=E2E&Category!=Contract"

# Tier 2 only (manual/scheduled)
dotnet test --filter "Category=Contract"

# All E2E
dotnet test --filter "Category=E2E"
```

---

## CI Integration (Future)

### GitHub Actions for Tier 2

```yaml
# .github/workflows/ebay-contract-tests.yml
name: eBay Contract Tests
on:
  schedule:
    - cron: '0 6 * * 1'  # Weekly Monday 6am
  workflow_dispatch:      # Manual trigger

jobs:
  contract-tests:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Start AIOWebScraper
        run: |
          cd AIOWebScraper/ScraperWorker
          dotnet run -- --dedicated-mode &
          sleep 10
      - name: Run contract tests
        run: dotnet test --filter "Category=Contract"
```

---

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Mock eBay for Tier 1 | HTML snapshots via test server | Deterministic, fast, no rate limits |
| Real eBay for Tier 2 | Direct HTTP calls | Detect HTML structure changes |
| Database | In-memory SQLite per test | Fast, isolated, no cleanup needed |
| AIOWebScraper | Manual startup | Simpler, matches existing pattern |
| Test count | 4 Tier 1 + 2 Tier 2 | Minimal coverage of critical paths |
