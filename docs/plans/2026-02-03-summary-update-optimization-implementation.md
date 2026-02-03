# Summary Update Optimization Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Use search result summary data to update existing active listings in-place, reserving full scrapes for new listings and sold transitions.

**Architecture:** `SearchListings` returns full `EbayProductSummary` records (with new `IsSold` flag). `ClassifyListings` sorts them into two buckets: `ToScrape` (new + sold) and `ToUpdateFromSummary` (existing active). Summary updates happen synchronously in `ScrapeJobProcessor`; scrape items go through the existing `EnqueueScrapeWork` pipeline.

**Tech Stack:** .NET 8.0, EF Core (SQLite in-memory for tests), NUnit 3.14.0, Moq, AngleSharp

**Design doc:** `docs/plans/2026-02-03-summary-update-optimization-design.md`

---

## Behavioral Parity Checklist

### Behaviors to Preserve
- [x] Multi-page search (continues until no results on a page)
- [x] Sold listing search (separate phase before active)
- [x] Filter terminal statuses (Sold/Ended/OutOfStock already in DB) — skip entirely
- [x] New listings get full scrape via `EnqueueScrapeWork`
- [x] `ScrapeRunListing` records created for scraped listings
- [x] `ScrapeRun` counter increments (`ListingsUpdated`, `ListingsSkipped`, `ListingsProcessed`)
- [x] Error handling — `ScrapeRun.Status = "Failed"` on exception, rethrow
- [x] Status progression: `Queued → Searching → Indexing → Completed/Failed`
- [x] Transition detection logging (active-to-sold count)
- [x] `TotalListingsFound` and `ListingsFilteredPreQueue` set on `ScrapeRun`

### New Behaviors
- [ ] `EbayProductSummary` carries `IsSold` flag from parser
- [ ] Existing active listings with `IsSold=false` updated from summary (no scrape)
- [ ] Existing active listings with `IsSold=true` routed to full scrape
- [ ] `ListingStatusHistory` created on price change from summary (Source = "SummaryUpdate")
- [ ] Summary-updated listings increment `ListingsUpdated` or `ListingsSkipped` on `ScrapeRun`

### Behaviors Intentionally Changed
- [ ] `SearchListings` returns `List<EbayProductSummary>` instead of `HashSet<string>`
- [ ] `FilterNewListings` replaced by `ClassifyListings` returning `ClassifiedListings`
- [ ] Existing active listings no longer enqueued for full scrape (updated from summary instead)

---

## Task 1: Add `IsSold` to `EbayProductSummary` and expose from parser

**Files:**
- Modify: `AIOMarketMaker.Core/Models/Ebay/IEbayProductSummary.cs`
- Modify: `AIOMarketMaker.Core/Parsers/EbaySearchParser.cs:42-53`
- Test: `AIOMarketMaker.Tests/UnitTests/SearchParserUnitTests.cs`

**Context:**
- `IEbayProductSummary` extends `IProduct` which has: `ListingId`, `Title`, `Price`, `Currency`, `Url`
- `IEbayProductSummary` adds: `ShippingCost`, `Images`
- `EbayProductSummary` record has all fields above plus `BuyingFormat`, `Condition`, `EndDateUtc`
- The parser already calls `IsSoldListing(li)` at line 41 of `EbaySearchParser.cs` but only uses it to decide whether to extract `EndDateUtc`

**Step 1: Add `IsSold` to the interface and record**

In `AIOMarketMaker.Core/Models/Ebay/IEbayProductSummary.cs`:

```csharp
public interface IEbayProductSummary : IProduct
{
    decimal? ShippingCost { get; init; }
    IEnumerable<string>? Images { get; init; }
    bool IsSold { get; init; }
}

public record EbayProductSummary(
       string? ListingId,
       string? Title,
       decimal? Price,
       string? Currency,
       decimal? ShippingCost,
       string? Url,
       BuyingFormat? BuyingFormat,
       Condition? Condition,
       IEnumerable<string>? Images,
       DateTime? EndDateUtc,
       bool IsSold
) : IEbayProductSummary;
```

**Step 2: Pass `IsSold` in the parser**

In `EbaySearchParser.cs`, the `yield return` block (lines 42-54) becomes:

```csharp
var isSold = IsSoldListing(li);
yield return new EbayProductSummary(
       ListingId: id,
       Title: ExtractTitle(li),
       Price: ExtractPrice(li),
       Currency: ExtractCurrency(li),
       ShippingCost: ExtractShippingCost(li),
       Images: new List<string> { ExtractImageUrl(li) },
       Url: GetListingUrl(li),
       EndDateUtc: isSold ? ExtractDate(li) : null,
       BuyingFormat: ExtractBuyingFormat(li),
       Condition: ExtractCondition(li)!,
       IsSold: isSold
   );
```

Note: `isSold` is already computed at line 41. Just move it above the `yield return` and pass it through.

**Step 3: Fix any tests that construct `EbayProductSummary` directly**

Search all test files for `new EbayProductSummary(` — each needs the new `IsSold` parameter appended. Default to `false` for existing tests unless the test is specifically about sold listings.

**Step 4: Build and run all tests**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj`
Expected: All tests pass (no behavioral changes, just a new field)

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Core/Models/Ebay/IEbayProductSummary.cs AIOMarketMaker/AIOMarketMaker.Core/Parsers/EbaySearchParser.cs
git add -u  # pick up any test fixes
git commit -m "feat: add IsSold flag to EbayProductSummary"
```

---

## Task 2: Change `SearchListings` to return `List<EbayProductSummary>`

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs` (the `SearchListings` method)
- Test: `AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs`

**Context:**
- Current `SearchListings` signature: `private async Task<HashSet<string>> SearchListings(string searchTerm, bool sold, int maxPages)`
- It parses search results into `IEbayProductSummary`, extracts just `ListingId`, adds to a `HashSet<string>`, and returns
- After this change, it returns the full summaries in a `List<EbayProductSummary>` (cast from `IEbayProductSummary`)
- Deduplication still needed — use a `HashSet<string>` internally to track seen IDs, skip duplicates

**Step 1: Write failing test — SearchListings returns summaries**

Add to `ScrapeJobProcessor_UnitTests.cs`:

```csharp
[Test]
public async Task Should_return_product_summaries_from_search()
{
    var scrapeRun = new ScrapeRun
    {
        Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
        TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
        InstanceId = Guid.NewGuid().ToString()
    };
    _dbContext.ScrapeRuns.Add(scrapeRun);
    await _dbContext.SaveChangesAsync();

    // Seed an existing active listing so summary update path is exercised
    _dbContext.Listings.Add(new Listing
    {
        ListingId = "EXISTING1", ScrapeJobId = 1,
        Title = "Old Title", Price = 100m, ListingStatus = "Active",
        Condition = "USED", ShippingCost = 5m
    });
    await _dbContext.SaveChangesAsync();

    var callCount = 0;
    var summary = new EbayProductSummary(
        ListingId: "EXISTING1", Title: "Old Title", Price: 150m,
        Currency: "GBP", ShippingCost: 5m, Url: "https://ebay.co.uk/itm/EXISTING1",
        BuyingFormat: BuyingFormat.BUY_NOW, Condition: Condition.USED,
        Images: new List<string>(), EndDateUtc: null, IsSold: false);

    _searchParserMock
        .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() =>
        {
            callCount++;
            // Call 1: sold page 1 (empty)
            // Call 2: active page 1 (returns listing)
            // Call 3: active page 2 (empty)
            return callCount == 2
                ? new IEbayProductSummary[] { summary }
                : Enumerable.Empty<IEbayProductSummary>();
        });

    var message = new ScrapeJobMessage(1, 1, "Test", "Manual");
    await CreateProcessor().Process(message);

    // Existing active listing with IsSold=false should be updated from summary
    var listing = _dbContext.Listings.First(l => l.ListingId == "EXISTING1");
    Assert.That(listing.Price, Is.EqualTo(150m), "Price should be updated from summary");
}
```

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~Should_return_product_summaries_from_search"`
Expected: FAIL (processor still uses IDs, not summaries)

**Step 2: Change `SearchListings` signature and implementation**

In `ScrapeJobProcessor.cs`, change:

```csharp
private async Task<List<IEbayProductSummary>> SearchListings(string searchTerm, bool sold, int maxPages)
{
    var results = new List<IEbayProductSummary>();
    var seenIds = new HashSet<string>();
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
        var pageResults = products
            .Where(p => !string.IsNullOrEmpty(p.ListingId) && seenIds.Add(p.ListingId!))
            .ToList();

        if (pageResults.Count == 0)
            break;

        results.AddRange(pageResults);
        page++;
    }

    return results;
}
```

Note: `seenIds.Add()` returns `false` if already present, so this deduplicates within a single search.

**Step 3: Update `RunScrape` to use new return types**

This will fail to compile because `RunScrape` calls `FilterNewListings` which expects `HashSet<string>`. For now, change the variable names in `RunScrape` from `soldListingIds`/`activeListingIds` to `soldSummaries`/`activeSummaries` and leave `FilterNewListings` broken — it will be replaced in Task 3.

Temporarily comment out the `FilterNewListings` call and everything after it in `RunScrape`, replacing with `throw new NotImplementedException("Task 3 will implement ClassifyListings");` so the project compiles.

**Step 4: Build to verify compilation**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: Compiles (existing tests may fail — that's expected, will be fixed in Task 3)

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs
git add AIOMarketMaker/AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "refactor: SearchListings returns full EbayProductSummary records"
```

---

## Task 3: Replace `FilterNewListings` with `ClassifyListings`

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`
- Test: `AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs`

**Context:**
- Current `FilterResult` record: `FilterResult(List<string> NewListingIds, int TotalFound, int TerminalCount)`
- Current `FilterNewListings` takes `HashSet<string> activeListingIds, HashSet<string> soldListingIds, int jobId`
- New `ClassifiedListings` record replaces `FilterResult`
- `ClassifyListings` takes `List<IEbayProductSummary> activeSummaries, List<IEbayProductSummary> soldSummaries, int jobId`
- Classification logic:
  1. Merge sold + active summaries. If a listing appears in both, sold wins.
  2. Look up each in DB by `ListingId` + `ScrapeJobId`
  3. If existing listing has terminal status → skip (increment terminal count)
  4. If no existing listing → `ToScrape` (new)
  5. If existing + `IsSold == true` → `ToScrape` (sold transition)
  6. If existing + `IsSold == false` → `ToUpdateFromSummary`

**Step 1: Write failing tests for classification**

Add to `ScrapeJobProcessor_UnitTests.cs`:

```csharp
[Test]
public async Task Should_route_sold_heuristic_listings_to_scrape()
{
    var scrapeRun = new ScrapeRun
    {
        Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
        TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
        InstanceId = Guid.NewGuid().ToString()
    };
    _dbContext.ScrapeRuns.Add(scrapeRun);

    // Existing active listing that now appears as sold in search
    _dbContext.Listings.Add(new Listing
    {
        ListingId = "SOLD_TRANS", ScrapeJobId = 1,
        Title = "Was Active", Price = 100m, ListingStatus = "Active"
    });
    await _dbContext.SaveChangesAsync();

    var callCount = 0;
    var soldSummary = new EbayProductSummary(
        ListingId: "SOLD_TRANS", Title: "Was Active", Price: 100m,
        Currency: "GBP", ShippingCost: 0m, Url: "https://ebay.co.uk/itm/SOLD_TRANS",
        BuyingFormat: BuyingFormat.BUY_NOW, Condition: Condition.USED,
        Images: new List<string>(), EndDateUtc: DateTime.UtcNow, IsSold: true);

    _searchParserMock
        .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() =>
        {
            callCount++;
            // Call 1: sold page 1 (returns sold listing)
            // Call 2: sold page 2 (empty)
            // Call 3: active page 1 (empty)
            return callCount == 1
                ? new IEbayProductSummary[] { soldSummary }
                : Enumerable.Empty<IEbayProductSummary>();
        });

    var message = new ScrapeJobMessage(1, 1, "Test", "Manual");
    await CreateProcessor().Process(message);

    // Sold heuristic listing should be enqueued for full scrape
    _webscraperClientMock.Verify(
        w => w.EnqueueScrapeWork(
            It.Is<IEnumerable<ScrapeWorkItem>>(items =>
                items.Any(i => i.ListingId == "SOLD_TRANS")),
            1, 1, It.IsAny<CancellationToken>()),
        Times.Once);
}

[Test]
public async Task Should_update_existing_active_listing_from_summary_without_scraping()
{
    var scrapeRun = new ScrapeRun
    {
        Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
        TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
        InstanceId = Guid.NewGuid().ToString()
    };
    _dbContext.ScrapeRuns.Add(scrapeRun);

    _dbContext.Listings.Add(new Listing
    {
        ListingId = "ACTIVE1", ScrapeJobId = 1,
        Title = "Active Item", Price = 100m, ListingStatus = "Active",
        Condition = "USED", ShippingCost = 5m
    });
    await _dbContext.SaveChangesAsync();

    var callCount = 0;
    var activeSummary = new EbayProductSummary(
        ListingId: "ACTIVE1", Title: "Active Item", Price: 120m,
        Currency: "GBP", ShippingCost: 3m, Url: "https://ebay.co.uk/itm/ACTIVE1",
        BuyingFormat: BuyingFormat.BUY_NOW, Condition: Condition.USED,
        Images: new List<string>(), EndDateUtc: null, IsSold: false);

    _searchParserMock
        .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() =>
        {
            callCount++;
            // Call 1: sold page 1 (empty)
            // Call 2: active page 1 (returns listing)
            // Call 3: active page 2 (empty)
            return callCount == 2
                ? new IEbayProductSummary[] { activeSummary }
                : Enumerable.Empty<IEbayProductSummary>();
        });

    var message = new ScrapeJobMessage(1, 1, "Test", "Manual");
    await CreateProcessor().Process(message);

    // Should NOT be enqueued for scrape
    _webscraperClientMock.Verify(
        w => w.EnqueueScrapeWork(
            It.IsAny<IEnumerable<ScrapeWorkItem>>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
        Times.Never);

    // Should be updated from summary
    var listing = _dbContext.Listings.First(l => l.ListingId == "ACTIVE1");
    Assert.Multiple(() =>
    {
        Assert.That(listing.Price, Is.EqualTo(120m));
        Assert.That(listing.ShippingCost, Is.EqualTo(3m));
        Assert.That(listing.UpdatedUtc, Is.Not.Null);
    });
}

[Test]
public async Task Should_skip_unchanged_existing_active_listing()
{
    var scrapeRun = new ScrapeRun
    {
        Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
        TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
        InstanceId = Guid.NewGuid().ToString()
    };
    _dbContext.ScrapeRuns.Add(scrapeRun);

    _dbContext.Listings.Add(new Listing
    {
        ListingId = "UNCHANGED1", ScrapeJobId = 1,
        Title = "Same Item", Price = 100m, ListingStatus = "Active",
        Condition = "USED", ShippingCost = 5m, Currency = "GBP"
    });
    await _dbContext.SaveChangesAsync();

    var callCount = 0;
    // Summary with identical values
    var activeSummary = new EbayProductSummary(
        ListingId: "UNCHANGED1", Title: "Same Item", Price: 100m,
        Currency: "GBP", ShippingCost: 5m, Url: "https://ebay.co.uk/itm/UNCHANGED1",
        BuyingFormat: BuyingFormat.BUY_NOW, Condition: Condition.USED,
        Images: new List<string>(), EndDateUtc: null, IsSold: false);

    _searchParserMock
        .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() =>
        {
            callCount++;
            return callCount == 2
                ? new IEbayProductSummary[] { activeSummary }
                : Enumerable.Empty<IEbayProductSummary>();
        });

    var message = new ScrapeJobMessage(1, 1, "Test", "Manual");
    await CreateProcessor().Process(message);

    // Should NOT be enqueued
    _webscraperClientMock.Verify(
        w => w.EnqueueScrapeWork(
            It.IsAny<IEnumerable<ScrapeWorkItem>>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
        Times.Never);

    // UpdatedUtc should NOT be set (nothing changed)
    var listing = _dbContext.Listings.First(l => l.ListingId == "UNCHANGED1");
    Assert.That(listing.UpdatedUtc, Is.Null, "Unchanged listing should not be marked as updated");
}
```

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~Should_route_sold_heuristic|Should_update_existing_active|Should_skip_unchanged"`
Expected: FAIL

**Step 2: Replace `FilterResult` with `ClassifiedListings` and implement `ClassifyListings`**

In `ScrapeJobProcessor.cs`, replace the `FilterResult` record:

```csharp
public record ClassifiedListings(
    List<IEbayProductSummary> ToScrape,
    List<IEbayProductSummary> ToUpdateFromSummary,
    int TotalFound,
    int TerminalCount);
```

Replace `FilterNewListings` with:

```csharp
private async Task<ClassifiedListings> ClassifyListings(
    List<IEbayProductSummary> activeSummaries, List<IEbayProductSummary> soldSummaries, int jobId)
{
    // Merge summaries — sold wins if listing appears in both searches
    var soldIds = soldSummaries
        .Where(s => !string.IsNullOrEmpty(s.ListingId))
        .Select(s => s.ListingId!)
        .ToHashSet();

    var merged = new Dictionary<string, IEbayProductSummary>();
    foreach (var summary in soldSummaries.Concat(activeSummaries))
    {
        if (string.IsNullOrEmpty(summary.ListingId)) continue;
        merged.TryAdd(summary.ListingId, summary);
    }

    var allListingIds = merged.Keys.ToList();

    var transitionCount = await _dbContext.Listings
        .Where(l => l.ScrapeJobId == jobId
                 && soldIds.Contains(l.ListingId)
                 && l.ListingStatus == "Active")
        .CountAsync();

    _logger.LogInformation("Found {Count} listings that transitioned from Active to Sold", transitionCount);

    // Load existing listings for classification
    var existingListings = await _dbContext.Listings
        .Where(l => l.ScrapeJobId == jobId && allListingIds.Contains(l.ListingId))
        .ToDictionaryAsync(l => l.ListingId);

    var toScrape = new List<IEbayProductSummary>();
    var toUpdate = new List<IEbayProductSummary>();
    var terminalCount = 0;

    foreach (var (listingId, summary) in merged)
    {
        if (!existingListings.TryGetValue(listingId, out var existing))
        {
            // New listing — needs full scrape
            toScrape.Add(summary);
        }
        else if (TerminalStatuses.Contains(existing.ListingStatus ?? ""))
        {
            // Terminal — skip entirely
            terminalCount++;
        }
        else if (summary.IsSold)
        {
            // Existing + sold heuristic — important transition, full scrape
            toScrape.Add(summary);
        }
        else
        {
            // Existing + still active — update from summary
            toUpdate.Add(summary);
        }
    }

    _logger.LogInformation(
        "Classified {Total} listings: {ScrapeCount} to scrape, {UpdateCount} to update from summary, {TerminalCount} terminal",
        merged.Count, toScrape.Count, toUpdate.Count, terminalCount);

    return new ClassifiedListings(toScrape, toUpdate, merged.Count, terminalCount);
}
```

**Step 3: Build and run tests**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeJobProcessor"`
Expected: New tests still fail — `RunScrape` not yet wired up

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs
git add AIOMarketMaker/AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "feat: add ClassifyListings with sold heuristic routing"
```

---

## Task 4: Implement `UpdateListingsFromSummary`

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`
- Test: `AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs`

**Context:**
- This method handles the "existing + still active" bucket
- For each summary: compare price, condition, shipping against existing `Listing`
- If changed: update listing fields, create `ListingStatusHistory` (Source = "SummaryUpdate"), increment `ListingsUpdated`
- If unchanged: increment `ListingsSkipped`
- Uses EF Core directly (no raw SQL needed — processor runs sequentially, no concurrency)
- `ListingStatusHistory.ListingId` is the DB `Listing.Id` (int FK), not the eBay listing ID string

**Step 1: Implement `UpdateListingsFromSummary`**

Add to `ScrapeJobProcessor.cs`:

```csharp
private async Task UpdateListingsFromSummary(
    List<IEbayProductSummary> summaries, ScrapeRun scrapeRun, int jobId)
{
    foreach (var summary in summaries)
    {
        var listing = await _dbContext.Listings
            .FirstOrDefaultAsync(l => l.ListingId == summary.ListingId && l.ScrapeJobId == jobId);

        if (listing == null) continue;

        var priceChanged = listing.Price != summary.Price;
        var conditionChanged = listing.Condition != summary.Condition?.ToString();
        var shippingChanged = listing.ShippingCost != summary.ShippingCost;

        if (priceChanged || conditionChanged || shippingChanged)
        {
            listing.Price = summary.Price;
            listing.Condition = summary.Condition?.ToString();
            listing.ShippingCost = summary.ShippingCost;
            listing.UpdatedUtc = DateTime.UtcNow;

            if (priceChanged)
            {
                _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                {
                    ListingId = listing.Id,
                    ListingStatus = listing.ListingStatus ?? "Active",
                    Price = summary.Price,
                    RecordedUtc = DateTime.UtcNow,
                    Source = "SummaryUpdate"
                });
            }

            scrapeRun.ListingsUpdated++;
            _logger.LogInformation("Updated listing {ListingId} from summary (price: {Price}, condition: {Condition})",
                summary.ListingId, summary.Price, summary.Condition);
        }
        else
        {
            scrapeRun.ListingsSkipped++;
        }

        scrapeRun.ListingsProcessed++;
    }

    await _dbContext.SaveChangesAsync();
}
```

**Step 2: Build and verify test setup**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: Compiles

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs
git commit -m "feat: add UpdateListingsFromSummary with price history tracking"
```

---

## Task 5: Wire up `RunScrape` and update `CreateAndEnqueueListings`

**Files:**
- Modify: `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`
- Test: `AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs`

**Context:**
- `RunScrape` needs to call `ClassifyListings` instead of `FilterNewListings`
- Then call `UpdateListingsFromSummary` for the summary batch
- Then call `CreateAndEnqueueListings` for the scrape batch
- `CreateAndEnqueueListings` currently takes `List<string>` — change to take `List<IEbayProductSummary>` and extract IDs internally
- Completion logic: if both `ToScrape` and `ToUpdateFromSummary` are empty → Completed. If only `ToScrape` is empty but summary updates happened → also Completed (no async work pending).

**Step 1: Update `RunScrape`**

```csharp
private async Task RunScrape(ScrapeRun scrapeRun, int jobId, string searchTerm)
{
    const int maxPages = 100;

    var soldSummaries = await SearchListings(searchTerm, sold: true, maxPages);
    _logger.LogInformation("Sold search complete: {Count} unique sold listings", soldSummaries.Count);

    scrapeRun.CurrentPhase = "Searching Active";
    await _dbContext.SaveChangesAsync();

    var activeSummaries = await SearchListings(searchTerm, sold: false, maxPages);
    _logger.LogInformation("Active search complete: {Count} unique active listings", activeSummaries.Count);

    scrapeRun.CurrentPhase = "Classifying";
    await _dbContext.SaveChangesAsync();

    var classified = await ClassifyListings(activeSummaries, soldSummaries, jobId);

    scrapeRun.TotalListingsFound = classified.TotalFound;
    scrapeRun.ListingsFilteredPreQueue = classified.TerminalCount;

    if (classified.ToScrape.Count == 0 && classified.ToUpdateFromSummary.Count == 0)
    {
        scrapeRun.Status = "Completed";
        scrapeRun.CurrentPhase = "Completed";
        scrapeRun.CompletedUtc = DateTime.UtcNow;
        _logger.LogInformation("No new or changed listings found for job {JobId} - marking as completed", jobId);
        await _dbContext.SaveChangesAsync();
        return;
    }

    if (classified.ToUpdateFromSummary.Count > 0)
    {
        scrapeRun.CurrentPhase = "Updating from summary";
        await _dbContext.SaveChangesAsync();

        await UpdateListingsFromSummary(classified.ToUpdateFromSummary, scrapeRun, jobId);
        _logger.LogInformation("Updated {Count} listings from summary data", classified.ToUpdateFromSummary.Count);
    }

    if (classified.ToScrape.Count > 0)
    {
        scrapeRun.Status = "Indexing";
        scrapeRun.CurrentPhase = "Indexing";
        await _dbContext.SaveChangesAsync();

        await CreateAndEnqueueListings(classified.ToScrape, scrapeRun, jobId);
    }
    else
    {
        // Only summary updates, no async scraping needed
        scrapeRun.Status = "Completed";
        scrapeRun.CurrentPhase = "Completed";
        scrapeRun.CompletedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }
}
```

**Step 2: Update `CreateAndEnqueueListings` to take summaries**

```csharp
private async Task CreateAndEnqueueListings(
    List<IEbayProductSummary> summaries, ScrapeRun scrapeRun, int jobId)
{
    foreach (var summary in summaries)
    {
        if (string.IsNullOrEmpty(summary.ListingId)) continue;

        _dbContext.ScrapeRunListings.Add(new ScrapeRunListing
        {
            ScrapeRunId = scrapeRun.Id,
            ScrapeJobId = jobId,
            ListingId = summary.ListingId,
            Status = "Pending",
            CreatedUtc = DateTime.UtcNow
        });
    }
    await _dbContext.SaveChangesAsync();

    var workItems = summaries
        .Where(s => !string.IsNullOrEmpty(s.ListingId))
        .Select(s => new ScrapeWorkItem(
            s.ListingId!,
            _urlBuilder.BuildListingUrl(s.ListingId!),
            _urlBuilder.BuildDescriptionUrl(s.ListingId!)));

    await _webscraperClient.EnqueueScrapeWork(workItems, scrapeRun.Id, jobId);

    _logger.LogInformation("Enqueued {Count} listings for processing. Search phase complete for job {JobId}.",
        summaries.Count, jobId);
}
```

**Step 3: Update existing tests**

The existing test `Should_skip_terminal_listings_but_rescrape_active` needs updating. With the new logic, an existing active listing with `IsSold=false` will now be routed to summary update, not to `EnqueueScrapeWork`. Update the test:

```csharp
[Test]
public async Task Should_skip_terminal_listings_and_update_active_from_summary()
{
    var scrapeRun = new ScrapeRun
    {
        Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
        TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
        InstanceId = Guid.NewGuid().ToString()
    };
    _dbContext.ScrapeRuns.Add(scrapeRun);

    _dbContext.Listings.Add(new Listing
    {
        ListingId = "ACTIVE1", ScrapeJobId = 1,
        Title = "Active Item", Price = 100m, ListingStatus = "Active"
    });
    _dbContext.Listings.Add(new Listing
    {
        ListingId = "SOLD1", ScrapeJobId = 1,
        Title = "Sold Item", ListingStatus = "Sold"
    });
    await _dbContext.SaveChangesAsync();

    var callCount = 0;
    var activeSummary = new EbayProductSummary(
        ListingId: "ACTIVE1", Title: "Active Item", Price: 100m,
        Currency: "GBP", ShippingCost: 0m, Url: "https://ebay.co.uk/itm/ACTIVE1",
        BuyingFormat: BuyingFormat.BUY_NOW, Condition: Condition.USED,
        Images: new List<string>(), EndDateUtc: null, IsSold: false);
    var soldSummary = new EbayProductSummary(
        ListingId: "SOLD1", Title: "Sold Item", Price: 90m,
        Currency: "GBP", ShippingCost: 0m, Url: "https://ebay.co.uk/itm/SOLD1",
        BuyingFormat: BuyingFormat.BUY_NOW, Condition: Condition.USED,
        Images: new List<string>(), EndDateUtc: null, IsSold: false);

    _searchParserMock
        .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() =>
        {
            callCount++;
            return callCount == 2
                ? new IEbayProductSummary[] { activeSummary, soldSummary }
                : Enumerable.Empty<IEbayProductSummary>();
        });

    var message = new ScrapeJobMessage(1, 1, "Test", "Manual");
    await CreateProcessor().Process(message);

    var run = await _dbContext.ScrapeRuns.FindAsync(1);
    Assert.Multiple(() =>
    {
        Assert.That(run!.TotalListingsFound, Is.EqualTo(2));
        Assert.That(run.ListingsFilteredPreQueue, Is.EqualTo(1),
            "Sold listing should be filtered as terminal");
    });

    // Active listing should NOT be enqueued (updated from summary instead)
    _webscraperClientMock.Verify(
        w => w.EnqueueScrapeWork(
            It.IsAny<IEnumerable<ScrapeWorkItem>>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
        Times.Never);
}
```

Also update `Should_enqueue_via_webscraper_client_for_new_listings` — the mock `IEbayProductSummary` needs `IsSold` setup:

```csharp
mockSummary.Setup(s => s.IsSold).Returns(false);
```

And the test assertion remains valid because the listing is new (not in DB), so it still routes to scrape.

**Step 4: Run all processor tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~ScrapeJobProcessor"`
Expected: All tests pass

**Step 5: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs
git add AIOMarketMaker/AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "feat: wire up summary update path in RunScrape"
```

---

## Task 6: Add `ListingStatusHistory` test for summary price change

**Files:**
- Test: `AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs`

**Context:**
- When a summary update detects a price change, `UpdateListingsFromSummary` should create a `ListingStatusHistory` record with `Source = "SummaryUpdate"`
- This test verifies the history record is created correctly

**Step 1: Write the test**

Add to `ScrapeJobProcessor_UnitTests.cs`:

```csharp
[Test]
public async Task Should_create_status_history_when_summary_price_changes()
{
    var scrapeRun = new ScrapeRun
    {
        Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
        TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
        InstanceId = Guid.NewGuid().ToString()
    };
    _dbContext.ScrapeRuns.Add(scrapeRun);

    _dbContext.Listings.Add(new Listing
    {
        ListingId = "PRICE_CHG", ScrapeJobId = 1,
        Title = "Price Change Item", Price = 100m, ListingStatus = "Active",
        Condition = "USED", ShippingCost = 5m
    });
    await _dbContext.SaveChangesAsync();

    var callCount = 0;
    var summary = new EbayProductSummary(
        ListingId: "PRICE_CHG", Title: "Price Change Item", Price: 85m,
        Currency: "GBP", ShippingCost: 5m, Url: "https://ebay.co.uk/itm/PRICE_CHG",
        BuyingFormat: BuyingFormat.BUY_NOW, Condition: Condition.USED,
        Images: new List<string>(), EndDateUtc: null, IsSold: false);

    _searchParserMock
        .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() =>
        {
            callCount++;
            return callCount == 2
                ? new IEbayProductSummary[] { summary }
                : Enumerable.Empty<IEbayProductSummary>();
        });

    var message = new ScrapeJobMessage(1, 1, "Test", "Manual");
    await CreateProcessor().Process(message);

    var listing = _dbContext.Listings.First(l => l.ListingId == "PRICE_CHG");
    var history = _dbContext.ListingStatusHistory
        .Where(h => h.ListingId == listing.Id)
        .ToList();

    Assert.Multiple(() =>
    {
        Assert.That(history, Has.Count.EqualTo(1));
        Assert.That(history[0].Source, Is.EqualTo("SummaryUpdate"));
        Assert.That(history[0].Price, Is.EqualTo(85m));
        Assert.That(history[0].ListingStatus, Is.EqualTo("Active"));
    });
}

[Test]
public async Task Should_not_create_status_history_when_summary_unchanged()
{
    var scrapeRun = new ScrapeRun
    {
        Id = 1, JobId = 1, Status = "Queued", CurrentPhase = "Queued",
        TriggerType = "Manual", StartedUtc = DateTime.UtcNow,
        InstanceId = Guid.NewGuid().ToString()
    };
    _dbContext.ScrapeRuns.Add(scrapeRun);

    _dbContext.Listings.Add(new Listing
    {
        ListingId = "NO_CHG", ScrapeJobId = 1,
        Title = "No Change", Price = 100m, ListingStatus = "Active",
        Condition = "USED", ShippingCost = 5m
    });
    await _dbContext.SaveChangesAsync();

    var callCount = 0;
    var summary = new EbayProductSummary(
        ListingId: "NO_CHG", Title: "No Change", Price: 100m,
        Currency: "GBP", ShippingCost: 5m, Url: "https://ebay.co.uk/itm/NO_CHG",
        BuyingFormat: BuyingFormat.BUY_NOW, Condition: Condition.USED,
        Images: new List<string>(), EndDateUtc: null, IsSold: false);

    _searchParserMock
        .Setup(p => p.ParseSearchResults(It.IsAny<IDocument>()))
        .Returns(() =>
        {
            callCount++;
            return callCount == 2
                ? new IEbayProductSummary[] { summary }
                : Enumerable.Empty<IEbayProductSummary>();
        });

    var message = new ScrapeJobMessage(1, 1, "Test", "Manual");
    await CreateProcessor().Process(message);

    var listing = _dbContext.Listings.First(l => l.ListingId == "NO_CHG");
    var history = _dbContext.ListingStatusHistory
        .Where(h => h.ListingId == listing.Id)
        .ToList();

    Assert.That(history, Is.Empty, "No history record when nothing changed");
}
```

**Step 2: Run the tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj --filter "FullyQualifiedName~Should_create_status_history|Should_not_create_status_history"`
Expected: PASS (implementation already done in Task 4)

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Tests/Unit/Services/ScrapeJobProcessor_UnitTests.cs
git commit -m "test: add ListingStatusHistory tests for summary updates"
```

---

## Task 7: Final verification — build, all tests, no regressions

**Files:** None (verification only)

**Step 1: Build entire solution**

Run: `dotnet build AIOMarketMaker/AIOMarketMaker.sln`
Expected: 0 errors

**Step 2: Run all tests**

Run: `dotnet test AIOMarketMaker/AIOMarketMaker.Tests/AIOMarketMaker.Tests.csproj`
Expected: All tests pass (note: 2 pre-existing failures in `ScrapeJobsApi_GetHistoryIssues_Tests` are unrelated)

**Step 3: Verify no WebScraper imports in processor**

Run: `grep -r "ScraperWorker" AIOMarketMaker/AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs`
Expected: No matches

**Step 4: Review the final processor file**

Read `AIOMarketMaker.Etl/Services/ScrapeJobProcessor.cs` and verify:
- `SearchListings` returns `List<IEbayProductSummary>`
- `ClassifyListings` produces `ClassifiedListings` with `ToScrape` and `ToUpdateFromSummary`
- `UpdateListingsFromSummary` handles price/condition/shipping comparison and history
- `CreateAndEnqueueListings` takes `List<IEbayProductSummary>`
- `RunScrape` reads like a clean summary of the pipeline
